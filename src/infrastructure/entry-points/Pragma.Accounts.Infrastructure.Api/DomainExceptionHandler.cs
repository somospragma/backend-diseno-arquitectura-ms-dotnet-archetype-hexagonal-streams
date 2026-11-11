using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Pragma.Accounts.Domain.Model.Exceptions;
using Pragma.Accounts.Infrastructure.Helpers;

namespace Pragma.Accounts.Infrastructure.Api;

/// <summary>
/// Traduce excepciones a RFC 7807 (application/problem+json) con el codigo de negocio
/// en la extension "code". Los errores inesperados no filtran detalles internos.
/// </summary>
internal sealed partial class DomainExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    private static readonly Dictionary<string, int> StatusByCode = new()
    {
        [ErrorCatalog.AccountNotFound.Code] = StatusCodes.Status404NotFound,
        [ErrorCatalog.AccountAlreadyExists.Code] = StatusCodes.Status409Conflict,
        [ErrorCatalog.AccountInvalidState.Code] = StatusCodes.Status422UnprocessableEntity,
        [ErrorCatalog.ValidationError.Code] = StatusCodes.Status400BadRequest,
        [ErrorCatalog.EventPublisherUnavailable.Code] = StatusCodes.Status503ServiceUnavailable,
        [ErrorCatalog.CreditScoreUnavailable.Code] = StatusCodes.Status503ServiceUnavailable,
    };

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var (status, error, detail) = exception switch
        {
            BusinessException business => (StatusFor(business.Error), business.Error, business.Message),
            TechnicalException technical => (StatusFor(technical.Error), technical.Error, technical.Error.DefaultMessage),
            BadHttpRequestException badRequest => (badRequest.StatusCode, ErrorCatalog.ValidationError, "Malformed request"),
            _ => (StatusCodes.Status500InternalServerError, ErrorCatalog.UnexpectedError, ErrorCatalog.UnexpectedError.DefaultMessage),
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogServerError(logger, error.Code, exception);
        }
        else
        {
            LogClientError(logger, error.Code, exception.GetType().Name);
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = error.DefaultMessage,
            Detail = detail,
            Instance = httpContext.Request.Path,
        };
        problem.Extensions["code"] = error.Code;
        if (httpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var correlationId))
        {
            problem.Extensions["correlationId"] = correlationId;
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        }).ConfigureAwait(false);
    }

    private static int StatusFor(ErrorCode error) =>
        StatusByCode.TryGetValue(error.Code, out var status) ? status : StatusCodes.Status500InternalServerError;

    [LoggerMessage(Level = LogLevel.Error, Message = "Request failed with {Code}")]
    private static partial void LogServerError(ILogger logger, string code, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Request rejected with {Code} ({ExceptionType})")]
    private static partial void LogClientError(ILogger logger, string code, string exceptionType);
}
