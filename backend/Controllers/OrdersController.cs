using Inventory.Api.Contracts;
using Inventory.Api.Data;
using Inventory.Api.Models;
using Inventory.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController(AppDbContext db, IStockNotifier notifier, ILogger<OrdersController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Place(PlaceOrderRequest request, CancellationToken ct)
    {
        if (request.Lines.Count == 0)
            return BadRequest(new { error = "Order must contain at least one line." });

        if (request.Lines.Any(line => line.Quantity <= 0))
            return BadRequest(new { error = "All line quantities must be greater than zero." });

        var order = new Order { OrderDate = DateTime.UtcNow, Status = OrderStatus.Placed };
        var stockChanges = new List<(int Id, string Name, int Previous, int Current, int Threshold)>();

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
                .ExecuteUpdateAsync(update => update.SetProperty(p => p.Stock, p => p.Stock - line.Quantity), ct);

            if (deducted == 0)
            {
                return await db.Products.AnyAsync(p => p.Id == line.ProductId, ct)
                    ? Conflict(new { error = $"Insufficient stock for product {line.ProductId}." })
                    : NotFound(new { error = $"Product {line.ProductId} not found." });
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
            stockChanges.Add((product.Id, product.Name, product.Stock + line.Quantity, product.Stock, product.ReorderThreshold));
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Notifications are best-effort and only sent after commit: a publish failure must not turn a
        // committed order into an error response (the client would retry and create a duplicate order).
        try
        {
            foreach (var change in stockChanges)
            {
                await notifier.StockUpdatedAsync(change.Id, change.Current, CancellationToken.None, request.ConnectionId);
                if (change.Previous >= change.Threshold && change.Current < change.Threshold)
                    await notifier.LowStockAlertAsync(change.Id, change.Name, change.Current, change.Threshold, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Order {OrderId} committed but stock notifications could not be published.", order.Id);
        }

        return Ok(new { id = order.Id });
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return NotFound(new { error = $"Order {id} not found." });
        if (order.Status == OrderStatus.Cancelled)
            return Conflict(new { error = $"Order {id} is already cancelled." });

        order.Status = OrderStatus.Cancelled;
        foreach (var line in order.Lines)
        {
            await db.Products
                .Where(p => p.Id == line.ProductId)
                .ExecuteUpdateAsync(update => update.SetProperty(p => p.Stock, p => p.Stock + line.Quantity), ct);
            var stock = await db.Products.Where(p => p.Id == line.ProductId).Select(p => p.Stock).SingleAsync(ct);
            await notifier.StockUpdatedAsync(line.ProductId, stock, ct);
        }

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("/api/products/{productId:int}/restock")]
    public async Task<IActionResult> Restock(int productId, RestockRequest request, CancellationToken ct)
    {
        if (request.Quantity <= 0)
            return BadRequest(new { error = "Quantity must be greater than zero." });

        var product = await db.Products
            .Where(p => p.Id == productId)
            .Select(p => new { p.Id, p.Stock })
            .SingleOrDefaultAsync(ct);
        if (product is null) return NotFound(new { error = $"Product {productId} not found." });

        await db.Products
            .Where(p => p.Id == productId)
            .ExecuteUpdateAsync(update => update.SetProperty(p => p.Stock, p => p.Stock + request.Quantity), ct);
        await notifier.StockUpdatedAsync(productId, product.Stock + request.Quantity, ct);
        return NoContent();
    }

    [HttpGet("recent")]
    public async Task<ActionResult<IReadOnlyList<OrderDto>>> Recent(
        [FromQuery] int status = 0,
        [FromQuery] int days = 7,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        take = Math.Clamp(take, 1, 200);

        var orders = await db.Orders
            .Where(o => (int)o.Status == status && o.OrderDate >= since)
            .OrderByDescending(o => o.OrderDate)
            .Take(take)
            .Select(o => new OrderDto(
                o.Id,
                o.OrderDate,
                o.Status.ToString(),
                o.Lines.Sum(l => l.Quantity * l.UnitPrice),
                o.Lines.Select(l => new OrderLineDto(l.ProductId, l.Product.Name, l.Quantity, l.UnitPrice)).ToList()))
            .ToListAsync(ct);

        return orders;
    }
}
