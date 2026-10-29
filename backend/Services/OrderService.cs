using Inventory.Api.Contracts;
using Inventory.Api.Data;
using Inventory.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Services;

/// <summary>
/// Order placement, cancellation and restocking. Every stock change is made with a conditional or
/// relative UPDATE inside a database transaction, so correctness does not depend on in-process state
/// and holds across any number of API instances. Notifications are published only after commit.
/// </summary>
public class OrderService(AppDbContext db, IStockNotifier notifier, ILogger<OrderService> logger)
{
    private record StockChange(int ProductId, string ProductName, int PreviousStock, int NewStock, int ReorderThreshold);

    public async Task<ServiceResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        var validation = ValidateOrder(request);
        if (validation is not null) return validation;

        var order = new Order { OrderDate = DateTime.UtcNow, Status = OrderStatus.Placed };
        var changes = new List<StockChange>();

        // Deductions and the order insert commit or roll back together. Disposing the transaction
        // without committing (any early return or exception) rolls back every deduction made so far.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        foreach (var line in request.Lines)
        {
            // Check-and-deduct is a single conditional UPDATE. SQL Server takes the row's update lock
            // before evaluating "Stock >= @quantity", so concurrent requests on any API instance are
            // serialized on the row and a request can only deduct stock that is still there.
            var deducted = await db.Products
                .Where(p => p.Id == line.ProductId && p.Stock >= line.Quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock - line.Quantity), ct);

            if (deducted == 0)
            {
                return await db.Products.AnyAsync(p => p.Id == line.ProductId, ct)
                    ? new ServiceResult.Conflict($"Insufficient stock for product {line.ProductId}.")
                    : new ServiceResult.NotFound($"Product {line.ProductId} not found.");
            }

            // The row stays exclusively locked by this transaction, so this is exactly our post-deduction value.
            var product = await db.Products
                .Where(p => p.Id == line.ProductId)
                .Select(p => new { p.Id, p.Name, p.UnitPrice, p.Stock, p.ReorderThreshold })
                .SingleAsync(ct);

            order.Lines.Add(new OrderLine
            {
                ProductId = product.Id,
                Quantity = line.Quantity,
                UnitPrice = product.UnitPrice
            });

            changes.Add(new StockChange(
                product.Id, product.Name, product.Stock + line.Quantity, product.Stock, product.ReorderThreshold));
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await PublishAsync(changes, $"order {order.Id}");
        return new ServiceResult.Ok<int>(order.Id);
    }

    public async Task<ServiceResult> RestockAsync(int productId, RestockRequest request, CancellationToken ct = default)
    {
        if (request.Quantity <= 0)
            return new ServiceResult.Invalid("Quantity must be greater than zero.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var updated = await db.Products
            .Where(p => p.Id == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock + request.Quantity), ct);

        if (updated == 0)
            return new ServiceResult.NotFound($"Product {productId} not found.");

        var product = await db.Products
            .Where(p => p.Id == productId)
            .Select(p => new { p.Id, p.Name, p.Stock, p.ReorderThreshold })
            .SingleAsync(ct);

        await transaction.CommitAsync(ct);

        await PublishAsync(
            [new StockChange(product.Id, product.Name, product.Stock - request.Quantity, product.Stock, product.ReorderThreshold)],
            $"restock of product {productId}");
        return new ServiceResult.Ok();
    }

    public async Task<ServiceResult> CancelOrderAsync(int orderId, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Claim the cancellation atomically so two concurrent cancels cannot both return the stock.
        var cancelled = await db.Orders
            .Where(o => o.Id == orderId && o.Status != OrderStatus.Cancelled)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Cancelled), ct);

        if (cancelled == 0)
        {
            return await db.Orders.AnyAsync(o => o.Id == orderId, ct)
                ? new ServiceResult.Conflict($"Order {orderId} is already cancelled.")
                : new ServiceResult.NotFound($"Order {orderId} not found.");
        }

        var lines = await db.OrderLines
            .Where(l => l.OrderId == orderId)
            .Select(l => new { l.ProductId, l.Quantity })
            .ToListAsync(ct);

        var changes = new List<StockChange>();
        foreach (var line in lines)
        {
            await db.Products
                .Where(p => p.Id == line.ProductId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock + line.Quantity), ct);

            var product = await db.Products
                .Where(p => p.Id == line.ProductId)
                .Select(p => new { p.Id, p.Name, p.Stock, p.ReorderThreshold })
                .SingleAsync(ct);

            changes.Add(new StockChange(product.Id, product.Name, product.Stock - line.Quantity, product.Stock, product.ReorderThreshold));
        }

        await transaction.CommitAsync(ct);

        await PublishAsync(changes, $"cancellation of order {orderId}");
        return new ServiceResult.Ok();
    }

    /// <summary>
    /// Best-effort, post-commit notification. A publish failure is logged and never turns a committed
    /// operation into an error response (a client retry would then duplicate the order).
    /// </summary>
    private async Task PublishAsync(IEnumerable<StockChange> changes, string operation)
    {
        try
        {
            foreach (var change in changes)
            {
                await notifier.StockUpdatedAsync(change.ProductId, change.NewStock, CancellationToken.None);
                if (IsLowStockTransition(change))
                    await notifier.LowStockAlertAsync(
                        change.ProductId, change.ProductName, change.NewStock, change.ReorderThreshold, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Committed {Operation} but stock notifications could not be published.", operation);
        }
    }

    /// <summary>Stock moved from at-or-above the reorder threshold to strictly below it.</summary>
    private static bool IsLowStockTransition(StockChange change)
        => change.PreviousStock >= change.ReorderThreshold && change.NewStock < change.ReorderThreshold;

    private static ServiceResult? ValidateOrder(PlaceOrderRequest request)
    {
        if (request.Lines.Count == 0)
            return new ServiceResult.Invalid("Order must contain at least one line.");

        if (request.Lines.Any(l => l.Quantity <= 0))
            return new ServiceResult.Invalid("All line quantities must be greater than zero.");

        return null;
    }
}
