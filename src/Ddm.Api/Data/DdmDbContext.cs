using Ddm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Data;

public sealed class DdmDbContext(DbContextOptions<DdmDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<ContentVersion> Versions => Set<ContentVersion>();
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
