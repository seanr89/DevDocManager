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

    private static Asset NewAsset(Guid projectId, string path) => new()
    {
        ProjectId = projectId, Path = path, ContentType = "image/png", Size = 1, Sha256 = new string('a', 64),
        StorageKey = "k", UpdatedBy = "u",
    };

    [Fact]
    public async Task Asset_path_spec_name_and_tag_name_are_unique_per_project()
    {
        using var db = NewDb();
        var p = new Project { Slug = "p", Name = "P" };
        db.Projects.Add(p);
        db.Assets.Add(NewAsset(p.Id, "a.png"));
        db.Specs.Add(new Spec { ProjectId = p.Id, Name = "api" });
        db.Tags.Add(new Tag { ProjectId = p.Id, Name = "guide" });
        await db.SaveChangesAsync();

        foreach (var add in new Action[]
                 {
                     () => db.Assets.Add(NewAsset(p.Id, "a.png")),
                     () => db.Specs.Add(new Spec { ProjectId = p.Id, Name = "api" }),
                     () => db.Tags.Add(new Tag { ProjectId = p.Id, Name = "guide" }),
                 })
        {
            db.ChangeTracker.Clear();
            add();
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(ex.IsUniqueViolation());
        }
    }

    [Fact]
    public async Task Deleting_a_project_cascades_to_assets_specs_tags_and_assignments()
    {
        using var db = NewDb();
        var p = new Project { Slug = "p", Name = "P" };
        var tag = new Tag { ProjectId = p.Id, Name = "guide" };
        db.Projects.Add(p);
        db.Assets.Add(NewAsset(p.Id, "a.png"));
        db.Specs.Add(new Spec { ProjectId = p.Id, Name = "api" });
        db.Tags.Add(tag);
        db.TagAssignments.Add(new TagAssignment { TagId = tag.Id, ItemType = ItemType.Project, ItemId = p.Id });
        await db.SaveChangesAsync();

        db.Projects.Remove(p);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.Assets.CountAsync());
        Assert.Equal(0, await db.Specs.CountAsync());
        Assert.Equal(0, await db.Tags.CountAsync());
        Assert.Equal(0, await db.TagAssignments.CountAsync());
    }

    [Fact]
    public async Task A_stale_document_row_cannot_be_saved()
    {
        using var db1 = NewDb();
        using var db2 = NewDb();
        var p = new Project { Slug = "p", Name = "P" };
        var doc = new Document { ProjectId = p.Id, Path = "a.md" };
        db1.Projects.Add(p);
        db1.Documents.Add(doc);
        await db1.SaveChangesAsync();

        var stale = await db2.Documents.SingleAsync();
        doc.Title = "first";
        await db1.SaveChangesAsync();
        stale.Title = "second";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }
}
