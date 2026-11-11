using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Ports;
using Pragma.Accounts.Domain.UseCases;
using Pragma.Accounts.Infrastructure.Cache;
using Pragma.Accounts.Infrastructure.CreditScore;
using Pragma.Accounts.Infrastructure.EventStream;
using Pragma.Accounts.Infrastructure.Messaging;
using Pragma.Accounts.Infrastructure.Persistence;

namespace Pragma.Accounts.Application;

/// <summary>Wiring de casos de uso y adapters. Falla al arrancar si falta configuracion obligatoria.</summary>
public static class ServiceRegistration
{
    public const string ServiceName = "pragma-accounts";

    public static IServiceCollection AddAccountsUseCases(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<CreateAccountUseCase>();
        services.AddScoped<GetAccountUseCase>();
        services.AddScoped<UpdateAccountUseCase>();
        services.AddScoped<PatchAccountUseCase>();
        services.AddScoped<DeleteAccountUseCase>();
        services.AddScoped<SearchAccountsUseCase>();
        services.AddScoped<GetCreditScoreUseCase>();
        services.AddScoped<StreamAccountEventsUseCase>();
        return services;
    }

    public static IServiceCollection AddAccountsAdapters(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPostgresPersistence(Required(configuration.GetConnectionString("Accounts"), "ConnectionStrings:Accounts"));
        services.AddRedisAccountCache(
            Required(configuration.GetConnectionString("Redis"), "ConnectionStrings:Redis"),
            configuration.GetSection(CacheOptions.Section).Get<CacheOptions>() ?? new CacheOptions());
        services.AddSqsEventPublisher(configuration.GetSection(SqsOptions.Section).Get<SqsOptions>() ?? new SqsOptions());
        services.AddChannelEventStream(configuration.GetSection(EventStreamOptions.Section).Get<EventStreamOptions>() ?? new EventStreamOptions());
        // Cada evento de dominio va a SQS (integracion) y al hub en proceso (Server-Sent Events).
        services.AddSingleton<IEventPublisher>(sp => new FanOutEventPublisher(
        [
            sp.GetRequiredKeyedService<IEventPublisher>(MessagingServiceCollectionExtensions.PublisherKey),
            sp.GetRequiredKeyedService<IEventPublisher>(EventStreamServiceCollectionExtensions.PublisherKey),
        ]));
        services.AddHttpCreditScoreGateway(configuration.GetSection(CreditScoreOptions.Section).Get<CreditScoreOptions>() ?? new CreditScoreOptions());
        return services;
    }

    /// <summary>Trazas y metricas OpenTelemetry. Exporta por OTLP solo si OTEL_EXPORTER_OTLP_ENDPOINT esta definido.</summary>
    public static IServiceCollection AddAccountsObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());
        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return services;
    }

    private static string Required(string? value, string key) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Missing required configuration '{key}'")
            : value;
}

/// <summary>Publica cada evento en todos los destinos registrados, en orden.</summary>
internal sealed class FanOutEventPublisher(IReadOnlyList<IEventPublisher> publishers) : IEventPublisher
{
    public async Task PublishAsync(AccountEvent accountEvent, CancellationToken cancellationToken)
    {
        foreach (var publisher in publishers)
        {
            await publisher.PublishAsync(accountEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}
