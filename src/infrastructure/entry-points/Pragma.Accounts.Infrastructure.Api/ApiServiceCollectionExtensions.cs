using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Pragma.Accounts.Infrastructure.Api;

public static class ApiServiceCollectionExtensions
{
    /// <summary>ProblemDetails (RFC 7807) + manejador de excepciones del dominio.</summary>
    public static IServiceCollection AddAccountsApi(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ctx =>
            ctx.ProblemDetails.Extensions.TryAdd("traceId", ctx.HttpContext.TraceIdentifier));
        // Los errores de binding (JSON malformado, body muy grande) pasan por el mismo handler.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddExceptionHandler<DomainExceptionHandler>();
        return services;
    }

    /// <summary>Pipeline de errores: excepciones y codigos de estado sin body pasan a ProblemDetails.</summary>
    public static WebApplication UseAccountsApiErrors(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }

    /// <summary>Limite del tamano de body para evitar abuso de memoria.</summary>
    public const long MaxRequestBodyBytes = 64 * 1024;
}
