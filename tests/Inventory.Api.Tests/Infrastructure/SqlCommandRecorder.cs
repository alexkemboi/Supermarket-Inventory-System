using System.Collections.Concurrent;
using System.Data.Common;
using Inventory.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Api.Tests.Infrastructure;

/// <summary>Captures every SQL command EF Core sends, so tests can assert on round-trips and SQL text.</summary>
public sealed class SqlCommandRecorder : DbCommandInterceptor
{
    public ConcurrentQueue<string> Commands { get; } = new();

    public void Clear() => Commands.Clear();

    public Action<IServiceCollection> Register => services =>
        services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(this));

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        Commands.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, ct);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
    {
        Commands.Enqueue(command.CommandText);
        return base.ScalarExecutingAsync(command, eventData, result, ct);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Commands.Enqueue(command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, ct);
    }
}
