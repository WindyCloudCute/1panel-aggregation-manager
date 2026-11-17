using Microsoft.EntityFrameworkCore;
using PanelFleet.Infrastructure.Persistence.Entities;

namespace PanelFleet.Infrastructure.Persistence;

public sealed class PanelFleetDbContext : DbContext
{
    public PanelFleetDbContext(DbContextOptions<PanelFleetDbContext> options)
        : base(options)
    {
    }

    public DbSet<NodeEntity> Nodes => Set<NodeEntity>();

    public DbSet<NodeGroupEntity> NodeGroups => Set<NodeGroupEntity>();

    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    public DbSet<AppSettingEntity> AppSettings => Set<AppSettingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<NodeGroupEntity>(entity =>
        {
            entity.ToTable("NodeGroups");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired().HasMaxLength(64).UseCollation("NOCASE");
            entity.HasIndex(item => item.Name).IsUnique();
        });

        modelBuilder.Entity<NodeEntity>(entity =>
        {
            entity.ToTable("Nodes");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired().HasMaxLength(64).UseCollation("NOCASE");
            entity.HasIndex(item => item.Name).IsUnique();
            entity.Property(item => item.BaseUrl).IsRequired().HasMaxLength(2048);
            entity.HasIndex(item => item.BaseUrl).IsUnique();
            entity.Property(item => item.SecurityEntrance).HasMaxLength(64);
            entity.Property(item => item.ApiKeyProtected).IsRequired();
            entity.Property(item => item.ApiKeyHint).IsRequired().HasMaxLength(16);
            entity.Property(item => item.TagsJson).IsRequired().HasDefaultValue("[]");
            entity.HasOne<NodeGroupEntity>()
                .WithMany()
                .HasForeignKey(item => item.GroupId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AuditEventEntity>(entity =>
        {
            entity.ToTable("AuditEvents");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Action).IsRequired().HasMaxLength(64);
            entity.Property(item => item.TargetType).IsRequired().HasMaxLength(64);
            entity.HasIndex(item => item.OccurredAt);
            entity.HasIndex(item => item.NodeId);
        });

        modelBuilder.Entity<AppSettingEntity>(entity =>
        {
            entity.ToTable("AppSettings");
            entity.HasKey(item => item.Key);
        });
    }
}
