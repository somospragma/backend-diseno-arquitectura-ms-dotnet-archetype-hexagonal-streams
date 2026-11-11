using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pragma.Accounts.Domain.Model.Exceptions;
using Pragma.Accounts.Domain.UseCases;

namespace Pragma.Accounts.Infrastructure.Api;

/// <summary>
/// Endpoints de cuentas: GET, POST, PUT, PATCH, DELETE, el metodo QUERY
/// (busqueda con body, draft IETF httpbis-safe-method-w-body) y Server-Sent Events.
/// Las colecciones se serializan en streaming desde IAsyncEnumerable.
/// </summary>
public static class AccountEndpoints
{
    public const string BasePath = "/api/v1/accounts";
    private const int DefaultLimit = 20;

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.MapGroup(BasePath).WithTags("Accounts");

        group.MapGet("/", FindAll).WithName("ListAccounts");
        // El segmento literal "events" tiene precedencia sobre "/{id}" en el enrutamiento.
        group.MapGet("/events", Events).WithName("StreamAccountEvents").Produces(StatusCodes.Status200OK, contentType: "text/event-stream");
        group.MapGet("/{id}", FindByIdAsync).WithName("GetAccount").ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/", CreateAsync).WithName("CreateAccount").ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("/{id}", UpdateAsync).WithName("UpdateAccount").ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPatch("/{id}", PatchAsync).WithName("PatchAccount").ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("/{id}", DeleteAsync).WithName("DeleteAccount").ProducesProblem(StatusCodes.Status404NotFound);
        group.MapMethods("/search", ["QUERY"], Search).WithName("SearchAccounts");
        group.MapGet("/{id}/credit-score", CreditScoreAsync).WithName("GetCreditScore")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return routes;
    }

    internal static Ok<IAsyncEnumerable<AccountResponse>> FindAll(
        [FromServices] GetAccountUseCase useCase, CancellationToken ct, [FromQuery] int limit = DefaultLimit) =>
        TypedResults.Ok(useCase.StreamAllAsync(limit, ct).Select(AccountRestMapper.ToResponse));

    /// <summary>Eventos de dominio en vivo (text/event-stream). Opcionalmente filtrados por cuenta.</summary>
    internal static ServerSentEventsResult<AccountEventResponse> Events(
        HttpContext http, [FromServices] StreamAccountEventsUseCase useCase, CancellationToken ct, [FromQuery] string? accountId = null) =>
        TypedResults.ServerSentEvents(
            SubscribeThenFlush(http, useCase.ExecuteAsync(accountId, ct).Select(AccountRestMapper.ToResponse), ct),
            eventType: "account-event");

    /// <summary>
    /// Registra la suscripcion y luego envia los headers. Sin esto la respuesta no arranca
    /// hasta el primer evento (el cliente no confirma la conexion) y, por este orden,
    /// se garantiza que ningun evento posterior a la conexion se pierde.
    /// </summary>
    private static async IAsyncEnumerable<T> SubscribeThenFlush<T>(
        HttpContext http, IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var events = source.GetAsyncEnumerator(ct);
        // MoveNextAsync corre sincronicamente hasta esperar el canal: la suscripcion ya quedo registrada.
        var next = events.MoveNextAsync();
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        while (await next.ConfigureAwait(false))
        {
            yield return events.Current;
            next = events.MoveNextAsync();
        }
    }

    internal static async Task<Ok<AccountResponse>> FindByIdAsync(string id, [FromServices] GetAccountUseCase useCase, CancellationToken ct)
    {
        var account = await useCase.ExecuteAsync(id, ct).ConfigureAwait(false);
        return TypedResults.Ok(AccountRestMapper.ToResponse(account));
    }

    internal static async Task<Results<Created<AccountResponse>, ValidationProblem>> CreateAsync(
        [FromBody] AccountRequest request, [FromServices] CreateAccountUseCase useCase, CancellationToken ct)
    {
        var errors = RequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return Validation(errors);
        }

        var created = await useCase.ExecuteAsync(AccountRestMapper.ToDomain(request), ct).ConfigureAwait(false);
        return TypedResults.Created($"{BasePath}/{created.Id}", AccountRestMapper.ToResponse(created));
    }

    internal static async Task<Results<Ok<AccountResponse>, ValidationProblem>> UpdateAsync(
        string id, [FromBody] AccountRequest request, [FromServices] UpdateAccountUseCase useCase, CancellationToken ct)
    {
        var errors = RequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return Validation(errors);
        }

        var updated = await useCase.ExecuteAsync(id, AccountRestMapper.ToDomain(request), ct).ConfigureAwait(false);
        return TypedResults.Ok(AccountRestMapper.ToResponse(updated));
    }

    internal static async Task<Results<Ok<AccountResponse>, ValidationProblem>> PatchAsync(
        string id, [FromBody] AccountPatchRequest request, [FromServices] PatchAccountUseCase useCase, CancellationToken ct)
    {
        var errors = RequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return Validation(errors);
        }

        var patched = await useCase.ExecuteAsync(id, AccountRestMapper.ToDomain(request), ct).ConfigureAwait(false);
        return TypedResults.Ok(AccountRestMapper.ToResponse(patched));
    }

    internal static async Task<NoContent> DeleteAsync(string id, [FromServices] DeleteAccountUseCase useCase, CancellationToken ct)
    {
        await useCase.ExecuteAsync(id, ct).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    internal static Ok<IAsyncEnumerable<AccountResponse>> Search(
        [FromBody] AccountSearchRequest request, [FromServices] SearchAccountsUseCase useCase, CancellationToken ct,
        [FromQuery] int limit = DefaultLimit) =>
        TypedResults.Ok(useCase.ExecuteAsync(AccountRestMapper.ToDomain(request), limit, ct).Select(AccountRestMapper.ToResponse));

    internal static async Task<Ok<CreditScoreResponse>> CreditScoreAsync(string id, [FromServices] GetCreditScoreUseCase useCase, CancellationToken ct)
    {
        var score = await useCase.ExecuteAsync(id, ct).ConfigureAwait(false);
        return TypedResults.Ok(AccountRestMapper.ToResponse(score));
    }

    private static ValidationProblem Validation(Dictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            title: ErrorCatalog.ValidationError.DefaultMessage,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCatalog.ValidationError.Code });
}
