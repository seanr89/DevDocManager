namespace Ddm.Api.Common;

public static class ProblemDetailsSetup
{
    public static IServiceCollection AddDdmProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            var status = ctx.ProblemDetails.Status ?? ctx.HttpContext.Response.StatusCode;
            ctx.ProblemDetails.Extensions.TryAdd("code", ProblemCodes.ForStatus(status));
        });
        services.AddExceptionHandler<ApiExceptionHandler>();
        return services;
    }
}
