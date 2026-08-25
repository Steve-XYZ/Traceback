using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Traceback.Domain.Entities;

namespace Traceback.Infrastructure.Persistence.Configurations;

internal sealed class EngineerConfiguration : IEntityTypeConfiguration<Engineer>
{
    public void Configure(EntityTypeBuilder<Engineer> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedByProvider).HasMaxLength(64);
        b.Property(x => x.DisplayName).HasMaxLength(256);
        b.Property(x => x.Email).HasMaxLength(320);
        b.HasIndex(x => x.Email).IsUnique().HasFilter("email IS NOT NULL");
    }
}

internal sealed class WorkItemConfiguration : IEntityTypeConfiguration<WorkItem>
{
    public void Configure(EntityTypeBuilder<WorkItem> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedByProvider).HasMaxLength(64);
        b.Property(x => x.Key).IsRequired().HasMaxLength(128);
        b.HasIndex(x => x.Key).IsUnique();
        b.Property(x => x.Url).HasMaxLength(1024);

        b.HasOne(x => x.Assignee)
            .WithMany()
            .HasForeignKey(x => x.AssigneeEngineerId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasMany(x => x.ImplementedBy)
            .WithOne(x => x.WorkItem)
            .HasForeignKey(x => x.WorkItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PullRequestConfiguration : IEntityTypeConfiguration<PullRequest>
{
    public void Configure(EntityTypeBuilder<PullRequest> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedByProvider).HasMaxLength(64);
        b.Property(x => x.ExternalName).IsRequired().HasMaxLength(512);
        b.Property(x => x.Url).HasMaxLength(1024);
        b.HasIndex(x => x.ExternalName);

        // A PR number is only unique within one repository.
        b.HasIndex(x => new { x.SourceRepositoryId, x.Number })
            .IsUnique()
            .HasFilter("source_repository_id IS NOT NULL AND number IS NOT NULL");
        b.HasIndex(x => new { x.SourceRepositoryId, x.UpdatedAt });
        b.HasIndex(x => x.MergedAt);

        b.HasOne(x => x.SourceRepository)
            .WithMany()
            .HasForeignKey(x => x.SourceRepositoryId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne(x => x.Author)
            .WithMany()
            .HasForeignKey(x => x.AuthorEngineerId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasMany(x => x.Implements)
            .WithOne(x => x.PullRequest)
            .HasForeignKey(x => x.PullRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.Contains)
            .WithOne(x => x.PullRequest)
            .HasForeignKey(x => x.PullRequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CommitConfiguration : IEntityTypeConfiguration<Commit>
{
    public void Configure(EntityTypeBuilder<Commit> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedByProvider).HasMaxLength(64);
        b.Property(x => x.Sha).IsRequired().HasMaxLength(64);

        // Commit identity is repository-scoped: the same SHA can exist in
        // several repositories (forks) and relationships must not leak across
        // them. Rows observed without a repository context keep the legacy
        // SHA-only resolution path; PostgreSQL unique indexes treat NULLs as
        // distinct so legacy rows remain valid under this index.
        b.HasIndex(x => new { x.SourceRepositoryId, x.Sha }).IsUnique();
        b.HasIndex(x => x.Sha);
        b.HasIndex(x => new { x.SourceRepositoryId, x.AuthoredAt });

        b.HasOne(x => x.SourceRepository)
            .WithMany()
            .HasForeignKey(x => x.SourceRepositoryId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne(x => x.Author)
            .WithMany()
            .HasForeignKey(x => x.AuthorEngineerId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne(x => x.Committer)
            .WithMany()
            .HasForeignKey(x => x.CommitterEngineerId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasMany(x => x.InPullRequests)
            .WithOne(x => x.Commit)
            .HasForeignKey(x => x.CommitId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WorkflowRunConfiguration : IEntityTypeConfiguration<WorkflowRun>
{
    public void Configure(EntityTypeBuilder<WorkflowRun> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedByProvider).HasMaxLength(64);
        b.Property(x => x.ExternalName).IsRequired().HasMaxLength(512);
        b.Property(x => x.Branch).HasMaxLength(512);
        b.Property(x => x.TriggerEvent).HasMaxLength(64);
        b.Property(x => x.Url).HasMaxLength(1024);

        // One row per provider run id and attempt: reruns add attempts instead
        // of rewriting history.
        b.HasIndex(x => new { x.SourceRepositoryId, x.RunId, x.RunAttempt })
            .IsUnique()
            .HasFilter("source_repository_id IS NOT NULL AND run_id IS NOT NULL");
        b.HasIndex(x => new { x.SourceRepositoryId, x.StartedAt });

        b.HasOne(x => x.SourceRepository)
            .WithMany()
            .HasForeignKey(x => x.SourceRepositoryId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne(x => x.Commit)
            .WithMany(c => c.BuiltBy)
            .HasForeignKey(x => x.CommitId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Produces)
            .WithOne(x => x.WorkflowRun)
            .HasForeignKey(x => x.WorkflowRunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BuildArtifactConfiguration : IEntityTypeConfiguration<BuildArtifact>
{
    public void Configure(EntityTypeBuilder<BuildArtifact> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedByProvider).HasMaxLength(64);
        b.Property(x => x.Name).IsRequired().HasMaxLength(512);
        b.Property(x => x.Version).HasMaxLength(256);
        b.Property(x => x.Digest).HasMaxLength(256);
        b.Property(x => x.CanonicalKey).IsRequired().HasMaxLength(768);
        b.HasIndex(x => x.CanonicalKey).IsUnique();
        b.Property(x => x.Uri).HasMaxLength(1024);
    }
}

internal sealed class JoinTableConfigurations :
    IEntityTypeConfiguration<WorkItemPullRequest>,
    IEntityTypeConfiguration<PullRequestCommit>,
    IEntityTypeConfiguration<WorkflowRunArtifact>
{
    public void Configure(EntityTypeBuilder<WorkItemPullRequest> b)
    {
        b.HasKey(x => new { x.WorkItemId, x.PullRequestId });
        b.HasIndex(x => x.PullRequestId);
    }

    public void Configure(EntityTypeBuilder<PullRequestCommit> b)
    {
        b.HasKey(x => new { x.PullRequestId, x.CommitId });
        b.HasIndex(x => x.CommitId);
    }

    public void Configure(EntityTypeBuilder<WorkflowRunArtifact> b)
    {
        b.HasKey(x => new { x.WorkflowRunId, x.BuildArtifactId });
        b.HasIndex(x => x.BuildArtifactId);
    }
}
