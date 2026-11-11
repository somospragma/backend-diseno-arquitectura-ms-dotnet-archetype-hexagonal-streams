using Pragma.Accounts.Domain.Model.Sensitive;

namespace Pragma.Accounts.Domain.Model;

/// <summary>Cuenta bancaria: agregado principal del dominio.</summary>
public sealed class Account
{
    public string Id { get; set; } = string.Empty;

    public string HolderName { get; set; } = string.Empty;

    /// <summary>Dato sensible (PII): se enmascara en logs y respuestas.</summary>
    [Sensitive(SensitiveClassification.Pii, MaskingStrategy.Email)]
    public string Email { get; set; } = string.Empty;

    public string AccountType { get; set; } = string.Empty;

    public decimal Balance { get; set; }

    public string Status { get; set; } = AccountStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Aplica sobre la cuenta solo los campos presentes del parche.</summary>
    public void Apply(AccountPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!string.IsNullOrWhiteSpace(patch.HolderName))
        {
            HolderName = patch.HolderName;
        }

        if (!string.IsNullOrWhiteSpace(patch.Email))
        {
            Email = patch.Email;
        }

        if (!string.IsNullOrWhiteSpace(patch.AccountType))
        {
            AccountType = patch.AccountType;
        }

        if (!string.IsNullOrWhiteSpace(patch.Status))
        {
            Status = patch.Status;
        }

        if (patch.Balance is { } balance)
        {
            Balance = balance;
        }
    }
}
