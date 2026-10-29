using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Api.Tests.Infrastructure;

namespace Inventory.Api.Tests;

/// <summary>
/// Pins the HTTP contract of the order, restock and cancel endpoints. These tests were run against the
/// original controller implementation before the refactoring and must keep passing after it.
/// </summary>
public class OrderContractTests(SqlServerDatabase db) : IClassFixture<SqlServerDatabase>
{
    private HttpClient Client => db.Primary.CreateClient();

    [Fact]
    public async Task Successful_order_returns_200_with_the_new_order_id()
    {
        var productId = await db.CreateProductAsync(stock: 10, unitPrice: 3.25m);

        var response = await Client.PlaceOrderAsync(productId, 2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["id"], body.RootElement.EnumerateObject().Select(p => p.Name));
        var orderId = body.RootElement.GetProperty("id").GetInt32();

        await using var context = db.CreateDbContext();
        var line = context.OrderLines.Single(l => l.OrderId == orderId);
        Assert.Equal(productId, line.ProductId);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(3.25m, line.UnitPrice);
        Assert.Equal(Models.OrderStatus.Placed, context.Orders.Single(o => o.Id == orderId).Status);
    }

    [Fact]
    public async Task Order_without_lines_returns_400()
    {
        var response = await Client.PostAsJsonAsync("/api/orders", new { lines = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Order must contain at least one line.", await response.ErrorAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Order_with_non_positive_quantity_returns_400(int quantity)
    {
        var productId = await db.CreateProductAsync(stock: 10);

        var response = await Client.PlaceOrderAsync(productId, quantity);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("All line quantities must be greater than zero.", await response.ErrorAsync());
        Assert.Equal(10, await db.GetStockAsync(productId));
    }

    [Fact]
    public async Task Order_for_unknown_product_returns_404()
    {
        var response = await Client.PlaceOrderAsync(int.MaxValue, 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"Product {int.MaxValue} not found.", await response.ErrorAsync());
    }

    [Fact]
    public async Task Order_exceeding_stock_returns_409()
    {
        var productId = await db.CreateProductAsync(stock: 1);

        var response = await Client.PlaceOrderAsync(productId, 2);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal($"Insufficient stock for product {productId}.", await response.ErrorAsync());
    }

    [Fact]
    public async Task Order_with_two_lines_for_the_same_product_deducts_both()
    {
        var productId = await db.CreateProductAsync(stock: 10);

        var response = await Client.PlaceOrderAsync((productId, 2), (productId, 3));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, await db.GetStockAsync(productId));
    }

    [Fact]
    public async Task Restock_adds_quantity_and_returns_204()
    {
        var productId = await db.CreateProductAsync(stock: 4);

        var response = await Client.PostAsJsonAsync($"/api/products/{productId}/restock", new { quantity = 6 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(10, await db.GetStockAsync(productId));
    }

    [Fact]
    public async Task Restock_with_non_positive_quantity_returns_400()
    {
        var productId = await db.CreateProductAsync(stock: 4);

        var response = await Client.PostAsJsonAsync($"/api/products/{productId}/restock", new { quantity = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Quantity must be greater than zero.", await response.ErrorAsync());
    }

    [Fact]
    public async Task Restock_of_unknown_product_returns_404()
    {
        var response = await Client.PostAsJsonAsync($"/api/products/{int.MaxValue}/restock", new { quantity = 1 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"Product {int.MaxValue} not found.", await response.ErrorAsync());
    }

    [Fact]
    public async Task Cancel_returns_stock_and_a_second_cancel_returns_409()
    {
        var productId = await db.CreateProductAsync(stock: 10);
        var orderId = await (await Client.PlaceOrderAsync(productId, 4)).OrderIdAsync();

        var first = await Client.PostAsync($"/api/orders/{orderId}/cancel", null);
        var second = await Client.PostAsync($"/api/orders/{orderId}/cancel", null);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal($"Order {orderId} is already cancelled.", await second.ErrorAsync());
        Assert.Equal(10, await db.GetStockAsync(productId));
    }

    [Fact]
    public async Task Cancel_of_unknown_order_returns_404()
    {
        var response = await Client.PostAsync($"/api/orders/{int.MaxValue}/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"Order {int.MaxValue} not found.", await response.ErrorAsync());
    }

    [Fact]
    public async Task Recent_orders_returns_placed_orders_with_lines_and_totals()
    {
        var productId = await db.CreateProductAsync(stock: 10, unitPrice: 2m);
        var orderId = await (await Client.PlaceOrderAsync(productId, 3)).OrderIdAsync();

        var orders = await Client.GetFromJsonAsync<JsonElement>("/api/orders/recent?take=200");

        var order = orders.EnumerateArray().Single(o => o.GetProperty("id").GetInt32() == orderId);
        Assert.Equal("Placed", order.GetProperty("status").GetString());
        Assert.Equal(6m, order.GetProperty("total").GetDecimal());
        Assert.Equal(productId, order.GetProperty("lines")[0].GetProperty("productId").GetInt32());
    }
}
