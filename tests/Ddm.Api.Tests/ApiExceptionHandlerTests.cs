using Ddm.Api.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ddm.Api.Tests;

public class ApiExceptionHandlerTests
{
    private static async Task<(bool Handled, int Status, string? ContentType, JsonElement Body)> RunAsync(Exception ex, string? accept = null)
    {
        var services = new ServiceCollection().AddLogging().AddDdmProblemDetails().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        if (accept is not null) ctx.Request.Headers.Accept = accept;
        ctx.Response.Body = new MemoryStream();
        var handler = new ApiExceptionHandler(services.GetRequiredService<IProblemDetailsService>());
        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);
        ctx.Response.Body.Position = 0;
        var body = ctx.Response.Body.Length == 0 ? default : await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Response.Body);
        return (handled, ctx.Response.StatusCode, ctx.Response.ContentType, body);
    }

    [Fact]
    public async Task ApiException_becomes_problem_json_with_code()
    {
        var r = await RunAsync(ApiException.Conflict("slug_taken", "Slug taken"));
        Assert.True(r.Handled);
        Assert.Equal(409, r.Status);
        Assert.StartsWith("application/problem+json", r.ContentType);
        Assert.Equal("slug_taken", r.Body.GetProperty("code").GetString());
        Assert.Equal("Slug taken", r.Body.GetProperty("title").GetString());
        Assert.Equal(409, r.Body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Detail_is_included_when_given()
    {
        var r = await RunAsync(ApiException.BadRequest("validation_failed", "Invalid", "name is required"));
        Assert.Equal("name is required", r.Body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Problem_json_is_written_even_when_the_client_accepts_only_other_types()
    {
        var r = await RunAsync(new ApiException(406, "not_acceptable", "Not acceptable"), accept: "application/xml");
        Assert.True(r.Handled);
        Assert.Equal(406, r.Status);
        Assert.StartsWith("application/problem+json", r.ContentType);
        Assert.Equal("not_acceptable", r.Body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Malformed_request_bodies_map_to_400_not_500()
    {
        var r = await RunAsync(new BadHttpRequestException("bad json", 400));
        Assert.True(r.Handled);
        Assert.Equal(400, r.Status);
        Assert.Equal("bad_request", r.Body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_exceptions_are_left_to_the_default_500_path()
    {
        var r = await RunAsync(new InvalidOperationException("boom"));
        Assert.False(r.Handled);
    }


    [Fact]
    public async Task Extensions_such_as_an_errors_list_are_written()
    {
        var r = await RunAsync(ApiException.Unprocessable("invalid_spec", "Invalid", [new { pointer = "/a", line = 3 }]));
        Assert.Equal(422, r.Status);
        Assert.Equal("invalid_spec", r.Body.GetProperty("code").GetString());
        var e = r.Body.GetProperty("errors")[0];
        Assert.Equal("/a", e.GetProperty("pointer").GetString());
        Assert.Equal(3, e.GetProperty("line").GetInt32());
    }

    [Theory]
    [InlineData(401, "unauthenticated")]
    [InlineData(404, "not_found")]
    [InlineData(412, "precondition_failed")]
    [InlineData(422, "unprocessable_content")]
    [InlineData(428, "precondition_required")]
    [InlineData(500, "internal_error")]
    [InlineData(418, "http_418")]
    public void ForStatus_maps_statuses_to_stable_codes(int status, string code) =>
        Assert.Equal(code, ProblemCodes.ForStatus(status));
}
