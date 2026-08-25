using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Traceback.Domain.Entities;

namespace Traceback.Infrastructure.Persistence.Configurations;

internal sealed class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedByProvider).HasMaxLength(64);
        b.Property(x => x.Name).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class EnvironmentConfiguration : IEntityTypeConfiguration<DeploymentEnvironment>
{
    public void Configure(EntityTypeBuilder<DeploymentEnvironment> b)
    {
        b.ToTable("environments");
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedByProvider).HasMaxLength(64);
        b.Property(x => x.Name).IsRequired().HasMaxLength(128);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class ServiceInstanceConfiguration : IEntityTypeConfiguration<ServiceInstance>
{
    public void Configure(EntityTypeBuilder<ServiceInstance> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedByProvider).HasMaxLength(64);
        b.Property(x => x.ExternalName).IsRequired().HasMaxLength(512);

        b.HasOne(x => x.Service)
            .WithMany(s => s.Instances)
            .HasForeignKey(x => x.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.Environment)
            .WithMany(e => e.Instances)
            .HasForeignKey(x => x.EnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeploymentConfiguration : IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedByProvider).HasMaxLength(64);

        // Natural key: the same artifact deployed to the same service/environment
        // at the same instant is the same deployment, regardless of which
        // connector reported it. Defense in depth on top of event fingerprints.
        b.HasIndex(x => new { x.ArtifactId, x.ServiceId, x.EnvironmentId, x.DeployedAt }).IsUnique();

        // Primary history access path: history per service/environment, newest first.
        b.HasIndex(x => new { x.ServiceId, x.EnvironmentId, x.DeployedAt })
            .HasMethod("btree")
            .IsDescending(false, false, true);

        b.HasOne(x => x.Artifact)
            .WithMany(a => a.DeployedAs)
            .HasForeignKey(x => x.ArtifactId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Service)
            .WithMany()
            .HasForeignKey(x => x.ServiceId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Environment)
            .WithMany()
            .HasForeignKey(x => x.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.WorkflowRun)
            .WithMany()
            .HasForeignKey(x => x.WorkflowRunId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
