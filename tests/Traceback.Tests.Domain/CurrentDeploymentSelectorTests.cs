using Traceback.Domain.Entities;
using Traceback.Domain.Policies;

namespace Traceback.Tests.Domain;

public class CurrentDeploymentSelectorTests
{
    private static Deployment Deployment(DateTimeOffset deployedAt, DeploymentStatus status, long sequence = 0) =>
        new()
        {
            DeployedAt = deployedAt,
            Status = status,
            IngestedSequence = sequence,
            Artifact = null!,
            Service = null!,
            Environment = null!,
        };

    [Fact]
    public void Selects_most_recent_successful_deployment()
    {
        var t0 = DateTimeOffset.UtcNow;
        var deployments = new[]
        {
            Deployment(t0.AddHours(-3), DeploymentStatus.Succeeded),
            Deployment(t0, DeploymentStatus.Succeeded, sequence: 7),
        };

        var current = CurrentDeploymentSelector.Select(deployments);

        Assert.NotNull(current);
        Assert.Equal(t0, current!.DeployedAt);
    }

    [Fact]
    public void Ignores_failed_and_unfinished_deployments()
    {
        var t0 = DateTimeOffset.UtcNow;
        var deployments = new[]
        {
            Deployment(t0, DeploymentStatus.Failed, sequence: 9),
            Deployment(t0.AddSeconds(1), DeploymentStatus.InProgress, sequence: 10),
            Deployment(t0.AddSeconds(2), DeploymentStatus.Unknown, sequence: 11),
            Deployment(t0.AddHours(-1), DeploymentStatus.Succeeded, sequence: 8),
        };

        var current = CurrentDeploymentSelector.Select(deployments);

        Assert.Equal(DeploymentStatus.Succeeded, current!.Status);
    }

    [Fact]
    public void Breaks_timestamp_ties_by_ingestion_sequence()
    {
        var t0 = DateTimeOffset.UtcNow;
        var olderObservation = Deployment(t0, DeploymentStatus.Succeeded, sequence: 1);
        var laterObservation = Deployment(t0, DeploymentStatus.Succeeded, sequence: 2);

        var current = CurrentDeploymentSelector.Select([olderObservation, laterObservation]);

        Assert.Equal(2, current!.IngestedSequence);
    }

    [Fact]
    public void Returns_null_when_nothing_succeeded()
    {
        var t0 = DateTimeOffset.UtcNow;
        var deployments = new[] { Deployment(t0, DeploymentStatus.Failed), Deployment(t0, DeploymentStatus.Unknown) };

        var current = CurrentDeploymentSelector.Select(deployments);

        Assert.Null(current);
    }
}
