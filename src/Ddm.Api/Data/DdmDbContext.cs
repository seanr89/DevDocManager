using Ddm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Data;

public sealed class DdmDbContext(DbContextOptions<DdmDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<ContentVersion> Versions => Set<ContentVersion>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Spec> Specs => Set<Spec>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TagAssignment> TagAssignments => Set<TagAssignment>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Project>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Slug).HasMaxLength(64).UseCollation("C");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Visibility).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<Member>(e =>
        {
            e.HasKey(x => new { x.ProjectId, x.UserId });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UserId).HasMaxLength(256).UseCollation("C");
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<Document>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Path).HasMaxLength(255).UseCollation("C");
            e.HasIndex(x => new { x.ProjectId, x.Path }).IsUnique();
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.FrontMatter).HasColumnType("jsonb");
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        b.Entity<ContentVersion>(e =>
        {
            e.ToTable("versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.ItemType).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.ItemType, x.ItemId, x.Number }).IsUnique();
            e.Property(x => x.ContentRef).HasMaxLength(512);
            e.Property(x => x.ContentSha256).HasMaxLength(64);
            e.Property(x => x.Author).HasMaxLength(300);
            e.Property(x => x.Message).HasMaxLength(500);
            e.Property(x => x.NormalizedRef).HasMaxLength(512);
        });

        b.Entity<Asset>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Path).HasMaxLength(255).UseCollation("C");
            e.HasIndex(x => new { x.ProjectId, x.Path }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.Sha256 });
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.StorageKey).HasMaxLength(512);
            e.Property(x => x.UpdatedBy).HasMaxLength(300);
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        b.Entity<Spec>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(100).UseCollation("C");
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.ApiVersion).HasMaxLength(100);
            e.Property(x => x.OpenApiVersion).HasMaxLength(16);
            e.Property(x => x.Format).HasMaxLength(8);
            e.Property(x => x.Operations).HasColumnType("jsonb");
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        b.Entity<Tag>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(50).UseCollation("C");
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
        });

        b.Entity<TagAssignment>(e =>
        {
            e.HasKey(x => new { x.TagId, x.ItemType, x.ItemId });
            e.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ItemType).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.ItemType, x.ItemId });
        });

        b.Entity<ApiToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.HashedSecret).HasMaxLength(64);
            e.Property(x => x.CreatedBy).HasMaxLength(300);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.ProjectId, x.Id });
            e.Property(x => x.Actor).HasMaxLength(300);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.Target).HasMaxLength(512);
        });
    }
}
