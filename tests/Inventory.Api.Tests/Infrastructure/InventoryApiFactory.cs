using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inventory.Api.Tests.Infrastructure;

/// <summary>One in-process API instance bound to the given SQL Server database.</summary>
public sealed class InventoryApiFactory(string connectionString, Action<IServiceCollection>? configureServices)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString }));
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        if (configureServices is not null) builder.ConfigureTestServices(configureServices);
    }
}
