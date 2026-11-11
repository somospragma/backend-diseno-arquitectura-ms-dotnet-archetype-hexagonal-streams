using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Model.Exceptions;
using Pragma.Accounts.Domain.Ports;
using Pragma.Accounts.Domain.UseCases;

namespace Pragma.Accounts.Tests.Domain;

public sealed class UseCaseTests
{
    private readonly IAccountGateway _accounts = Substitute.For<IAccountGateway>();
    private readonly ICacheGateway _cache = Substitute.For<ICacheGateway>();
    private readonly IEventPublisher _events = Substitute.For<IEventPublisher>();
    private readonly ICreditScoreGateway _scores = Substitute.For<ICreditScoreGateway>();
    private readonly FakeTimeProvider _clock = new(TestData.Now);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public UseCaseTests() =>
        _accounts.SaveAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Account>());

    [Fact]
    public async Task Create_WithoutId_GeneratesIdActivatesAndPublishes()
    {
        var input = TestData.Account(id: "", status: AccountStatus.Blocked);

        var created = await new CreateAccountUseCase(_accounts, _events, _clock).ExecuteAsync(input, _ct);

        Assert.True(Guid.TryParse(created.Id, out var id));
        Assert.Equal(7, id.Version);
        Assert.Equal(AccountStatus.Active, created.Status);
        Assert.Equal(TestData.Now, created.CreatedAt);
        await _events.Received(1).PublishAsync(
            Arg.Is<AccountEvent>(e => e.AccountId == created.Id && e.Type == AccountEventTypes.Created && e.OccurredAt == TestData.Now),
            _ct);
    }

    [Fact]
    public async Task Create_WithExistingId_ThrowsAlreadyExists()
    {
        _accounts.ExistsByIdAsync("acc-1", _ct).Returns(true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            new CreateAccountUseCase(_accounts, _events, _clock).ExecuteAsync(TestData.Account(), _ct));

        Assert.Equal(ErrorCatalog.AccountAlreadyExists, ex.Error);
        await _accounts.DidNotReceive().SaveAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithExistingId_MessageIncludesId()
    {
        _accounts.ExistsByIdAsync("acc-1", _ct).Returns(true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            new CreateAccountUseCase(_accounts, _events, _clock).ExecuteAsync(TestData.Account(), _ct));

        Assert.Equal("Account acc-1 already exists", ex.Message);
    }

    [Fact]
    public async Task NullArguments_AreRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new CreateAccountUseCase(_accounts, _events, _clock).ExecuteAsync(null!, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new UpdateAccountUseCase(_accounts, _cache, _events, _clock).ExecuteAsync("acc-1", null!, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new PatchAccountUseCase(_accounts, _cache, _events, _clock).ExecuteAsync("acc-1", null!, _ct));
        Assert.Throws<ArgumentNullException>(() => new SearchAccountsUseCase(_accounts).ExecuteAsync(null!, 10, _ct));
        await _accounts.DidNotReceive().FindByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithNewId_KeepsProvidedId()
    {
        var created = await new CreateAccountUseCase(_accounts, _events, _clock).ExecuteAsync(TestData.Account("custom-1"), _ct);

        Assert.Equal("custom-1", created.Id);
    }

    [Fact]
    public async Task Get_CacheHit_DoesNotQueryGateway()
    {
        _cache.GetAsync("acc-1", _ct).Returns(TestData.Account());

        var account = await new GetAccountUseCase(_accounts, _cache).ExecuteAsync("acc-1", _ct);

        Assert.Equal("acc-1", account.Id);
        await _accounts.DidNotReceive().FindByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_CacheMiss_LoadsAndCaches()
    {
        var stored = TestData.Account();
        _accounts.FindByIdAsync("acc-1", _ct).Returns(stored);

        var account = await new GetAccountUseCase(_accounts, _cache).ExecuteAsync("acc-1", _ct);

        Assert.Same(stored, account);
        await _cache.Received(1).SetAsync(stored, _ct);
    }

    [Fact]
    public async Task Get_NotFound_Throws()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            new GetAccountUseCase(_accounts, _cache).ExecuteAsync("missing", _ct));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 50)]
    [InlineData(1000, GetAccountUseCase.MaxPageSize)]
    public void StreamAll_ClampsLimit(int requested, int expected)
    {
        _ = new GetAccountUseCase(_accounts, _cache).StreamAllAsync(requested, _ct);

        _ = _accounts.Received(1).StreamAllAsync(expected, _ct);
    }

    [Fact]
    public async Task Update_KeepsIdentityAndCreationDate_EvictsAndPublishes()
    {
        _accounts.FindByIdAsync("acc-1", _ct).Returns(TestData.Account());
        var replacement = TestData.Account(id: "other");
        replacement.HolderName = "Nuevo";
        replacement.CreatedAt = DateTimeOffset.MinValue;

        var updated = await new UpdateAccountUseCase(_accounts, _cache, _events, _clock).ExecuteAsync("acc-1", replacement, _ct);

        Assert.Equal("acc-1", updated.Id);
        Assert.Equal(TestData.Now, updated.CreatedAt);
        Assert.Equal("Nuevo", updated.HolderName);
        await _cache.Received(1).EvictAsync("acc-1", _ct);
        await _events.Received(1).PublishAsync(Arg.Is<AccountEvent>(e => e.Type == AccountEventTypes.Updated), _ct);
    }

    [Fact]
    public async Task Update_NotFound_Throws()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            new UpdateAccountUseCase(_accounts, _cache, _events, _clock).ExecuteAsync("missing", TestData.Account(), _ct));
    }

    [Fact]
    public async Task Update_InvalidStatus_ThrowsInvalidState()
    {
        _accounts.FindByIdAsync("acc-1", _ct).Returns(TestData.Account());

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            new UpdateAccountUseCase(_accounts, _cache, _events, _clock).ExecuteAsync("acc-1", TestData.Account(status: "CLOSED"), _ct));

        Assert.Equal(ErrorCatalog.AccountInvalidState, ex.Error);
        Assert.Equal("Status 'CLOSED' is not valid", ex.Message);
        await _accounts.DidNotReceive().SaveAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Patch_AppliesOnlyPresentFields()
    {
        _accounts.FindByIdAsync("acc-1", _ct).Returns(TestData.Account());

        var patched = await new PatchAccountUseCase(_accounts, _cache, _events, _clock)
            .ExecuteAsync("acc-1", new AccountPatch(Balance: 250m), _ct);

        Assert.Equal(250m, patched.Balance);
        Assert.Equal("Ana Perez", patched.HolderName);
        await _cache.Received(1).EvictAsync("acc-1", _ct);
    }

    [Fact]
    public async Task Patch_NotFound_Throws()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            new PatchAccountUseCase(_accounts, _cache, _events, _clock).ExecuteAsync("missing", new AccountPatch(), _ct));
    }

    [Fact]
    public async Task Delete_Existing_DeletesEvictsAndPublishes()
    {
        _accounts.ExistsByIdAsync("acc-1", _ct).Returns(true);

        await new DeleteAccountUseCase(_accounts, _cache, _events, _clock).ExecuteAsync("acc-1", _ct);

        await _accounts.Received(1).DeleteByIdAsync("acc-1", _ct);
        await _cache.Received(1).EvictAsync("acc-1", _ct);
        await _events.Received(1).PublishAsync(Arg.Is<AccountEvent>(e => e.Type == AccountEventTypes.Deleted), _ct);
    }

    [Fact]
    public async Task Delete_NotFound_Throws()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            new DeleteAccountUseCase(_accounts, _cache, _events, _clock).ExecuteAsync("missing", _ct));
        await _accounts.DidNotReceive().DeleteByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_PassesCriteriaAndClampedLimit()
    {
        var criteria = new AccountSearchCriteria(Status: AccountStatus.Active);
        _accounts.SearchAsync(criteria, GetAccountUseCase.MaxPageSize, _ct).Returns(new[] { TestData.Account() }.ToAsyncEnumerable());

        var found = await new SearchAccountsUseCase(_accounts).ExecuteAsync(criteria, 500, _ct).ToListAsync(_ct);

        Assert.Single(found);
    }

    [Fact]
    public async Task StreamEvents_WithoutFilter_YieldsEverything()
    {
        var stream = Substitute.For<IAccountEventStream>();
        stream.SubscribeAsync(_ct).Returns(Events("a", "b", "a"));

        var received = await new StreamAccountEventsUseCase(stream).ExecuteAsync(null, _ct).ToListAsync(_ct);

        Assert.Equal(["a", "b", "a"], received.Select(e => e.AccountId));
    }

    [Fact]
    public async Task StreamEvents_WithAccountFilter_YieldsOnlyThatAccount()
    {
        var stream = Substitute.For<IAccountEventStream>();
        stream.SubscribeAsync(_ct).Returns(Events("a", "b", "a"));

        var received = await new StreamAccountEventsUseCase(stream).ExecuteAsync("a", _ct).ToListAsync(_ct);

        Assert.Equal(2, received.Count);
        Assert.All(received, e => Assert.Equal("a", e.AccountId));
    }

    private static IAsyncEnumerable<AccountEvent> Events(params string[] accountIds) =>
        accountIds.Select((id, i) => new AccountEvent(id, $"e{i}", AccountEventTypes.Updated, TestData.Now)).ToAsyncEnumerable();

    [Fact]
    public async Task CreditScore_ExistingAccount_ReturnsProviderScore()
    {
        _accounts.ExistsByIdAsync("acc-1", _ct).Returns(true);
        _scores.GetAsync("acc-1", _ct).Returns(new CreditScore("acc-1", 720, "A"));

        var score = await new GetCreditScoreUseCase(_accounts, _scores).ExecuteAsync("acc-1", _ct);

        Assert.Equal(720, score.Score);
    }

    [Fact]
    public async Task CreditScore_MissingAccount_Throws()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            new GetCreditScoreUseCase(_accounts, _scores).ExecuteAsync("missing", _ct));
        await _scores.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
