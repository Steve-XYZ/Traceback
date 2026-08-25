using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Traceback.Domain.Entities;

namespace Traceback.Infrastructure.Persistence;

public class TracebackDbContext(DbContextOptions<TracebackDbContext> options) : DbContext(options)
{
    public DbSet<Engineer> Engineers => Set<Engineer>();
    public DbSet<SourceRepository> SourceRepositories => Set<SourceRepository>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<PullRequest> PullRequests => Set<PullRequest>();
    public DbSet<Commit> Commits => Set<Commit>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<BuildArtifact> BuildArtifacts => Set<BuildArtifact>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<DeploymentEnvironment> Environments => Set<DeploymentEnvironment>();
    public DbSet<ServiceInstance> ServiceInstances => Set<ServiceInstance>();

    public DbSet<WorkItemPullRequest> WorkItemPullRequests => Set<WorkItemPullRequest>();
    public DbSet<PullRequestCommit> PullRequestCommits => Set<PullRequestCommit>();
    public DbSet<WorkflowRunArtifact> WorkflowRunArtifacts => Set<WorkflowRunArtifact>();

    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pgcrypto");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TracebackDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>JSON serializer options used for observation payload storage.</summary>
    public static readonly JsonSerializerOptions PayloadSerializerOptions = new(JsonSerializerDefaults.Web);
}
