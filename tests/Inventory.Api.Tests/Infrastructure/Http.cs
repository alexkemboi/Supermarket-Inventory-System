using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Inventory.Api.Tests.Infrastructure;

public static class Http
{
    public static Task<HttpResponseMessage> PlaceOrderAsync(
        this HttpClient client, int productId, int quantity, string? connectionId = null)
        => client.PostAsJsonAsync("/api/orders", new
        {
            lines = new[] { new { productId, quantity } },
            connectionId
        });

    public static Task<HttpResponseMessage> PlaceOrderAsync(
        this HttpClient client, params (int ProductId, int Quantity)[] lines)
        => client.PostAsJsonAsync("/api/orders", new
        {
            lines = lines.Select(l => new { productId = l.ProductId, quantity = l.Quantity })
        });

    public static async Task<string?> ErrorAsync(this HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    public static async Task<int> OrderIdAsync(this HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetInt32();
    }
}
