using System.Net.Http.Json;
using Inventory.Api.Contracts;
using Inventory.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Inventory.Api.Tests;

/// <summary>Behaviour and round-trip budget of GET /api/products (the inventory list page).</summary>
public class ProductListTests : IClassFixture<SqlServerDatabase>
{
    private readonly SqlServerDatabase db;
    private readonly ITestOutputHelper output;
    private readonly SqlCommandRecorder recorder = new();
    private readonly HttpClient client;

    public ProductListTests(SqlServerDatabase db, ITestOutputHelper output)
    {
        this.db = db;
        this.output = output;
        client = db.CreateFactory(recorder.Register).CreateClient();
    }

    [Fact]
    public async Task Page_returns_products_ordered_by_id_with_their_category_names()
    {
        var page = await client.GetFromJsonAsync<PagedResult<ProductListDto>>("/api/products?page=2&pageSize=25");

        await using var context = db.CreateDbContext();
        var expected = await context.Products
            .OrderBy(p => p.Id).Skip(25).Take(25)
            .Select(p => new ProductListDto(p.Id, p.Name, p.Sku, p.Category.Name, p.UnitPrice, p.Stock, p.ReorderThreshold))
            .ToListAsync();

        Assert.NotNull(page);
        Assert.Equal(await context.Products.CountAsync(), page.Total);
        Assert.Equal(2, page.Page);
        Assert.Equal(25, page.PageSize);
        Assert.Equal(expected, page.Items);
    }

    [Fact]
    public async Task Search_matches_name_or_sku_and_total_reflects_the_filter()
    {
        await using var context = db.CreateDbContext();
        var sku = await context.Products.OrderBy(p => p.Id).Select(p => p.Sku).FirstAsync();
        var nameFragment = "Milk";
        var expectedNameMatches = await context.Products.CountAsync(p => p.Name.Contains(nameFragment) || p.Sku.Contains(nameFragment));

        var bySku = await client.GetFromJsonAsync<PagedResult<ProductListDto>>($"/api/products?search={sku}");
        var byName = await client.GetFromJsonAsync<PagedResult<ProductListDto>>($"/api/products?search={nameFragment}&pageSize=200");

        Assert.Equal(sku, Assert.Single(bySku!.Items).Sku);
        Assert.Equal(expectedNameMatches, byName!.Total);
        Assert.All(byName.Items, p => Assert.True(p.Name.Contains(nameFragment) || p.Sku.Contains(nameFragment)));
    }

    [Fact]
    public async Task Page_size_is_clamped_and_pages_past_the_end_are_empty()
    {
        var large = await client.GetFromJsonAsync<PagedResult<ProductListDto>>("/api/products?pageSize=5000");
        var beyond = await client.GetFromJsonAsync<PagedResult<ProductListDto>>("/api/products?page=100000&pageSize=50");

        Assert.Equal(200, large!.PageSize);
        Assert.Equal(200, large.Items.Count);
        Assert.Empty(beyond!.Items);
        Assert.True(beyond.Total > 0);
    }

    /// <summary>
    /// The inventory page requests 200 rows. The list must be served with a count query plus one page
    /// query, independent of page size (no per-row category lookups).
    /// </summary>
    [Fact]
    public async Task Inventory_page_request_uses_a_constant_number_of_sql_commands()
    {
        recorder.Clear();

        await client.GetFromJsonAsync<PagedResult<ProductListDto>>("/api/products?pageSize=200");

        var commands = recorder.Commands.ToList();
        output.WriteLine($"SQL commands executed: {commands.Count}");
        foreach (var sql in commands.Distinct()) output.WriteLine($"--- ({commands.Count(c => c == sql)}x)\n{sql}");

        Assert.Equal(2, commands.Count);
    }
}
