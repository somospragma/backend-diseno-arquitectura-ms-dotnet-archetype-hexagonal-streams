using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Pragma.Accounts.Infrastructure.Helpers;

/// <summary>
/// Propaga X-Correlation-Id: lo toma del request si es valido o genera uno nuevo,
/// lo agrega al scope de logs y lo devuelve en la respuesta.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var incoming = context.Request.Headers[HeaderName].ToString();
        // Solo se acepta un formato acotado: evita inyeccion en logs y cabeceras.
        var correlationId = SafeId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString();

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["correlationId"] = correlationId }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeId();
}

/// <summary>Cabeceras de seguridad para APIs (OWASP Secure Headers).</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            headers["Cache-Control"] = "no-store";
            return Task.CompletedTask;
        });
        return next(context);
    }
}

public static class HelpersApplicationBuilderExtensions
{
    public static IApplicationBuilder UsePragmaHelpers(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>().UseMiddleware<SecurityHeadersMiddleware>();
}
