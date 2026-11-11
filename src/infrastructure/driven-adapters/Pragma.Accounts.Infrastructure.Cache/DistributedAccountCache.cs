using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Ports;

namespace Pragma.Accounts.Infrastructure.Cache;

public sealed class CacheOptions
{
    public const string Section = "Cache";

    /// <summary>Tiempo de vida de cada entrada.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Cache-aside sobre IDistributedCache (Redis). Si la cache falla se registra
/// y se continua: la cache nunca debe tumbar una operacion de negocio.
/// </summary>
internal sealed partial class DistributedAccountCache(
    IDistributedCache cache,
    CacheOptions options,
    ILogger<DistributedAccountCache> logger) : ICacheGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Account?> GetAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await cache.GetAsync(Key(id), cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : JsonSerializer.Deserialize<Account>(bytes, Json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCacheFailure(logger, "get", ex);
            return null;
        }
    }

    public async Task SetAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(account, Json);
            var entry = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Ttl };
            await cache.SetAsync(Key(account.Id), bytes, entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCacheFailure(logger, "set", ex);
        }
    }

    public async Task EvictAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveAsync(Key(id), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCacheFailure(logger, "evict", ex);
        }
    }

    internal static string Key(string id) => $"accounts:{id}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache operation '{Operation}' failed; continuing without cache")]
    private static partial void LogCacheFailure(ILogger logger, string operation, Exception exception);
}

public static class CacheServiceCollectionExtensions
{
    /// <summary>Registra Redis como cache distribuida de cuentas.</summary>
    public static IServiceCollection AddRedisAccountCache(this IServiceCollection services, string connectionString, CacheOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        services.AddStackExchangeRedisCache(redis =>
        {
            redis.Configuration = connectionString;
            redis.InstanceName = "pragma-accounts:";
        });
        services.AddSingleton(options);
        services.AddSingleton<ICacheGateway, DistributedAccountCache>();
        return services;
    }
}
