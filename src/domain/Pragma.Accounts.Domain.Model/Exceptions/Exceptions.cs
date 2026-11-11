namespace Pragma.Accounts.Domain.Model.Exceptions;

/// <summary>Codigo de negocio estable: los consumidores reaccionan por Code, no por el mensaje.</summary>
public sealed record ErrorCode(string Code, string DefaultMessage);

/// <summary>Catalogo versionado de codigos de error. Estable entre versiones.</summary>
public static class ErrorCatalog
{
    public static readonly ErrorCode AccountNotFound = new("ACC-404-001", "Account not found");
    public static readonly ErrorCode AccountAlreadyExists = new("ACC-409-001", "Account already exists");
    public static readonly ErrorCode AccountInvalidState = new("ACC-422-001", "Account is in an invalid state for this operation");
    public static readonly ErrorCode ValidationError = new("ACC-400-001", "Request validation failed");
    public static readonly ErrorCode EventPublisherUnavailable = new("ACC-503-001", "Event publisher unavailable");
    public static readonly ErrorCode CreditScoreUnavailable = new("ACC-503-002", "Credit score provider unavailable");
    public static readonly ErrorCode UnexpectedError = new("ACC-500-001", "Unexpected error");
}

/// <summary>Base de las excepciones del dominio.</summary>
public abstract class DomainException : Exception
{
    protected DomainException(ErrorCode error, string? message = null, Exception? innerException = null)
        : base(message ?? error?.DefaultMessage, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public ErrorCode Error { get; }
}

/// <summary>Regla de negocio incumplida: error esperado y recuperable por el cliente.</summary>
public class BusinessException(ErrorCode error, string? message = null) : DomainException(error, message);

/// <summary>Falla tecnica (dependencia caida, timeout): no es culpa del cliente.</summary>
public class TechnicalException(ErrorCode error, string? message = null, Exception? innerException = null)
    : DomainException(error, message, innerException);

/// <summary>La cuenta solicitada no existe.</summary>
public sealed class AccountNotFoundException(string accountId)
    : BusinessException(ErrorCatalog.AccountNotFound, $"Account {accountId} not found")
{
    public string AccountId { get; } = accountId;
}
