using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Model.Exceptions;

namespace Pragma.Accounts.Tests.Domain;

public sealed class AccountTests
{
    [Fact]
    public void Apply_WithAllFields_ReplacesEveryField()
    {
        var account = TestData.Account();

        account.Apply(new AccountPatch("Luis", "luis@example.com", "CHECKING", 5m, AccountStatus.Blocked));

        Assert.Equal("Luis", account.HolderName);
        Assert.Equal("luis@example.com", account.Email);
        Assert.Equal("CHECKING", account.AccountType);
        Assert.Equal(5m, account.Balance);
        Assert.Equal(AccountStatus.Blocked, account.Status);
    }

    [Fact]
    public void Apply_WithEmptyPatch_KeepsCurrentValues()
    {
        var account = TestData.Account();

        account.Apply(new AccountPatch(HolderName: " ", Email: ""));

        Assert.Equal("Ana Perez", account.HolderName);
        Assert.Equal("ana.perez@example.com", account.Email);
        Assert.Equal("SAVINGS", account.AccountType);
        Assert.Equal(100m, account.Balance);
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Theory]
    [InlineData("ACTIVE", true)]
    [InlineData("INACTIVE", true)]
    [InlineData("BLOCKED", true)]
    [InlineData("active", false)]
    [InlineData(null, false)]
    public void IsValid_ChecksAllowedStatuses(string? status, bool expected) =>
        Assert.Equal(expected, AccountStatus.IsValid(status));

    [Fact]
    public void AccountNotFoundException_CarriesCatalogCode()
    {
        var ex = new AccountNotFoundException("acc-9");

        Assert.Equal(ErrorCatalog.AccountNotFound, ex.Error);
        Assert.Equal("acc-9", ex.AccountId);
        Assert.Contains("acc-9", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TechnicalException_UsesDefaultMessageWhenNoneGiven()
    {
        var ex = new TechnicalException(ErrorCatalog.CreditScoreUnavailable);

        Assert.Equal(ErrorCatalog.CreditScoreUnavailable.DefaultMessage, ex.Message);
    }
}
