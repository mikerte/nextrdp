using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NextRdp.Platform.Application;

namespace NextRdp.ControlPlane;

public sealed class PlatformProblemExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title, detail, errors) = exception switch
        {
            PlatformProblemException problem => (problem.StatusCode, problem.Code, problem.Title, problem.Message, problem.Errors),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "invalid_request_body", "Invalid request body",
                "The request body is malformed or contains a value in an unsupported format.", (IReadOnlyDictionary<string, string[]>)new Dictionary<string, string[]>()),
            _ => (0, string.Empty, string.Empty, string.Empty, (IReadOnlyDictionary<string, string[]>)new Dictionary<string, string[]>())
        };
        if (status == 0) return false;
        httpContext.Response.StatusCode = status;
        httpContext.Response.Headers.CacheControl = "no-store";
        var details = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"https://nextrdp.local/problems/{code}",
            Instance = httpContext.Request.Path
        };
        details.Extensions["code"] = code;
        details.Extensions["correlationId"] = httpContext.TraceIdentifier;
        if (errors.Count > 0) details.Extensions["errors"] = errors;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception
        });
    }
}
