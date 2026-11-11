using Pragma.Accounts.Domain.Model;

namespace Pragma.Accounts.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    public static Account Account(string id = "acc-1", string status = AccountStatus.Active) => new()
    {
        Id = id,
        HolderName = "Ana Perez",
        Email = "ana.perez@example.com",
        AccountType = "SAVINGS",
        Balance = 100m,
        Status = status,
        CreatedAt = Now,
    };
}
