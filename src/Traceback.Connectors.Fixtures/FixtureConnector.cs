using Traceback.Connectors.Abstractions;

namespace Traceback.Connectors.Fixtures;

/// <summary>
/// Scripted, multi-provider fixture representing the first-milestone chain:
/// BOS-2268 → PR #1842 → commit be82d… → run #98122 → player-manager:be82d →
/// deployed to player-manager/staging.
///
/// Events are deliberately ordered newest-first (deployment before build before
/// commit before pull request before ticket), exactly the shape real webhook
/// storms produce, to exercise out-of-order reconstruction in the seeded stack.
/// </summary>
public sealed class FixtureConnector : IConnector
{
    public const string Name = "fixtures";

    public const string WorkItemKey = "BOS-2268";
    public const string RepositoryFullName = "acme/player-manager";
    public const string PullRequestName = $"{RepositoryFullName}#1842";
    public const string CommitSha = "be82d7f1c0d4e5a6b7c8d9e0f1a2b3c4d5e6f708";
    public const string MergeCommitSha = "ff71a0c9d4e5a6b7c8d9e0f1a2b3c4d5e6f70811";
    public const string WorkflowRunName = $"{RepositoryFullName}/actions/runs/98122/attempts/1";
    public const string ArtifactName = "player-manager";
    public const string ArtifactTag = "be82d";
    public const string ServiceName = "player-manager";
    public const string EnvironmentName = "staging";

    // Earlier, superseded deployment so "current" and "history" are meaningfully different.
    private const string PreviousCommitSha = "aa12e5b3c0d4e5a6b7c8d9e0f1a2b3c4d5e6f708";
    private const string PreviousRunName = $"{RepositoryFullName}/actions/runs/98100/attempts/1";
    private const string PreviousArtifactTag = "aa12e";

    private static readonly DateTimeOffset TicketOpenedAt = new(2026, 08, 17, 09, 12, 00, TimeSpan.Zero);
    private static readonly DateTimeOffset PrOpenedAt = new(2026, 08, 20, 10, 15, 00, TimeSpan.Zero);
    private static readonly DateTimeOffset PrMergedAt = new(2026, 08, 21, 14, 03, 00, TimeSpan.Zero);
    private static readonly DateTimeOffset CommitAuthoredAt = new(2026, 08, 21, 11, 47, 00, TimeSpan.Zero);
    private static readonly DateTimeOffset RunStartedAt = new(2026, 08, 21, 14, 05, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset RunCompletedAt = new(2026, 08, 21, 14, 19, 42, TimeSpan.Zero);
    private static readonly DateTimeOffset DeployedAt = new(2026, 08, 21, 14, 26, 30, TimeSpan.Zero);

    private static readonly DateTimeOffset PreviousCommitAuthoredAt = new(2026, 08, 20, 16, 20, 00, TimeSpan.Zero);
    private static readonly DateTimeOffset PreviousRunCompletedAt = new(2026, 08, 20, 17, 02, 00, TimeSpan.Zero);
    private static readonly DateTimeOffset PreviousDeployedAt = new(2026, 08, 20, 17, 09, 15, TimeSpan.Zero);

    /// <summary>Receive timestamps advance monotonically within one collection pass.</summary>
    private int _sequence;

    string IConnector.Name => Name;

    public async IAsyncEnumerable<TracebackEvent> CollectAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Each collection is a fresh, deterministic replay of the same scenario,
        // so repeated ingestions produce identical fingerprints and dedupe.
        _sequence = 0;
        foreach (var evt in BuildScenario())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }
    }

    internal IReadOnlyList<TracebackEvent> BuildScenario()
    {
        _sequence = 0;
        var linear = "linear";
        var github = "github";
        var docker = "docker";

        var mira = new EngineerRef("Mira Chen", "mira@acme.dev");
        var jonas = new EngineerRef("Jonas Weber", "jonas@acme.dev");

        TracebackEvent Observed(
            string provider, string entityType, string externalKey, string? url,
            DateTimeOffset occurredAt, Func<int, EventProvenance, TracebackEvent> make)
        {
            var observedAt = NextObservedAt();
            return make(_sequence++, new EventProvenance(provider, entityType, externalKey, url, occurredAt, observedAt));
        }

        return
        [
            // --- staging is already running yesterday's build (observed first) ---
            Observed(docker, ExternalEntity.Service, ServiceName, null, PreviousDeployedAt,
                (_, p) => new ServiceObserved(p, ServiceName, "Player roster and progression service", "platform")),
            Observed(docker, ExternalEntity.Environment, EnvironmentName, null, PreviousDeployedAt,
                (_, p) => new EnvironmentObserved(p, EnvironmentName, "staging")),
            Observed(github, ExternalEntity.Repository, RepositoryFullName, null, PreviousDeployedAt,
                (_, p) => new RepositoryObserved(p, RepositoryFullName, "acme/player-manager", "acme", "player-manager",
                    "Internal roster services", "private", "main", "https://github.com/acme/player-manager")),
            Observed(github, ExternalEntity.Commit, $"{RepositoryFullName}@{PreviousCommitSha}", null, PreviousCommitAuthoredAt,
                (_, p) => new CommitObserved(p, PreviousCommitSha, RepositoryFullName, "chore: bump base image", PreviousCommitAuthoredAt, jonas,
                    PreviousCommitAuthoredAt.AddMinutes(3), jonas)),
            Observed(github, ExternalEntity.WorkflowRun, PreviousRunName, null, PreviousDeployedAt,
                (_, p) => new WorkflowRunObserved(p, PreviousRunName, "player-manager-ci", 98100, "completed", "success",
                    PreviousRunCompletedAt.AddMinutes(-14), PreviousRunCompletedAt, PreviousCommitSha,
                    [new ArtifactDescriptor(ArtifactName, PreviousArtifactTag, null, $"registry.acme.dev/{ArtifactName}:{PreviousArtifactTag}")],
                    Repository: RepositoryFullName, RunId: 98100, RunAttempt: 1)),
            Observed(docker, ExternalEntity.BuildArtifact, $"{ArtifactName}@{PreviousArtifactTag}", $"registry.acme.dev/{ArtifactName}:{PreviousArtifactTag}", PreviousDeployedAt,
                (_, p) => new BuildArtifactObserved(p, new ArtifactDescriptor(ArtifactName, PreviousArtifactTag, null, $"registry.acme.dev/{ArtifactName}:{PreviousArtifactTag}"))),
            Observed(docker, ExternalEntity.Deployment, $"{ServiceName}/{EnvironmentName}/{PreviousArtifactTag}", null, PreviousDeployedAt,
                (_, p) => new DeploymentObserved(p, ServiceName, EnvironmentName,
                    new ArtifactDescriptor(ArtifactName, PreviousArtifactTag, null, null),
                    DeploymentOutcome.Succeeded, PreviousDeployedAt,
                    new ExternalRef(github, ExternalEntity.WorkflowRun, PreviousRunName))),

            // --- today's chain arrives newest-first ---
            Observed(docker, ExternalEntity.Deployment, $"{ServiceName}/{EnvironmentName}/{ArtifactTag}", null, DeployedAt,
                (_, p) => new DeploymentObserved(p, ServiceName, EnvironmentName,
                    new ArtifactDescriptor(ArtifactName, ArtifactTag, "sha256:9f1c2a7be82d0f1a2b3c4d5e6f7089aabbccddeeff00112233445566778899aa", $"registry.acme.dev/{ArtifactName}:{ArtifactTag}"),
                    DeploymentOutcome.Succeeded, DeployedAt,
                    new ExternalRef(github, ExternalEntity.WorkflowRun, WorkflowRunName))),
            Observed(docker, ExternalEntity.BuildArtifact, $"{ArtifactName}@{ArtifactTag}", $"registry.acme.dev/{ArtifactName}:{ArtifactTag}", RunCompletedAt,
                (_, p) => new BuildArtifactObserved(p, new ArtifactDescriptor(ArtifactName, ArtifactTag, "sha256:9f1c2a7be82d0f1a2b3c4d5e6f7089aabbccddeeff00112233445566778899aa", $"registry.acme.dev/{ArtifactName}:{ArtifactTag}"))),
            Observed(github, ExternalEntity.WorkflowRun, WorkflowRunName, null, RunCompletedAt,
                (_, p) => new WorkflowRunObserved(p, WorkflowRunName, "player-manager-ci", 98122, "completed", "success",
                    RunStartedAt, RunCompletedAt, CommitSha,
                    [new ArtifactDescriptor(ArtifactName, ArtifactTag, "sha256:9f1c2a7be82d0f1a2b3c4d5e6f7089aabbccddeeff00112233445566778899aa", null)],
                    Repository: RepositoryFullName, RunId: 98122, RunAttempt: 1)),
            Observed(github, ExternalEntity.Commit, $"{RepositoryFullName}@{CommitSha}", null, CommitAuthoredAt,
                (_, p) => new CommitObserved(p, CommitSha, RepositoryFullName, "fix: cache roster per season (#1842)", CommitAuthoredAt, mira,
                    CommitAuthoredAt.AddMinutes(6), mira)),
            Observed(github, ExternalEntity.PullRequest, PullRequestName, "https://github.com/acme/player-manager/pull/1842", PrMergedAt,
                (_, p) => new PullRequestObserved(p, PullRequestName, RepositoryFullName, 1842,
                    "Cache roster responses per season", "merged", "https://github.com/acme/player-manager/pull/1842",
                    PrMergedAt, mira, [CommitSha],
                    CreatedAt: PrOpenedAt, UpdatedAt: PrMergedAt, MergeCommitSha: MergeCommitSha,
                    HeadSha: CommitSha, HeadBranch: "feature/cache-roster", BaseBranch: "main")),
            Observed(linear, ExternalEntity.WorkItem, WorkItemKey, "https://linear.app/acme/issue/BOS-2268", TicketOpenedAt,
                (_, p) => new WorkItemObserved(p, WorkItemKey, "Roster page slow for large seasons",
                    "Roster endpoint re-renders the full table on every request.",
                    "Done", "bug", "https://linear.app/acme/issue/BOS-2268",
                    mira, [new ExternalRef(github, ExternalEntity.PullRequest, PullRequestName)])),
        ];
    }

    private DateTimeOffset NextObservedAt() =>
        new DateTimeOffset(2026, 08, 21, 14, 30, 00, TimeSpan.Zero).AddSeconds(_sequence * 3);
}

/// <summary>External entity type names mirrored locally so fixtures do not reference Infrastructure.</summary>
internal static class ExternalEntity
{
    public const string Service = "service";
    public const string Environment = "environment";
    public const string Repository = "repository";
    public const string Commit = "commit";
    public const string WorkflowRun = "workflow_run";
    public const string BuildArtifact = "build_artifact";
    public const string Deployment = "deployment";
    public const string PullRequest = "pull_request";
    public const string WorkItem = "work_item";
}
