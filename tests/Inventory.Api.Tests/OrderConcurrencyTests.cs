using System.Diagnostics;
using System.Net;
using Inventory.Api.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace Inventory.Api.Tests;

/// <summary>
/// Inventory integrity under concurrency, exercised against real SQL Server through two independent
/// API hosts ("instances") that share one database.
/// </summary>
public class OrderConcurrencyTests(SqlServerDatabase db, ITestOutputHelper output) : IClassFixture<SqlServerDatabase>
{
    private HttpClient InstanceA => db.Primary.CreateClient();
    private readonly Lazy<InventoryApiFactory> instanceB = new(() => db.CreateFactory());
    private HttpClient InstanceB => instanceB.Value.CreateClient();

    [Fact]
    public async Task Ordering_less_than_available_stock_deducts_exactly_the_ordered_quantity()
    {
        var productId = await db.CreateProductAsync(stock: 10);

        var response = await InstanceA.PlaceOrderAsync(productId, 3);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(7, await db.GetStockAsync(productId));
        Assert.Equal(3, await db.OrderedQuantityAsync(productId));
    }

    [Fact]
    public async Task Ordering_exactly_the_available_stock_succeeds_and_leaves_zero()
    {
        var productId = await db.CreateProductAsync(stock: 4);

        var response = await InstanceA.PlaceOrderAsync(productId, 4);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await db.GetStockAsync(productId));
    }

    [Fact]
    public async Task Ordering_more_than_available_stock_is_rejected_without_side_effects()
    {
        var productId = await db.CreateProductAsync(stock: 4);

        var response = await InstanceA.PlaceOrderAsync(productId, 5);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal($"Insufficient stock for product {productId}.", await response.ErrorAsync());
        Assert.Equal(4, await db.GetStockAsync(productId));
        Assert.Equal(0, await db.OrderLineCountAsync(productId));
    }

    /// <summary>
    /// Deterministic last-unit race. A test transaction holds an exclusive lock on the product row
    /// while one request is sent to each API instance. Once SQL Server reports both requests blocked
    /// on that lock, the lock is released, so both compete for the last unit at the same instant.
    /// </summary>
    [Fact]
    public async Task Two_instances_racing_for_the_last_unit_cannot_both_succeed()
    {
        var productId = await db.CreateProductAsync(stock: 1);
        var clientA = InstanceA;
        var clientB = InstanceB;
        // Warm both hosts first so neither request is still running startup work when the lock is held.
        await clientA.GetAsync("/health");
        await clientB.GetAsync("/health");

        await using var blocker = new SqlConnection(db.ConnectionString);
        await blocker.OpenAsync();
        var tx = (SqlTransaction)await blocker.BeginTransactionAsync();
        short blockerSession;
        await using (var lockRow = new SqlCommand(
            "UPDATE Products SET Stock = Stock WHERE Id = @id; SELECT CAST(@@SPID AS smallint);", blocker, tx))
        {
            lockRow.Parameters.AddWithValue("@id", productId);
            blockerSession = (short)(await lockRow.ExecuteScalarAsync())!;
        }

        var orderA = clientA.PlaceOrderAsync(productId, 1);
        var orderB = clientB.PlaceOrderAsync(productId, 1);
        await WaitForBlockedRequestsAsync(blockerSession, expected: 2);
        await tx.CommitAsync();

        var statuses = (await Task.WhenAll(orderA, orderB)).Select(r => r.StatusCode).ToList();
        output.WriteLine($"Last-unit race results: {string.Join(", ", statuses)}");

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(0, await db.GetStockAsync(productId));
        Assert.Equal(1, await db.OrderedQuantityAsync(productId));
        Assert.Equal(1, await db.OrderLineCountAsync(productId));
    }

    [Fact]
    public async Task Many_concurrent_orders_across_two_instances_never_exceed_stock()
    {
        const int initialStock = 10;
        const int requests = 40;
        var productId = await db.CreateProductAsync(stock: initialStock);
        var clients = new[] { InstanceA, InstanceB };
        using var gate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, requests).Select(i => Task.Run(async () =>
        {
            gate.Wait();
            return await clients[i % 2].PlaceOrderAsync(productId, 1);
        })).ToList();
        gate.Set();
        var statuses = (await Task.WhenAll(tasks)).Select(r => r.StatusCode).ToList();

        var succeeded = statuses.Count(s => s == HttpStatusCode.OK);
        var finalStock = await db.GetStockAsync(productId);
        var ordered = await db.OrderedQuantityAsync(productId);
        output.WriteLine($"{requests} requests: {succeeded} OK, {statuses.Count(s => s == HttpStatusCode.Conflict)} Conflict, " +
                         $"other {statuses.Count(s => s is not HttpStatusCode.OK and not HttpStatusCode.Conflict)}; " +
                         $"final stock {finalStock}; units in order lines {ordered}");

        Assert.Equal(initialStock, succeeded);
        Assert.Equal(requests - initialStock, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(0, finalStock);
        Assert.Equal(initialStock, ordered);
    }

    [Fact]
    public async Task Concurrent_orders_of_mixed_quantities_keep_stock_and_order_lines_consistent()
    {
        const int initialStock = 25;
        var productId = await db.CreateProductAsync(stock: initialStock);
        var clients = new[] { InstanceA, InstanceB };
        var quantities = Enumerable.Range(0, 30).Select(i => 1 + i % 4).ToArray();
        using var gate = new ManualResetEventSlim(false);

        var tasks = quantities.Select((quantity, i) => Task.Run(async () =>
        {
            gate.Wait();
            var response = await clients[i % 2].PlaceOrderAsync(productId, quantity);
            return (quantity, response.StatusCode);
        })).ToList();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        var soldUnits = results.Where(r => r.StatusCode == HttpStatusCode.OK).Sum(r => r.quantity);
        var finalStock = await db.GetStockAsync(productId);

        Assert.All(results, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.True(finalStock >= 0);
        Assert.Equal(initialStock - soldUnits, finalStock);
        Assert.Equal(soldUnits, await db.OrderedQuantityAsync(productId));
        // Every rejected request must have been unable to fit in what was left.
        Assert.All(results.Where(r => r.StatusCode == HttpStatusCode.Conflict), r => Assert.True(r.quantity > finalStock));
    }

    [Fact]
    public async Task Failure_on_a_later_line_rolls_back_earlier_deductions()
    {
        var available = await db.CreateProductAsync(stock: 5);
        var soldOut = await db.CreateProductAsync(stock: 0);

        var response = await InstanceA.PlaceOrderAsync((available, 2), (soldOut, 1));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(5, await db.GetStockAsync(available));
        Assert.Equal(0, await db.OrderLineCountAsync(available));
        Assert.Equal(0, await db.OrdersWithoutLinesAsync());
    }

    [Fact]
    public async Task Unknown_product_on_a_later_line_rolls_back_earlier_deductions()
    {
        var available = await db.CreateProductAsync(stock: 5);

        var response = await InstanceA.PlaceOrderAsync((available, 2), (int.MaxValue, 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(5, await db.GetStockAsync(available));
        Assert.Equal(0, await db.OrderLineCountAsync(available));
    }

    /// <summary>
    /// A committed order must be reported as a success even if the real-time notification fails;
    /// otherwise the client sees an error for an order that exists and a retry would duplicate it.
    /// </summary>
    [Fact]
    public async Task Notification_failure_after_commit_does_not_report_a_committed_order_as_failed()
    {
        var notifier = new RecordingStockNotifier { ThrowOnPublish = true };
        var client = db.CreateFactory(notifier.Register).CreateClient();
        var productId = await db.CreateProductAsync(stock: 5);

        var response = await client.PlaceOrderAsync(productId, 2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, await db.GetStockAsync(productId));
        Assert.Equal(1, await db.OrderLineCountAsync(productId));
    }

    [Fact]
    public async Task Each_accepted_submission_deducts_stock_exactly_once()
    {
        var productId = await db.CreateProductAsync(stock: 10);

        var first = await InstanceA.PlaceOrderAsync(productId, 2);
        var second = await InstanceB.PlaceOrderAsync(productId, 2);

        Assert.NotEqual(await first.OrderIdAsync(), await second.OrderIdAsync());
        Assert.Equal(6, await db.GetStockAsync(productId));
        Assert.Equal(2, await db.OrderLineCountAsync(productId));
    }

    [Fact]
    public async Task Concurrent_cancellations_return_stock_only_once()
    {
        var productId = await db.CreateProductAsync(stock: 10);
        var orderId = await (await InstanceA.PlaceOrderAsync(productId, 4)).OrderIdAsync();
        var clients = new[] { InstanceA, InstanceB };
        using var gate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            gate.Wait();
            return await clients[i % 2].PostAsync($"/api/orders/{orderId}/cancel", null);
        })).ToList();
        gate.Set();
        var statuses = (await Task.WhenAll(tasks)).Select(r => r.StatusCode).ToList();

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.NoContent));
        Assert.Equal(9, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(10, await db.GetStockAsync(productId));
    }

    [Fact]
    public async Task Database_rejects_negative_stock_even_from_a_raw_update()
    {
        var productId = await db.CreateProductAsync(stock: 1);
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("UPDATE Products SET Stock = -1 WHERE Id = @id", connection);
        command.Parameters.AddWithValue("@id", productId);

        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());

        Assert.Contains("CK_Products_Stock_NonNegative", error.Message);
        Assert.Equal(1, await db.GetStockAsync(productId));
    }

    private async Task WaitForBlockedRequestsAsync(short blockingSession, int expected)
    {
        await using var monitor = new SqlConnection(db.ConnectionString);
        await monitor.OpenAsync();
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            // Waiters queue behind each other, so the second request may report the first as its blocker.
            await using var command = new SqlCommand(
                """
                SELECT COUNT(*) FROM sys.dm_exec_requests
                WHERE database_id = DB_ID() AND blocking_session_id <> 0 AND session_id <> @session
                """, monitor);
            command.Parameters.AddWithValue("@session", blockingSession);
            var blocked = (int)(await command.ExecuteScalarAsync())!;
            if (blocked >= expected) return;
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException($"Only {blocked} of {expected} requests reached the locked row.");
            await Task.Delay(20);
        }
    }
}
