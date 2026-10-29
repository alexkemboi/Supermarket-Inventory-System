using Inventory.Api.Data;
using Inventory.Api.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Api.Tests.Infrastructure;

/// <summary>
/// A disposable SQL Server database (created on the server from docker-compose) shared by the
/// tests of one class. Each API host created from it behaves like an independent API instance:
/// its own DI container, DbContexts and connection pool, all pointed at the same database.
/// </summary>
public sealed class SqlServerDatabase : IAsyncLifetime
{
    private readonly List<InventoryApiFactory> factories = [];
    private int skuCounter;

    public string ConnectionString { get; }
    public string DatabaseName { get; }
    public int CategoryId { get; private set; }

    public SqlServerDatabase()
    {
        DatabaseName = $"InventoryTests_{Guid.NewGuid():N}";
        ConnectionString = new SqlConnectionStringBuilder(BaseConnectionString())
        {
            InitialCatalog = DatabaseName,
            MaxPoolSize = 200
        }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        // Keep startup fast: products and categories are seeded, the 250k historical orders are not.
        Environment.SetEnvironmentVariable("SEED_ORDER_COUNT", "0");

        var primary = CreateFactory();
        _ = primary.Server; // runs Program: migrations + seeding

        await using var db = CreateDbContext();
        if (db.Database.GetDbConnection().Database != DatabaseName)
            throw new InvalidOperationException("Tests must never run against the application database.");
        CategoryId = await db.Categories.OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();

        // Guard against the API host silently using the appsettings connection string.
        using var scope = primary.Services.CreateScope();
        var apiDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (apiDb.Database.GetDbConnection().Database != DatabaseName)
            throw new InvalidOperationException("API host is not using the disposable test database.");
    }

    public async Task DisposeAsync()
    {
        foreach (var factory in factories) await factory.DisposeAsync();
        SqlConnection.ClearAllPools();

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    public InventoryApiFactory Primary => factories[0];

    public InventoryApiFactory CreateFactory(
        Action<IServiceCollection>? configureServices = null, bool authenticated = true)
    {
        var factory = new InventoryApiFactory(ConnectionString, configureServices, authenticated);
        factories.Add(factory);
        return factory;
    }

    public AppDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task<int> CreateProductAsync(int stock, int reorderThreshold = 15, decimal unitPrice = 2.50m)
    {
        var n = Interlocked.Increment(ref skuCounter);
        await using var db = CreateDbContext();
        var product = new Product
        {
            Name = $"Test product {n} {Guid.NewGuid():N}",
            Sku = $"T-{n:000000}-{Random.Shared.Next(100000):00000}",
            CategoryId = CategoryId,
            UnitPrice = unitPrice,
            Stock = stock,
            ReorderThreshold = reorderThreshold
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    public async Task<int> GetStockAsync(int productId)
    {
        await using var db = CreateDbContext();
        return await db.Products.Where(p => p.Id == productId).Select(p => p.Stock).SingleAsync();
    }

    /// <summary>Total quantity recorded in order lines of non-cancelled orders for a product.</summary>
    public async Task<int> OrderedQuantityAsync(int productId)
    {
        await using var db = CreateDbContext();
        return await db.OrderLines
            .Where(l => l.ProductId == productId && l.Order.Status != OrderStatus.Cancelled)
            .SumAsync(l => (int?)l.Quantity) ?? 0;
    }

    public async Task<int> OrderLineCountAsync(int productId)
    {
        await using var db = CreateDbContext();
        return await db.OrderLines.CountAsync(l => l.ProductId == productId);
    }

    public async Task<int> OrdersWithoutLinesAsync()
    {
        await using var db = CreateDbContext();
        return await db.Orders.CountAsync(o => !o.Lines.Any());
    }

    private static string BaseConnectionString()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;

        // Fall back to the API's own appsettings.json so no credentials are duplicated in the tests.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Inventory.sln")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Could not locate the repository root.");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(directory.FullName, "backend", "appsettings.json"))
            .Build();
        return configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("No DefaultConnection connection string configured.");
    }
}
