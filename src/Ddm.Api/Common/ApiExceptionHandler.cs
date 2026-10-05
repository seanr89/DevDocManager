using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Ddm.Api.Common;

public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct) => ex switch
    {
        ApiException api => WriteAsync(ctx, ex, api.Status, api.Code, api.Title, api.Detail, api.Extensions),
        // Kestrel enforces the limit set by RequestBody.AllowUpTo mid-read on chunked bodies; answer with the same
        // problem code as RequestBody's own checks.
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } big =>
            WriteAsync(ctx, ex, big.StatusCode, ProblemCodes.ForStatus(big.StatusCode), "The request body is too large", big.Message, null),
        BadHttpRequestException bad => WriteAsync(ctx, ex, bad.StatusCode, "bad_request", "The request could not be read", bad.Message, null),
        _ => ValueTask.FromResult(false),
    };

    private async ValueTask<bool> WriteAsync(HttpContext ctx, Exception ex, int status, string code, string title, string? detail,
        IReadOnlyDictionary<string, object?>? extensions)
    {
        ctx.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"urn:ddm:problem:{code}",
            Extensions = { ["code"] = code },
        };
        if (extensions is not null)
            foreach (var (key, value) in extensions) problem.Extensions[key] = value;
        if (await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = ctx, Exception = ex, ProblemDetails = problem }))
            return true;

        // The default writer honours Accept and declines (e.g. Accept: application/xml), which would send an
        // empty body. Errors are always problem+json with a stable code, so write it regardless.
        await Results.Problem(problem).ExecuteAsync(ctx);
        return true;
    }
}
