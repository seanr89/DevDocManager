using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class DataLayerTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private DdmDbContext NewDb()
    {
        _ = Anonymous(); // ensure the host (and migrations) have started
        return Factory.Services.CreateScope().ServiceProvider.GetRequiredService<DdmDbContext>();
    }

    [Fact]
    public async Task Migrations_create_the_schema()
    {
        using var db = NewDb();
        Assert.Equal(0, await db.Projects.CountAsync());
        Assert.Equal(0, await db.Versions.CountAsync());
    }

    [Fact]
    public async Task Project_slug_is_unique()
    {
        using var db = NewDb();
        db.Projects.Add(new Project { Slug = "payments", Name = "A" });
        await db.SaveChangesAsync();
        db.Projects.Add(new Project { Slug = "payments", Name = "B" });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.IsUniqueViolation());
    }

    [Fact]
    public async Task Document_path_is_unique_per_project_but_not_across_projects()
    {
        using var db = NewDb();
        var a = new Project { Slug = "a", Name = "A" };
        var b = new Project { Slug = "b", Name = "B" };
        db.Projects.AddRange(a, b);
        db.Documents.AddRange(
            new Document { ProjectId = a.Id, Path = "x.md" },
            new Document { ProjectId = b.Id, Path = "x.md" });
        await db.SaveChangesAsync();
        db.Documents.Add(new Document { ProjectId = a.Id, Path = "x.md" });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.IsUniqueViolation());
    }

    [Fact]
    public async Task Deleting_a_project_cascades_to_members_documents_and_tokens()
    {
        using var db = NewDb();
        var p = new Project { Slug = "p", Name = "P" };
        db.Projects.Add(p);
        db.Members.Add(new Member { ProjectId = p.Id, UserId = "u", Role = Role.Admin });
        db.Documents.Add(new Document { ProjectId = p.Id, Path = "x.md" });
        db.ApiTokens.Add(new ApiToken { ProjectId = p.Id, Name = "t", Scope = TokenScope.Read, HashedSecret = "h", CreatedBy = "u" });
        await db.SaveChangesAsync();

        db.Projects.Remove(p);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.Members.CountAsync());
        Assert.Equal(0, await db.Documents.CountAsync());
        Assert.Equal(0, await db.ApiTokens.CountAsync());
    }

    [Fact]
    public async Task Version_numbers_are_unique_per_item()
    {
        using var db = NewDb();
        var item = Guid.NewGuid();
        ContentVersion V(int n) => new() { ItemType = ItemType.Document, ItemId = item, Number = n, ContentRef = "k", ContentSha256 = "s", Author = "u" };
        db.Versions.Add(V(1));
        await db.SaveChangesAsync();
        db.Versions.Add(V(1));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.IsUniqueViolation());
    }
}
