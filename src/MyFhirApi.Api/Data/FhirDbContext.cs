using Microsoft.EntityFrameworkCore;

namespace MyFhirApi.Api.Data;

public class FhirDbContext(DbContextOptions<FhirDbContext> options) : DbContext(options)
{
    public DbSet<ResourceEntity> Resources => Set<ResourceEntity>();
    public DbSet<SearchIndexEntity> SearchIndex => Set<SearchIndexEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ResourceEntity>(e =>
        {
            e.ToTable("Resources");
            e.HasKey(r => r.Pk);
            e.Property(r => r.ResourceType).HasMaxLength(64);
            e.Property(r => r.ResourceId).HasMaxLength(64);
            // One row per version, and at most one row per version number.
            e.HasIndex(r => new { r.ResourceType, r.ResourceId, r.VersionId }).IsUnique();
            e.HasIndex(r => new { r.ResourceType, r.IsCurrent, r.IsDeleted });
        });

        modelBuilder.Entity<SearchIndexEntity>(e =>
        {
            e.ToTable("SearchIndex");
            e.Property(i => i.ResourceType).HasMaxLength(64);
            e.Property(i => i.ResourceId).HasMaxLength(64);
            e.Property(i => i.ParamName).HasMaxLength(64);
            e.Property(i => i.StringValue).HasMaxLength(256);
            // FHIR's ":exact" string modifier is case-sensitive, but SQL Server's default collation is not.
            e.Property(i => i.StringExact).HasMaxLength(256).UseCollation("Latin1_General_100_CS_AS");
            e.Property(i => i.TokenSystem).HasMaxLength(256);
            e.Property(i => i.TokenCode).HasMaxLength(256);
            e.Property(i => i.ReferenceValue).HasMaxLength(256);
            e.HasIndex(i => new { i.ResourceType, i.ResourceId });
            e.HasIndex(i => new { i.ResourceType, i.ParamName, i.StringValue });
            e.HasIndex(i => new { i.ResourceType, i.ParamName, i.TokenCode });
            e.HasIndex(i => new { i.ResourceType, i.ParamName, i.ReferenceValue });
        });
    }
}
