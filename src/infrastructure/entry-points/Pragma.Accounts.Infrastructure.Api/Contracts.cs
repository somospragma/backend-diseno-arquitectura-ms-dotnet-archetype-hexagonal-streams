using System.Net.Mail;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Infrastructure.Helpers;

namespace Pragma.Accounts.Infrastructure.Api;

/// <summary>Body de creacion (POST) y reemplazo (PUT).</summary>
public sealed record AccountRequest(string? HolderName, string? Email, string? AccountType, decimal? Balance, string? Status = null);

/// <summary>Body de actualizacion parcial (PATCH): solo se aplican los campos presentes.</summary>
public sealed record AccountPatchRequest(string? HolderName, string? Email, string? AccountType, decimal? Balance, string? Status);

/// <summary>Body de busqueda (metodo HTTP QUERY).</summary>
public sealed record AccountSearchRequest(string? Status, string? AccountType, string? HolderName);

/// <summary>Representacion publica de una cuenta. El email sale enmascarado.</summary>
public sealed record AccountResponse(
    string Id,
    string HolderName,
    string Email,
    string AccountType,
    decimal Balance,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record CreditScoreResponse(string AccountId, int Score, string Rating);

/// <summary>Evento publicado por Server-Sent Events.</summary>
public sealed record AccountEventResponse(string AccountId, string EventId, string Type, DateTimeOffset OccurredAt);

internal static class AccountRestMapper
{
    public static Account ToDomain(AccountRequest request) => new()
    {
        HolderName = request.HolderName!.Trim(),
        Email = request.Email!.Trim(),
        AccountType = request.AccountType!.Trim(),
        Balance = request.Balance ?? 0m,
        Status = string.IsNullOrWhiteSpace(request.Status) ? AccountStatus.Active : request.Status,
    };

    public static AccountPatch ToDomain(AccountPatchRequest request) =>
        new(request.HolderName?.Trim(), request.Email?.Trim(), request.AccountType?.Trim(), request.Balance, request.Status);

    public static AccountSearchCriteria ToDomain(AccountSearchRequest request) =>
        new(request.Status, request.AccountType, request.HolderName);

    public static AccountResponse ToResponse(Account account) => new(
        account.Id,
        account.HolderName,
        DataMasker.MaskEmail(account.Email),
        account.AccountType,
        account.Balance,
        account.Status,
        account.CreatedAt);

    public static CreditScoreResponse ToResponse(CreditScore score) => new(score.AccountId, score.Score, score.Rating);

    public static AccountEventResponse ToResponse(AccountEvent accountEvent) =>
        new(accountEvent.AccountId, accountEvent.EventId, accountEvent.Type, accountEvent.OccurredAt);
}

/// <summary>Validacion explicita de entrada: errores por campo para el ValidationProblem.</summary>
internal static class RequestValidator
{
    private const int MaxNameLength = 120;
    private const int MaxEmailLength = 254;

    public static Dictionary<string, string[]> Validate(AccountRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        Required(errors, "holderName", request.HolderName, MaxNameLength);
        Required(errors, "accountType", request.AccountType, 30);
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            errors["email"] = ["is required"];
        }
        else
        {
            Email(errors, request.Email);
        }

        Balance(errors, request.Balance);
        Status(errors, request.Status);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(AccountPatchRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.HolderName is { Length: > MaxNameLength })
        {
            errors["holderName"] = [$"must be at most {MaxNameLength} characters"];
        }

        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            Email(errors, request.Email);
        }

        Balance(errors, request.Balance);
        Status(errors, request.Status);
        return errors;
    }

    private static void Required(Dictionary<string, string[]> errors, string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = ["is required"];
        }
        else if (value.Length > maxLength)
        {
            errors[field] = [$"must be at most {maxLength} characters"];
        }
    }

    private static void Email(Dictionary<string, string[]> errors, string email)
    {
        if (email.Length > MaxEmailLength || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email.Trim())
        {
            errors["email"] = ["must be a valid email address"];
        }
    }

    private static void Balance(Dictionary<string, string[]> errors, decimal? balance)
    {
        if (balance is < 0)
        {
            errors["balance"] = ["must be greater than or equal to 0"];
        }
    }

    private static void Status(Dictionary<string, string[]> errors, string? status)
    {
        if (!string.IsNullOrWhiteSpace(status) && !AccountStatus.IsValid(status))
        {
            errors["status"] = [$"must be one of {AccountStatus.Active}, {AccountStatus.Inactive}, {AccountStatus.Blocked}"];
        }
    }
}
