using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Ddm.Api.Common;

public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct) => ex switch
    {
        ApiException api => WriteAsync(ctx, ex, api.Status, api.Code, api.Title, api.Detail),
        BadHttpRequestException bad => WriteAsync(ctx, ex, bad.StatusCode, "bad_request", "The request could not be read", bad.Message),
        _ => ValueTask.FromResult(false),
    };

    private async ValueTask<bool> WriteAsync(HttpContext ctx, Exception ex, int status, string code, string title, string? detail)
    {
        ctx.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = ctx,
            Exception = ex,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Type = $"urn:ddm:problem:{code}",
                Extensions = { ["code"] = code },
            },
        });
    }
}
