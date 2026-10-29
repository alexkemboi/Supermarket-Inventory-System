using System.Net;
using System.Net.Http.Json;
using Inventory.Api.Tests.Infrastructure;

namespace Inventory.Api.Tests;

/// <summary>
/// Threshold semantics: an alert is raised only by the order that moves stock from at-or-above the
/// reorder threshold to strictly below it.
/// </summary>
public class LowStockAlertTests : IClassFixture<SqlServerDatabase>
{
    private readonly SqlServerDatabase db;
    private readonly RecordingStockNotifier notifier = new();
    private readonly HttpClient client;

    public LowStockAlertTests(SqlServerDatabase db)
    {
        this.db = db;
        client = db.CreateFactory(notifier.Register).CreateClient();
    }

    [Fact]
    public async Task Stock_remaining_above_threshold_raises_no_alert()
    {
        var productId = await db.CreateProductAsync(stock: 20, reorderThreshold: 15);

        await client.PlaceOrderAsync(productId, 2);

        Assert.Empty(AlertsFor(productId));
        Assert.Contains(new StockUpdatedEvent(productId, 18, null), notifier.StockUpdates);
    }

    [Fact]
    public async Task Stock_reaching_the_threshold_exactly_raises_no_alert()
    {
        var productId = await db.CreateProductAsync(stock: 20, reorderThreshold: 15);

        await client.PlaceOrderAsync(productId, 5);

        Assert.Equal(15, await db.GetStockAsync(productId));
        Assert.Empty(AlertsFor(productId));
    }

    [Fact]
    public async Task Stock_falling_below_the_threshold_raises_one_alert_with_current_values()
    {
        var productId = await db.CreateProductAsync(stock: 16, reorderThreshold: 15);

        await client.PlaceOrderAsync(productId, 3);

        var alert = Assert.Single(AlertsFor(productId));
        Assert.Equal(13, alert.Stock);
        Assert.Equal(15, alert.ReorderThreshold);
        Assert.StartsWith("Test product", alert.ProductName);
    }

    [Fact]
    public async Task Orders_while_already_below_threshold_do_not_repeat_the_alert()
    {
        var productId = await db.CreateProductAsync(stock: 15, reorderThreshold: 15);

        await client.PlaceOrderAsync(productId, 1); // 15 -> 14: crosses
        await client.PlaceOrderAsync(productId, 1); // 14 -> 13: already below

        Assert.Equal(14, Assert.Single(AlertsFor(productId)).Stock);
    }

    [Fact]
    public async Task Restocking_above_the_threshold_re_arms_the_alert()
    {
        var productId = await db.CreateProductAsync(stock: 15, reorderThreshold: 15);

        await client.PlaceOrderAsync(productId, 1);                                              // 15 -> 14
        await client.PostAsJsonAsync($"/api/products/{productId}/restock", new { quantity = 10 }); // 14 -> 24
        await client.PlaceOrderAsync(productId, 12);                                             // 24 -> 12

        Assert.Equal([14, 12], AlertsFor(productId).Select(a => a.Stock));
    }

    [Fact]
    public async Task Failed_order_raises_no_alert_and_no_stock_update()
    {
        var productId = await db.CreateProductAsync(stock: 16, reorderThreshold: 15);

        var response = await client.PlaceOrderAsync(productId, 50);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(AlertsFor(productId));
        Assert.DoesNotContain(notifier.StockUpdates, u => u.ProductId == productId);
    }

    [Fact]
    public async Task Rolled_back_multi_line_order_raises_no_alert_for_the_earlier_line()
    {
        var crossing = await db.CreateProductAsync(stock: 16, reorderThreshold: 15);
        var soldOut = await db.CreateProductAsync(stock: 0);

        var response = await client.PlaceOrderAsync((crossing, 5), (soldOut, 1));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(AlertsFor(crossing));
        Assert.DoesNotContain(notifier.StockUpdates, u => u.ProductId == crossing);
    }

    /// <summary>Concurrent orders that collectively cross the threshold produce exactly one alert.</summary>
    [Fact]
    public async Task Concurrent_orders_crossing_the_threshold_raise_exactly_one_alert()
    {
        var productId = await db.CreateProductAsync(stock: 20, reorderThreshold: 15);
        using var gate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            return await client.PlaceOrderAsync(productId, 1);
        })).ToList();
        gate.Set();
        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(10, await db.GetStockAsync(productId));
        Assert.Equal(14, Assert.Single(AlertsFor(productId)).Stock);
        // Every published stock value is distinct: no two orders observed the same "previous" stock.
        var published = notifier.StockUpdates.Where(u => u.ProductId == productId).Select(u => u.Stock).ToList();
        Assert.Equal(Enumerable.Range(10, 10).Reverse(), published.OrderDescending());
    }

    private List<LowStockAlertEvent> AlertsFor(int productId)
        => notifier.LowStockAlerts.Where(a => a.ProductId == productId).ToList();
}
