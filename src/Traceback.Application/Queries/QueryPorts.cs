using System.Text;

namespace Traceback.Application.Queries;

/// <summary>Read-side port: work-item to deployment traversal.</summary>
public interface IWorkItemQueries
{
    /// <summary>Returns null when no work item with the given key is known.</summary>
    Task<WorkItemDeploymentResult?> GetDeploymentChainAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Read-side port: per-service/per-environment deployment state and history.</summary>
public interface IServiceQueries
{
    /// <summary>Returns null when the service or environment is unknown.</summary>
    Task<CurrentDeploymentResult?> GetCurrentDeploymentAsync(string serviceName, string environmentName, CancellationToken cancellationToken = default);

    /// <summary>History window; returns null when the service or environment is unknown.</summary>
    Task<DeploymentHistoryResult?> GetDeploymentHistoryAsync(string serviceName, string environmentName, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// Opaque keyset cursor over the unified repository-changes timeline. Encodes
/// the position of the last returned entry (occurrence time, entry kind, entity
/// id) so continuation pages are deterministic and stable under inserts.
/// </summary>
public static class ChangesCursorCodec
{
    public const string KindPullRequest = "pull_request";
    public const string KindCommit = "commit";
    public const string KindWorkflowRun = "workflow_run";

    public static bool IsKnownKind(string kind) => kind is KindPullRequest or KindCommit or KindWorkflowRun;

    public static string Encode(DateTimeOffset occurredAt, string kind, Guid entityId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{occurredAt:O}|{kind}|{entityId:N}"));

    public static bool TryDecode(string? cursor, out DateTimeOffset occurredAt, out string kind, out Guid entityId)
    {
        occurredAt = default;
        kind = "";
        entityId = default;
        if (string.IsNullOrWhiteSpace(cursor))
            return false;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length != 3 || !DateTimeOffset.TryParse(parts[0], out var time) || !Guid.TryParseExact(parts[2], "N", out var id))
                return false;
            if (!IsKnownKind(parts[1]))
                return false;
            occurredAt = time;
            kind = parts[1];
            entityId = id;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Read-side port: repository-scoped source-control traversals.</summary>
public interface ISourceControlQueries
{
    /// <summary>Known source repositories, ordered by provider then key.</summary>
    Task<IReadOnlyList<SourceRepositorySummary>> ListRepositoriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Pull request context with commits and their workflow activity. Null when unknown.</summary>
    Task<PullRequestContextResult?> GetPullRequestContextAsync(string owner, string repo, int number, CancellationToken cancellationToken = default);

    /// <summary>Commit delivery context: containing PRs and executed workflow runs. Null when unknown.</summary>
    Task<CommitDeliveryContextResult?> GetCommitDeliveryContextAsync(string owner, string repo, string sha, CancellationToken cancellationToken = default);

    /// <summary>Time-windowed engineering changes for one repository. Null when the repository is unknown.</summary>
    Task<RepositoryChangesResult?> ListChangesAsync(
        string owner, string repo, DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit, string? cursor,
        CancellationToken cancellationToken = default);
}
