using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Inventory.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Inventory.Api.Tests;

/// <summary>
/// End-to-end SignalR delivery through the real hub (/hubs/stock): two connected clients play the
/// role of two browsers, exactly as the Angular StockRealtimeService connects and joins the group.
/// </summary>
public class StockHubTests(SqlServerDatabase db) : IClassFixture<SqlServerDatabase>, IAsyncLifetime
{
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(1);

    private BrowserClient browserA = null!;
    private BrowserClient browserB = null!;

    public async Task InitializeAsync()
    {
        browserA = await BrowserClient.ConnectAsync(db.Primary);
        browserB = await BrowserClient.ConnectAsync(db.Primary);
    }

    public async Task DisposeAsync()
    {
        await browserA.DisposeAsync();
        await browserB.DisposeAsync();
    }

    [Fact]
    public async Task Order_placed_by_one_browser_updates_stock_in_the_other_browser()
    {
        var productId = await db.CreateProductAsync(stock: 40);

        // The inventory page always sends its own SignalR connection id with the order.
        var response = await db.Primary.CreateClient().PlaceOrderAsync(productId, 3, browserA.ConnectionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(37, await browserB.NextStockAsync(productId, Delivery));
        Assert.Equal(37, await browserA.NextStockAsync(productId, Delivery));
    }

    [Fact]
    public async Task Restock_updates_stock_in_every_browser()
    {
        var productId = await db.CreateProductAsync(stock: 40);

        var response = await db.Primary.CreateClient()
            .PostAsJsonAsync($"/api/products/{productId}/restock", new { quantity = 5 });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(45, await browserA.NextStockAsync(productId, Delivery));
        Assert.Equal(45, await browserB.NextStockAsync(productId, Delivery));
    }

    [Fact]
    public async Task Rejected_order_publishes_no_stock_update_or_alert()
    {
        var productId = await db.CreateProductAsync(stock: 16, reorderThreshold: 15);

        var response = await db.Primary.CreateClient().PlaceOrderAsync(productId, 17, browserA.ConnectionId);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await Task.Delay(Silence);
        Assert.DoesNotContain(browserA.StockUpdates, u => u.ProductId == productId);
        Assert.DoesNotContain(browserB.StockUpdates, u => u.ProductId == productId);
        Assert.DoesNotContain(browserB.Alerts, a => a.ProductId == productId);
    }

    [Fact]
    public async Task Low_stock_alert_reaches_every_connected_browser()
    {
        var productId = await db.CreateProductAsync(stock: 16, reorderThreshold: 15);

        var response = await db.Primary.CreateClient().PlaceOrderAsync(productId, 2, browserA.ConnectionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        foreach (var browser in new[] { browserA, browserB })
        {
            var alert = await browser.NextAlertAsync(productId, Delivery);
            Assert.Equal(14, alert.Stock);
            Assert.Equal(15, alert.ReorderThreshold);
            Assert.StartsWith("Test product", alert.ProductName);
        }
    }

    [Fact]
    public async Task Browser_that_left_the_watchers_group_receives_nothing()
    {
        var productId = await db.CreateProductAsync(stock: 40);
        await browserB.Connection.InvokeAsync("LeaveStockWatchers");

        await db.Primary.CreateClient().PlaceOrderAsync(productId, 1, browserA.ConnectionId);

        Assert.Equal(39, await browserA.NextStockAsync(productId, Delivery));
        await Task.Delay(Silence);
        Assert.DoesNotContain(browserB.StockUpdates, u => u.ProductId == productId);
    }

    private sealed class BrowserClient : IAsyncDisposable
    {
        public HubConnection Connection { get; }
        public ConcurrentQueue<(int ProductId, int Stock)> StockUpdates { get; } = new();
        public ConcurrentQueue<LowStockAlertEvent> Alerts { get; } = new();
        public string ConnectionId => Connection.ConnectionId!;

        private BrowserClient(HubConnection connection)
        {
            Connection = connection;
            connection.On<int, int>("StockUpdated", (productId, stock) => StockUpdates.Enqueue((productId, stock)));
            connection.On<LowStockAlertEvent>("LowStockAlert", alert => Alerts.Enqueue(alert));
        }

        public static async Task<BrowserClient> ConnectAsync(InventoryApiFactory factory)
        {
            var server = factory.Server;
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(server.BaseAddress, "/hubs/stock"), options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                })
                .Build();
            var client = new BrowserClient(connection);
            await connection.StartAsync();
            await connection.InvokeAsync("JoinStockWatchers");
            return client;
        }

        public async Task<int> NextStockAsync(int productId, TimeSpan timeout)
            => (await WaitForAsync(StockUpdates, u => u.ProductId == productId, timeout)).Stock;

        public Task<LowStockAlertEvent> NextAlertAsync(int productId, TimeSpan timeout)
            => WaitForAsync(Alerts, a => a.ProductId == productId, timeout);

        private static async Task<T> WaitForAsync<T>(ConcurrentQueue<T> queue, Func<T, bool> match, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var found = queue.Where(match).ToList();
                if (found.Count > 0) return found[^1];
                await Task.Delay(25);
            }
            throw new TimeoutException("Expected SignalR event was not received.");
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
