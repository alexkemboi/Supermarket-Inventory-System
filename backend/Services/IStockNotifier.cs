using Inventory.Api.Contracts;
using Inventory.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Inventory.Api.Services;

public interface IStockNotifier
{
    Task StockUpdatedAsync(int productId, int newStock, CancellationToken ct = default, string? requesterConnectionId = null);
    Task LowStockAlertAsync(int productId, string productName, int stock, int reorderThreshold, CancellationToken ct = default);
}

public class SignalRStockNotifier(IHubContext<StockHub> hub) : IStockNotifier
{
    public const string StockWatchersGroup = "stock-watchers";

    // Stock changes are broadcast to every watcher, including the requester (whose own page applies the
    // same value idempotently). The requester's connection id is accepted for compatibility but no longer
    // narrows the audience: sending only to the requester is what kept other users' boards stale.
    public Task StockUpdatedAsync(int productId, int newStock, CancellationToken ct = default, string? requesterConnectionId = null)
        => hub.Clients.Group(StockWatchersGroup).SendAsync("StockUpdated", productId, newStock, ct);

    public Task LowStockAlertAsync(int productId, string productName, int stock, int reorderThreshold, CancellationToken ct = default)
        => hub.Clients.Group(StockWatchersGroup)
            .SendAsync("LowStockAlert", new LowStockAlertDto(productId, productName, stock, reorderThreshold), ct);
}
