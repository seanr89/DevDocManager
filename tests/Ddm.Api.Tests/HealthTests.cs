using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class HealthTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Healthz_returns_ok_without_credentials()
    {
        var response = await Anonymous().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_is_healthy_when_the_database_is_reachable()
    {
        var response = await Anonymous().GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_is_unhealthy_when_the_database_is_unreachable()
    {
        await using var factory = new DdmApiFactory("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=2", migrate: false);
        var response = await factory.CreateClient().GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
