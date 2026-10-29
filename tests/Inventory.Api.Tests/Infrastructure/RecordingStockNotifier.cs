using System.Collections.Concurrent;
using Inventory.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inventory.Api.Tests.Infrastructure;

public record StockUpdatedEvent(int ProductId, int Stock, string? RequesterConnectionId);

public record LowStockAlertEvent(int ProductId, string ProductName, int Stock, int ReorderThreshold);

public sealed class RecordingStockNotifier : IStockNotifier
{
    public ConcurrentQueue<StockUpdatedEvent> StockUpdates { get; } = new();
    public ConcurrentQueue<LowStockAlertEvent> LowStockAlerts { get; } = new();
    public bool ThrowOnPublish { get; set; }

    public Task StockUpdatedAsync(int productId, int newStock, CancellationToken ct = default, string? requesterConnectionId = null)
    {
        if (ThrowOnPublish) throw new InvalidOperationException("Simulated SignalR outage.");
        StockUpdates.Enqueue(new StockUpdatedEvent(productId, newStock, requesterConnectionId));
        return Task.CompletedTask;
    }

    public Task LowStockAlertAsync(int productId, string productName, int stock, int reorderThreshold, CancellationToken ct = default)
    {
        if (ThrowOnPublish) throw new InvalidOperationException("Simulated SignalR outage.");
        LowStockAlerts.Enqueue(new LowStockAlertEvent(productId, productName, stock, reorderThreshold));
        return Task.CompletedTask;
    }

    public Action<IServiceCollection> Register => services =>
    {
        services.RemoveAll<IStockNotifier>();
        services.AddSingleton<IStockNotifier>(this);
    };
}
