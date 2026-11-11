using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pragma.Accounts.Domain.Ports;

namespace Pragma.Accounts.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registra PostgreSQL (EF Core + Npgsql) como implementacion de IAccountGateway.</summary>
    public static IServiceCollection AddPostgresPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<AccountsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3)));
        services.AddScoped<IAccountGateway, EfAccountGateway>();
        services.AddHealthChecks().AddDbContextCheck<AccountsDbContext>("postgres", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// Crea el esquema si no existe. Solo para desarrollo local: en ambientes
    /// productivos el esquema se gestiona con migraciones versionadas.
    /// </summary>
    public static async Task EnsurePersistenceSchemaAsync(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
    }
}
