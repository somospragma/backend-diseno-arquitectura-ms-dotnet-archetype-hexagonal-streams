using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Model.Exceptions;
using Pragma.Accounts.Domain.Ports;

namespace Pragma.Accounts.Infrastructure.CreditScore;

public sealed class CreditScoreOptions
{
    public const string Section = "CreditScore";

    public Uri BaseUrl { get; set; } = new("http://localhost:8081");

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>DTO del proveedor externo: el contrato externo no se filtra al dominio.</summary>
internal sealed record CreditScoreDto(string? AccountId, int Score, string? Rating);

/// <summary>Cliente HTTP del proveedor de puntaje crediticio.</summary>
internal sealed class HttpCreditScoreGateway(HttpClient http) : ICreditScoreGateway
{
    public async Task<Domain.Model.CreditScore> GetAsync(string accountId, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await http.GetFromJsonAsync<CreditScoreDto>(
                    $"/api/v1/credit-scores/{Uri.EscapeDataString(accountId)}", cancellationToken)
                .ConfigureAwait(false);
            return dto is null
                ? throw new TechnicalException(ErrorCatalog.CreditScoreUnavailable, "Empty response from credit score provider")
                : new Domain.Model.CreditScore(accountId, dto.Score, dto.Rating ?? "UNKNOWN");
        }
        catch (HttpRequestException ex)
        {
            throw new TechnicalException(ErrorCatalog.CreditScoreUnavailable, innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout del cliente (no una cancelacion del llamador).
            throw new TechnicalException(ErrorCatalog.CreditScoreUnavailable, "Credit score provider timed out", ex);
        }
    }
}

public static class CreditScoreServiceCollectionExtensions
{
    /// <summary>Registra el cliente HTTP con la pipeline estandar de resiliencia (retry, circuit breaker, timeouts).</summary>
    public static IServiceCollection AddHttpCreditScoreGateway(this IServiceCollection services, CreditScoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddHttpClient<ICreditScoreGateway, HttpCreditScoreGateway>(client =>
            {
                client.BaseAddress = options.BaseUrl;
                client.Timeout = options.Timeout;
            })
            .AddStandardResilienceHandler();
        return services;
    }
}
