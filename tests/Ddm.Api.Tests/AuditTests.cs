using Ddm.Api.Common;
using Ddm.Api.Projects;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class AuditTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Admin_sees_who_did_what_newest_first()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "editor");
        await alice.DeleteAsync("/api/v1/projects/p/members/bob");

        var page = await ReadAsync<Page<AuditEntryDto>>(await alice.GetAsync("/api/v1/projects/p/audit"));
        Assert.Equal(["member.remove", "member.set", "project.create"], page.Items.Select(e => e.Action));
        Assert.All(page.Items, e => Assert.Equal("user:alice", e.Actor));
        Assert.Equal("bob", page.Items[0].Target);
    }

    [Fact]
    public async Task Audit_log_pages_with_a_cursor()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "reader");
        await AddMemberAsync(alice, "p", "carol", "reader");

        var first = await ReadAsync<Page<AuditEntryDto>>(await alice.GetAsync("/api/v1/projects/p/audit?limit=2"));
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.Next);
        var second = await ReadAsync<Page<AuditEntryDto>>(
            await alice.GetAsync($"/api/v1/projects/p/audit?limit=2&cursor={Uri.EscapeDataString(first.Next!)}"));
        Assert.Single(second.Items);
        Assert.Null(second.Next);
    }

    [Fact]
    public async Task Audit_log_is_admin_only()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "ed", "editor");
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientFor("ed").GetAsync("/api/v1/projects/p/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor("stranger").GetAsync("/api/v1/projects/p/audit")).StatusCode);
    }
}
