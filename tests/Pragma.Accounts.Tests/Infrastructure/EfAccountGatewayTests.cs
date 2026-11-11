using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Infrastructure.Persistence;

namespace Pragma.Accounts.Tests.Infrastructure;

/// <summary>Adapter de persistencia contra SQLite en memoria (mismo modelo EF que PostgreSQL).</summary>
public sealed class EfAccountGatewayTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private AccountsDbContext _db = null!;
    private EfAccountGateway _gateway = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(_ct);
        _db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync(_ct);
        _gateway = new EfAccountGateway(_db);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Save_New_InsertsAndFinds()
    {
        await _gateway.SaveAsync(TestData.Account(), _ct);

        var found = await _gateway.FindByIdAsync("acc-1", _ct);

        Assert.NotNull(found);
        Assert.Equal("ana.perez@example.com", found.Email);
        Assert.Equal(100m, found.Balance);
        Assert.True(await _gateway.ExistsByIdAsync("acc-1", _ct));
    }

    [Fact]
    public async Task Save_Existing_Updates()
    {
        await _gateway.SaveAsync(TestData.Account(), _ct);
        var changed = TestData.Account();
        changed.HolderName = "Otro";

        await _gateway.SaveAsync(changed, _ct);

        Assert.Equal("Otro", (await _gateway.FindByIdAsync("acc-1", _ct))!.HolderName);
        Assert.Single(await _gateway.StreamAllAsync(10, _ct).ToListAsync(_ct));
    }

    [Fact]
    public async Task FindById_Missing_ReturnsNull() =>
        Assert.Null(await _gateway.FindByIdAsync("missing", _ct));

    [Fact]
    public async Task FindAll_RespectsLimitAndOrder()
    {
        foreach (var id in new[] { "c", "a", "b" })
        {
            await _gateway.SaveAsync(TestData.Account(id), _ct);
        }

        var page = await _gateway.StreamAllAsync(2, _ct).ToListAsync(_ct);

        Assert.Equal(["a", "b"], page.Select(a => a.Id));
    }

    [Fact]
    public async Task Delete_RemovesAccount()
    {
        await _gateway.SaveAsync(TestData.Account(), _ct);

        await _gateway.DeleteByIdAsync("acc-1", _ct);

        Assert.False(await _gateway.ExistsByIdAsync("acc-1", _ct));
    }

    [Fact]
    public async Task Search_FiltersByEveryCriterion()
    {
        var blocked = TestData.Account("b1", AccountStatus.Blocked);
        var checking = TestData.Account("c1");
        checking.AccountType = "CHECKING";
        var other = TestData.Account("o1");
        other.HolderName = "Carlos Ruiz";
        foreach (var account in new[] { TestData.Account("a1"), blocked, checking, other })
        {
            await _gateway.SaveAsync(account, _ct);
        }

        Assert.Equal(["b1"], (await _gateway.SearchAsync(new AccountSearchCriteria(Status: AccountStatus.Blocked), 10, _ct).ToListAsync(_ct)).Select(a => a.Id));
        Assert.Equal(["c1"], (await _gateway.SearchAsync(new AccountSearchCriteria(AccountType: "CHECKING"), 10, _ct).ToListAsync(_ct)).Select(a => a.Id));
        Assert.Equal(["o1"], (await _gateway.SearchAsync(new AccountSearchCriteria(HolderName: "Carlos"), 10, _ct).ToListAsync(_ct)).Select(a => a.Id));
        Assert.Equal(4, (await _gateway.SearchAsync(new AccountSearchCriteria(), 10, _ct).ToListAsync(_ct)).Count);
    }
}
