using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Traceback.Domain.Entities;

namespace Traceback.Infrastructure.Persistence.Configurations;

internal sealed class ExternalIdentityConfiguration : IEntityTypeConfiguration<ExternalIdentity>
{
    public void Configure(EntityTypeBuilder<ExternalIdentity> b)
    {
        b.ToTable("external_identities", t =>
        {
            t.HasCheckConstraint(
                "ck_external_identities_type_match",
                BuildTypeMatchCheckSql());
        });

        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Provider).IsRequired().HasMaxLength(64);
        b.Property(x => x.EntityTypeName).IsRequired().HasMaxLength(64);
        b.Property(x => x.ExternalKey).IsRequired().HasMaxLength(768);

        // The idempotency anchor: the same external object reported twice by the
        // same provider maps to exactly one identity, and therefore one entity.
        b.HasIndex(x => new { x.Provider, x.EntityTypeName, x.ExternalKey }).IsUnique();

        // Exactly one typed FK is non-null; enforced per-type via CHECK constraint.
        b.HasOne(x => x.Engineer).WithMany().HasForeignKey(x => x.EngineerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.SourceRepository).WithMany().HasForeignKey(x => x.SourceRepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.WorkItem).WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.PullRequest).WithMany().HasForeignKey(x => x.PullRequestId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Commit).WithMany().HasForeignKey(x => x.CommitId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.WorkflowRun).WithMany().HasForeignKey(x => x.WorkflowRunId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.BuildArtifact).WithMany().HasForeignKey(x => x.BuildArtifactId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Deployment).WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Service).WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Environment).WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ServiceInstance).WithMany().HasForeignKey(x => x.ServiceInstanceId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.EntityTypeName, x.ExternalKey });
    }

    internal static string BuildTypeMatchCheckSql()
    {
        var fkColumns = new (string Type, string Column)[]
        {
            ("engineer", "engineer_id"),
            ("repository", "source_repository_id"),
            ("work_item", "work_item_id"),
            ("pull_request", "pull_request_id"),
            ("commit", "commit_id"),
            ("workflow_run", "workflow_run_id"),
            ("build_artifact", "build_artifact_id"),
            ("deployment", "deployment_id"),
            ("service", "service_id"),
            ("environment", "environment_id"),
            ("service_instance", "service_instance_id"),
        };

        var allColumns = fkColumns.Select(c => c.Column).ToArray();
        var clauses = fkColumns.Select(c =>
        {
            var others = allColumns
                .Where(col => col != c.Column)
                .Select(col => $"{col} IS NULL");
            return $"(entity_type_name = '{c.Type}' AND {c.Column} IS NOT NULL AND {string.Join(" AND ", others)})";
        });

        return string.Join(" OR ", clauses);
    }
}

internal sealed class ObservationConfiguration : IEntityTypeConfiguration<Observation>
{
    public void Configure(EntityTypeBuilder<Observation> b)
    {
        b.ToTable("observations");
        b.HasKey(x => x.Sequence);

        b.Property(x => x.Sequence).ValueGeneratedOnAdd();
        b.Property(x => x.Provider).IsRequired().HasMaxLength(64);
        b.Property(x => x.EventType).IsRequired().HasMaxLength(128);
        b.Property(x => x.EntityTypeName).IsRequired().HasMaxLength(64);
        b.Property(x => x.ExternalKey).IsRequired().HasMaxLength(768);
        b.Property(x => x.Fingerprint).IsRequired().HasMaxLength(64);

        b.HasIndex(x => x.Fingerprint).IsUnique();
        b.HasIndex(x => new { x.EntityTypeName, x.ExternalKey });
        b.HasIndex(x => x.DeploymentId);
        b.HasIndex(x => x.ObservedAt);

        b.Property(x => x.PayloadJson).HasColumnType("jsonb");

        // Deployment observations retain the provider's raw external key while
        // pointing at the resolved natural deployment. SetNull keeps the
        // append-only evidence log intact if a deployment is ever removed.
        b.HasOne(x => x.Deployment)
            .WithMany()
            .HasForeignKey(x => x.DeploymentId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class SyncStateConfiguration : IEntityTypeConfiguration<SyncState>
{
    public void Configure(EntityTypeBuilder<SyncState> b)
    {
        b.ToTable("sync_states");
        b.HasKey(x => new { x.IntegrationId, x.ResourceType });
        b.Property(x => x.IntegrationId).IsRequired().HasMaxLength(512);
        b.Property(x => x.ResourceType).IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.UpdatedAt);
    }
}

internal sealed class SourceRepositoryConfiguration : IEntityTypeConfiguration<SourceRepository>
{
    public void Configure(EntityTypeBuilder<SourceRepository> b)
    {
        b.ToTable("source_repositories");
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedByProvider).HasMaxLength(64);
        b.Property(x => x.Key).IsRequired().HasMaxLength(512);
        b.Property(x => x.FullName).IsRequired().HasMaxLength(512);
        b.Property(x => x.Owner).HasMaxLength(256);
        b.Property(x => x.Name).HasMaxLength(256);
        b.Property(x => x.Description).HasMaxLength(1024);
        b.Property(x => x.Visibility).HasMaxLength(32);
        b.Property(x => x.DefaultBranch).HasMaxLength(256);
        b.Property(x => x.Url).HasMaxLength(1024);

        // Repository keys are unique per provider; the identity mapping remains
        // the idempotency anchor, this index backs key-based lookups.
        b.HasIndex(x => new { x.CreatedByProvider, x.Key }).IsUnique();
    }
}
