namespace Pragma.Accounts.Domain.Model;

/// <summary>Estados validos de una cuenta.</summary>
public static class AccountStatus
{
    public const string Active = "ACTIVE";
    public const string Inactive = "INACTIVE";
    public const string Blocked = "BLOCKED";

    public static bool IsValid(string? status) =>
        status is Active or Inactive or Blocked;
}

/// <summary>Actualizacion parcial (HTTP PATCH): los campos nulos no se modifican.</summary>
public sealed record AccountPatch(
    string? HolderName = null,
    string? Email = null,
    string? AccountType = null,
    decimal? Balance = null,
    string? Status = null);

/// <summary>Criterios de busqueda (metodo HTTP QUERY). Los campos nulos no filtran.</summary>
public sealed record AccountSearchCriteria(
    string? Status = null,
    string? AccountType = null,
    string? HolderName = null);

/// <summary>Evento de dominio sobre una cuenta.</summary>
public sealed record AccountEvent(string AccountId, string EventId, string Type, DateTimeOffset OccurredAt);

/// <summary>Tipos de evento publicados por el dominio.</summary>
public static class AccountEventTypes
{
    public const string Created = "ACCOUNT_CREATED";
    public const string Updated = "ACCOUNT_UPDATED";
    public const string Deleted = "ACCOUNT_DELETED";
}

/// <summary>Puntaje crediticio obtenido de un proveedor externo.</summary>
public sealed record CreditScore(string AccountId, int Score, string Rating);
