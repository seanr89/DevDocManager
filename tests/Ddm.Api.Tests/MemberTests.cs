using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Projects;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class MemberTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Added_member_gains_access_with_the_given_role()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "editor");

        var p = await ReadAsync<ProjectDto>(await ClientFor("bob").GetAsync("/api/v1/projects/p"));
        Assert.Equal("editor", p.Role);
    }

    [Fact]
    public async Task Member_ids_with_special_characters_work()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "auth0|abc@example.com", "reader");
        var list = await ReadAsync<Page<MemberDto>>(await alice.GetAsync("/api/v1/projects/p/members"));
        Assert.Contains(list.Items, m => m.UserId == "auth0|abc@example.com" && m.Role == "reader");
    }

    [Fact]
    public async Task Put_on_an_existing_member_changes_their_role()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "editor");
        await AddMemberAsync(alice, "p", "bob", "reader");
        var list = await ReadAsync<Page<MemberDto>>(await alice.GetAsync("/api/v1/projects/p/members"));
        Assert.Equal("reader", list.Items.Single(m => m.UserId == "bob").Role);
    }

    [Theory] [InlineData("owner")] [InlineData("4")] [InlineData("")]
    public async Task Invalid_roles_are_rejected(string role)
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.PutAsJsonAsync("/api/v1/projects/p/members/bob", new { role });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Only_admins_manage_members()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "ed", "editor");
        var ed = ClientFor("ed");
        Assert.Equal(HttpStatusCode.Forbidden, (await ed.GetAsync("/api/v1/projects/p/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ed.PutAsJsonAsync("/api/v1/projects/p/members/x", new { role = "reader" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor("stranger").GetAsync("/api/v1/projects/p/members")).StatusCode);
    }

    [Fact]
    public async Task Removed_member_loses_access_to_a_private_project()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "reader");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/members/bob")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor("bob").GetAsync("/api/v1/projects/p")).StatusCode);
    }

    [Fact]
    public async Task Removing_a_non_member_returns_404()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.DeleteAsync("/api/v1/projects/p/members/ghost");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("member_not_found", await ProblemCodeAsync(r));
    }

    // Review Focus 4
    [Fact]
    public async Task The_last_admin_cannot_be_removed_or_demoted()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");

        var remove = await alice.DeleteAsync("/api/v1/projects/p/members/alice");
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        Assert.Equal("last_admin", await ProblemCodeAsync(remove));

        var demote = await alice.PutAsJsonAsync("/api/v1/projects/p/members/alice", new { role = "editor" });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("last_admin", await ProblemCodeAsync(demote));
    }

    [Fact]
    public async Task An_admin_can_step_down_once_another_admin_exists()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "admin");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/members/alice")).StatusCode);
    }

    // Review Focus 4: two admins removing each other at the same time must leave one.
    [Fact]
    public async Task Two_admins_removing_each_other_concurrently_leave_one_admin()
    {
        var alice = ClientFor("alice");
        var bob = ClientFor("bob");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "admin");

        var results = await Task.WhenAll(
            alice.DeleteAsync("/api/v1/projects/p/members/bob"),
            bob.DeleteAsync("/api/v1/projects/p/members/alice"));

        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.All(results, r => Assert.Contains(r.StatusCode,
            new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict, HttpStatusCode.NotFound }));
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DdmDbContext>();
        Assert.Equal(1, await db.Members.CountAsync(m => m.Role == Role.Admin));
    }
}
