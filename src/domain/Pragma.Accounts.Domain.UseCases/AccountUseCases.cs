using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Model.Exceptions;
using Pragma.Accounts.Domain.Ports;

namespace Pragma.Accounts.Domain.UseCases;

/// <summary>Crea una cuenta activa y publica ACCOUNT_CREATED.</summary>
public sealed class CreateAccountUseCase(IAccountGateway accounts, IEventPublisher events, TimeProvider clock)
{
    public async Task<Account> ExecuteAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.Id))
        {
            // UUID v7: ordenable por tiempo, util como clave primaria.
            account.Id = Guid.CreateVersion7(clock.GetUtcNow()).ToString();
        }
        else if (await accounts.ExistsByIdAsync(account.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(ErrorCatalog.AccountAlreadyExists, $"Account {account.Id} already exists");
        }

        account.Status = AccountStatus.Active;
        account.CreatedAt = clock.GetUtcNow();
        var saved = await accounts.SaveAsync(account, cancellationToken).ConfigureAwait(false);
        await events.PublishAsync(DomainEvents.Of(saved.Id, AccountEventTypes.Created, clock), cancellationToken).ConfigureAwait(false);
        return saved;
    }
}

/// <summary>Consulta por id con cache-aside y listado acotado en streaming.</summary>
public sealed class GetAccountUseCase(IAccountGateway accounts, ICacheGateway cache)
{
    public const int MaxPageSize = 100;

    public async Task<Account> ExecuteAsync(string id, CancellationToken cancellationToken)
    {
        var cached = await cache.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var account = await accounts.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new AccountNotFoundException(id);
        await cache.SetAsync(account, cancellationToken).ConfigureAwait(false);
        return account;
    }

    public IAsyncEnumerable<Account> StreamAllAsync(int limit, CancellationToken cancellationToken) =>
        accounts.StreamAllAsync(Math.Clamp(limit, 1, MaxPageSize), cancellationToken);
}

/// <summary>Reemplazo completo (HTTP PUT). Conserva id y fecha de creacion.</summary>
public sealed class UpdateAccountUseCase(IAccountGateway accounts, ICacheGateway cache, IEventPublisher events, TimeProvider clock)
{
    public async Task<Account> ExecuteAsync(string id, Account replacement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var existing = await accounts.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new AccountNotFoundException(id);

        replacement.Id = existing.Id;
        replacement.CreatedAt = existing.CreatedAt;
        return await AccountWrites.SaveAndNotifyAsync(replacement, accounts, cache, events, clock, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Actualizacion parcial (HTTP PATCH): solo aplica los campos presentes.</summary>
public sealed class PatchAccountUseCase(IAccountGateway accounts, ICacheGateway cache, IEventPublisher events, TimeProvider clock)
{
    public async Task<Account> ExecuteAsync(string id, AccountPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var existing = await accounts.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new AccountNotFoundException(id);

        existing.Apply(patch);
        return await AccountWrites.SaveAndNotifyAsync(existing, accounts, cache, events, clock, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Elimina una cuenta existente y publica ACCOUNT_DELETED.</summary>
public sealed class DeleteAccountUseCase(IAccountGateway accounts, ICacheGateway cache, IEventPublisher events, TimeProvider clock)
{
    public async Task ExecuteAsync(string id, CancellationToken cancellationToken)
    {
        if (!await accounts.ExistsByIdAsync(id, cancellationToken).ConfigureAwait(false))
        {
            throw new AccountNotFoundException(id);
        }

        await accounts.DeleteByIdAsync(id, cancellationToken).ConfigureAwait(false);
        await cache.EvictAsync(id, cancellationToken).ConfigureAwait(false);
        await events.PublishAsync(DomainEvents.Of(id, AccountEventTypes.Deleted, clock), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Busqueda por criterios (metodo HTTP QUERY), en streaming.</summary>
public sealed class SearchAccountsUseCase(IAccountGateway accounts)
{
    public IAsyncEnumerable<Account> ExecuteAsync(AccountSearchCriteria criteria, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        return accounts.SearchAsync(criteria, Math.Clamp(limit, 1, GetAccountUseCase.MaxPageSize), cancellationToken);
    }
}

/// <summary>Eventos de dominio en vivo, opcionalmente filtrados por cuenta.</summary>
public sealed class StreamAccountEventsUseCase(IAccountEventStream stream)
{
    public async IAsyncEnumerable<AccountEvent> ExecuteAsync(
        string? accountId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var accountEvent in stream.SubscribeAsync(cancellationToken).ConfigureAwait(false))
        {
            if (accountId is null || accountEvent.AccountId == accountId)
            {
                yield return accountEvent;
            }
        }
    }
}

/// <summary>Puntaje crediticio de una cuenta existente.</summary>
public sealed class GetCreditScoreUseCase(IAccountGateway accounts, ICreditScoreGateway creditScores)
{
    public async Task<CreditScore> ExecuteAsync(string accountId, CancellationToken cancellationToken)
    {
        if (!await accounts.ExistsByIdAsync(accountId, cancellationToken).ConfigureAwait(false))
        {
            throw new AccountNotFoundException(accountId);
        }

        return await creditScores.GetAsync(accountId, cancellationToken).ConfigureAwait(false);
    }
}

internal static class AccountWrites
{
    public static async Task<Account> SaveAndNotifyAsync(
        Account account,
        IAccountGateway accounts,
        ICacheGateway cache,
        IEventPublisher events,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!AccountStatus.IsValid(account.Status))
        {
            throw new BusinessException(ErrorCatalog.AccountInvalidState, $"Status '{account.Status}' is not valid");
        }

        var saved = await accounts.SaveAsync(account, cancellationToken).ConfigureAwait(false);
        await cache.EvictAsync(saved.Id, cancellationToken).ConfigureAwait(false);
        await events.PublishAsync(DomainEvents.Of(saved.Id, AccountEventTypes.Updated, clock), cancellationToken).ConfigureAwait(false);
        return saved;
    }
}

internal static class DomainEvents
{
    public static AccountEvent Of(string accountId, string type, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return new AccountEvent(accountId, Guid.CreateVersion7(now).ToString(), type, now);
    }
}
