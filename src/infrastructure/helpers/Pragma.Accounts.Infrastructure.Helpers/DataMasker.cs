namespace Pragma.Accounts.Infrastructure.Helpers;

/// <summary>Enmascaramiento de datos sensibles para logs y respuestas.</summary>
public static class DataMasker
{
    /// <summary>j***@dominio.com: conserva la primera letra y el dominio.</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? Full(email) : string.Concat(email.AsSpan(0, 1), "***", email.AsSpan(at));
    }

    /// <summary>****1234: conserva los ultimos cuatro caracteres.</summary>
    public static string MaskLastFour(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= 4 ? Full(value) : string.Concat("****", value.AsSpan(value.Length - 4));
    }

    public static string Full(string? value) => string.IsNullOrEmpty(value) ? string.Empty : "****";
}
