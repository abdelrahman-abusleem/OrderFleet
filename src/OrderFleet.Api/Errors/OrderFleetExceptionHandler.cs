using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Orders;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Diagnostics;

namespace OrderFleet.Api.Errors;

public sealed class OrderFleetExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<OrderFleetExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception,
        CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            DomainException or DbUpdateConcurrencyException or OperationConflictException => StatusCodes.Status409Conflict,
            ArgumentException or RequestValidationException => StatusCodes.Status400BadRequest,
            ResourceNotFoundException => StatusCodes.Status404NotFound,
            AccessDeniedException => StatusCodes.Status403Forbidden,
            DependencyUnavailableException or ProviderOutcomeUnknownException => StatusCodes.Status503ServiceUnavailable,
            PaymentDeclinedException => StatusCodes.Status402PaymentRequired,
            _ => 0
        };
        if (status == 0)
        {
            logger.LogError("Unhandled request failure {FailureType}; trace {TraceId}", exception.GetType().Name, context.TraceIdentifier);
            status = StatusCodes.Status500InternalServerError;
        }

        if (status == 503) logger.LogWarning("Request failed with {FailureType}; trace {TraceId}", exception.GetType().Name, context.TraceIdentifier);
        else if (status < 500) logger.LogDebug("Request failed with {FailureType}; trace {TraceId}", exception.GetType().Name, context.TraceIdentifier);
        context.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = status switch
            {
                409 => "Request conflicts with the current resource state.",
                404 => "Resource not found.",
                403 => "Access denied.",
                402 => "Payment authorization declined.",
                503 when exception is ProviderOutcomeUnknownException => "Payment outcome is being reconciled.",
                503 => "A required dependency is temporarily unavailable.",
                500 => "An unexpected error occurred.",
                _ => "Invalid request."
            },
            Detail = exception is RequestValidationException ? exception.Message : null
        };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        if (!await problemDetails.TryWriteAsync(new ProblemDetailsContext
        { HttpContext = context, ProblemDetails = problem }))
        {
            await context.Response.WriteAsJsonAsync(problem, options: null,
                contentType: "application/problem+json", cancellationToken: cancellationToken);
        }
        return true;
    }
}
