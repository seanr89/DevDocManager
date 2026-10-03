using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class MetricsTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Metrics_expose_http_request_durations_in_prometheus_format()
    {
        var client = Anonymous();
        await client.GetAsync("/healthz");
        var response = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("http_server_request_duration", await response.Content.ReadAsStringAsync());
    }
}
