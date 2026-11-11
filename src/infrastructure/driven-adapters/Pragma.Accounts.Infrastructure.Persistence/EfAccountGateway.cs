using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Ports;

namespace Pragma.Accounts.Infrastructure.Persistence;

/// <summary>
/// Implementacion de IAccountGateway con EF Core. Las colecciones se leen en streaming
/// (AsAsyncEnumerable): cada fila se mapea y entrega sin cargar el resultado completo en memoria.
/// </summary>
internal sealed class EfAccountGateway(AccountsDbContext db) : IAccountGateway
{
    public async Task<Account> SaveAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var entity = await db.Accounts.FindAsync([account.Id], cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new AccountEntity { Id = account.Id };
            db.Accounts.Add(entity);
        }

        AccountMapper.CopyTo(account, entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return AccountMapper.ToDomain(entity);
    }

    public async Task<Account?> FindByIdAsync(string id, CancellationToken cancellationToken)
    {
        var entity = await db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : AccountMapper.ToDomain(entity);
    }

    public IAsyncEnumerable<Account> StreamAllAsync(int limit, CancellationToken cancellationToken) =>
        Stream(db.Accounts.AsNoTracking().OrderBy(a => a.Id).Take(limit), cancellationToken);

    public Task<bool> ExistsByIdAsync(string id, CancellationToken cancellationToken) =>
        db.Accounts.AsNoTracking().AnyAsync(a => a.Id == id, cancellationToken);

    public Task DeleteByIdAsync(string id, CancellationToken cancellationToken) =>
        db.Accounts.Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken);

    public IAsyncEnumerable<Account> SearchAsync(AccountSearchCriteria criteria, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var query = db.Accounts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(criteria.Status))
        {
            query = query.Where(a => a.Status == criteria.Status);
        }

        if (!string.IsNullOrWhiteSpace(criteria.AccountType))
        {
            query = query.Where(a => a.AccountType == criteria.AccountType);
        }

        if (!string.IsNullOrWhiteSpace(criteria.HolderName))
        {
            query = query.Where(a => a.HolderName.Contains(criteria.HolderName));
        }

        return Stream(query.OrderBy(a => a.Id).Take(limit), cancellationToken);
    }

    private static async IAsyncEnumerable<Account> Stream(
        IQueryable<AccountEntity> query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entity in query.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return AccountMapper.ToDomain(entity);
        }
    }
}

internal static class AccountMapper
{
    public static Account ToDomain(AccountEntity entity) => new()
    {
        Id = entity.Id,
        HolderName = entity.HolderName,
        Email = entity.Email,
        AccountType = entity.AccountType,
        Balance = entity.Balance,
        Status = entity.Status,
        CreatedAt = entity.CreatedAt,
    };

    public static void CopyTo(Account account, AccountEntity entity)
    {
        entity.HolderName = account.HolderName;
        entity.Email = account.Email;
        entity.AccountType = account.AccountType;
        entity.Balance = account.Balance;
        entity.Status = account.Status;
        entity.CreatedAt = account.CreatedAt;
    }
}
