using AirPurifier.Core;
using Microsoft.AspNetCore.Diagnostics;

namespace AirPurifier.Api;

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            return false;

        var (status, title, detail) = exception switch
        {
            PurifierException { Code: PurifierError.DeviceOff } purifier => (409, "Purifier is OFF", purifier.Message),
            PurifierException { Code: PurifierError.RequestTimedOut or PurifierError.CommandUnconfirmed } purifier =>
                (504, "Purifier operation timed out", purifier.Message),
            PurifierException { Code: PurifierError.ConnectionFailed } purifier => (503, "Purifier unavailable", purifier.Message),
            PurifierException purifier => (502, "Purifier response rejected", purifier.Message),
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Invalid request", "Check the request content type and JSON body."),
            _ => (500, "Unexpected server error", "The request could not be completed.")
        };
        logger.LogWarning(exception, "Purifier API request failed with HTTP {StatusCode}", status);
        var extensions = new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier };
        if (exception is PurifierException error)
        {
            extensions["code"] = error.Code.ToString();
            extensions["confirmed"] = false;
            extensions["commandMayHaveSucceeded"] = error.CommandMayHaveSucceeded;
        }
        await Results.Problem(statusCode: status, title: title, detail: detail, extensions: extensions)
            .ExecuteAsync(context);
        return true;
    }
}
