using Pragma.Accounts.Domain.Model;

namespace Pragma.Accounts.Domain.Ports;

/// <summary>
/// Persistencia de cuentas. Las consultas de colecciones son streams
/// (IAsyncEnumerable): se consumen elemento a elemento, sin materializar la lista.
/// </summary>
public interface IAccountGateway
{
    Task<Account> SaveAsync(Account account, CancellationToken cancellationToken);

    Task<Account?> FindByIdAsync(string id, CancellationToken cancellationToken);

    IAsyncEnumerable<Account> StreamAllAsync(int limit, CancellationToken cancellationToken);

    Task<bool> ExistsByIdAsync(string id, CancellationToken cancellationToken);

    Task DeleteByIdAsync(string id, CancellationToken cancellationToken);

    /// <summary>Busqueda por criterios (metodo HTTP QUERY), en streaming.</summary>
    IAsyncEnumerable<Account> SearchAsync(AccountSearchCriteria criteria, int limit, CancellationToken cancellationToken);
}

/// <summary>Cache distribuida de cuentas (cache-aside). Una falla de cache no debe romper el flujo.</summary>
public interface ICacheGateway
{
    Task<Account?> GetAsync(string id, CancellationToken cancellationToken);

    Task SetAsync(Account account, CancellationToken cancellationToken);

    Task EvictAsync(string id, CancellationToken cancellationToken);
}

/// <summary>Publicacion de eventos de dominio.</summary>
public interface IEventPublisher
{
    Task PublishAsync(AccountEvent accountEvent, CancellationToken cancellationToken);
}

/// <summary>Suscripcion en vivo a los eventos de dominio (por ejemplo para Server-Sent Events).</summary>
public interface IAccountEventStream
{
    /// <summary>Stream de eventos desde el momento de la suscripcion hasta que se cancele el token.</summary>
    IAsyncEnumerable<AccountEvent> SubscribeAsync(CancellationToken cancellationToken);
}

/// <summary>Proveedor externo de puntaje crediticio.</summary>
public interface ICreditScoreGateway
{
    Task<CreditScore> GetAsync(string accountId, CancellationToken cancellationToken);
}
