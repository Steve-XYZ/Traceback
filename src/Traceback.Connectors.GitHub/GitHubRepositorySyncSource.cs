using System.Text.Json;
using Microsoft.Extensions.Options;
using Traceback.Connectors.Abstractions;

namespace Traceback.Connectors.GitHub;

/// <summary>
/// Incremental synchronization of one GitHub repository across four resource
/// streams (in order): repository metadata, pull requests, commits, workflow
/// runs (+ their artifacts).
///
/// Cursor/watermark strategy, shaped by what the REST API actually supports:
///
/// - repository: refetched on every pass (one request); its cursor is a
///   constant marking "initialized".
///
/// - pull_requests: the PR list API has no updated-since filter, so the stream
///   walks pages sorted by updated (descending) and stops when items fall at
///   or below watermark minus overlap. Items inside the overlap are re-emitted;
///   ingestion idempotency makes that cheap and honest.
///
/// - commits: the commits list API supports `since` (committer date), applied
///   as watermark minus overlap. Commits reachable only through branches arrive
///   via pull request streams instead. Force-push rewrites can move history
///   behind the watermark; recovery is a manual checkpoint reset (documented).
///
/// - workflow_runs: the runs list supports `created>=`, but reruns bump
///   run_attempt without moving created_at. The stream therefore re-inspects
///   created >= watermark minus overlap AND enumerates all attempts of any run
///   whose run_attempt exceeds 1, so reruns are never reduced to their latest
///   attempt. Artifacts are fetched per run and attached to that run's highest
///   observed attempt (GitHub scopes artifacts to runs, not attempts).
///
/// A pass never advances past truncated data: if the page cap is hit before a
/// stream finishes walking its window, the previous watermark is returned
/// again and the next synchronization redoes the window idempotently.
/// </summary>
internal sealed class GitHubRepositorySyncSource(
    IGitHubApiClient api,
    GitHubRepositorySyncSource.IOptionsMonitorHolder options) : IRepositorySyncSource
{
    public string Provider => "github";

    public IReadOnlyList<string> OrderedResourceTypes { get; } =
        ["repository", "pull_requests", "commits", "workflow_runs"];

    internal interface IOptionsMonitorHolder
    {
        GitHubConnectorOptions Current { get; }
    }

    internal sealed class OptionsMonitorHolder(IOptionsMonitor<GitHubConnectorOptions> monitor) : IOptionsMonitorHolder
    {
        public GitHubConnectorOptions Current => monitor.CurrentValue;
    }

    public async Task<ResourceFetchResult> FetchAsync(ResourceFetchRequest request, CancellationToken cancellationToken = default)
    {
        var (owner, name) = SplitKey(request.RepositoryKey);
        var mapper = new GitHubEventMapper(owner, name);
        return request.ResourceType switch
        {
            "repository" => await FetchRepositoryAsync(owner, name, mapper, cancellationToken),
            "pull_requests" => await FetchPullRequestsAsync(owner, name, mapper, request, cancellationToken),
            "commits" => await FetchCommitsAsync(owner, name, mapper, request, cancellationToken),
            "workflow_runs" => await FetchWorkflowRunsAsync(owner, name, mapper, request, cancellationToken),
            _ => throw new NotSupportedException($"Unknown resource type '{request.ResourceType}'."),
        };
    }

    private static (string Owner, string Name) SplitKey(string key)
    {
        var parts = key.Split('/', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new ArgumentException($"Repository key must look like 'owner/name', got '{key}'.", nameof(key));
        return (parts[0], parts[1]);
    }

    private async Task<ResourceFetchResult> FetchRepositoryAsync(string owner, string name, GitHubEventMapper mapper, CancellationToken ct)
    {
        var repo = await api.GetRepositoryAsync(owner, name, ct);
        return new ResourceFetchResult([mapper.MapRepository(repo)], NextCursor: "initialized") { InspectedCount = 1 };
    }

    private async Task<ResourceFetchResult> FetchPullRequestsAsync(
        string owner, string name, GitHubEventMapper mapper, ResourceFetchRequest request, CancellationToken ct)
    {
        var opts = options.Current;
        var cursor = PullRequestCursor.TryParse(request.Cursor);
        var initial = cursor is null;
        // Walk stops once items age past this floor: initial mode uses the
        // configured lookback depth, incremental mode re-inspects the overlap
        // window behind the watermark.
        var floor = initial
            ? request.Now.AddDays(-request.InitialLookbackDays)
            : cursor!.Value.NotBefore - TimeSpan.FromDays(opts.IncrementalOverlapDays);

        var events = new List<TracebackEvent>();
        var inspected = 0;
        var newestSeen = cursor?.NotBefore ?? DateTimeOffset.MinValue;
        var truncated = false;

        string? nextUrl = null;
        var pagesWalked = 0;
        while (true)
        {
            var page = await api.GetPullRequestsPageAsync(owner, name, nextUrl, opts.PageSize, ct);
            pagesWalked++;
            if (page.Items.Count == 0)
                break;

            foreach (var pr in page.Items)
            {
                inspected++;
                var updatedAt = pr.UpdatedAt ?? pr.CreatedAt ?? DateTimeOffset.MinValue;
                if (updatedAt < floor || (!initial && updatedAt == floor))
                    goto walkComplete;
                if (updatedAt > newestSeen)
                    newestSeen = updatedAt;

                events.AddRange(await MapPullRequestWithCommitsAsync(owner, name, pr, mapper, ct));
            }

            if (!page.HasNext)
                break;
            if (pagesWalked >= opts.MaxPagesPerFetch)
            {
                truncated = true;
                break;
            }
            nextUrl = page.NextUrl;
        }

    walkComplete:
        DateTimeOffset? next;
        if (truncated)
            next = cursor?.NotBefore;
        else if (newestSeen == DateTimeOffset.MinValue)
            next = null;
        else
            next = newestSeen;
        return new ResourceFetchResult(events, PullRequestCursor.Write(next)) { InspectedCount = inspected };
    }

    /// <summary>Fetches a PR's commit membership (authoritative evidence) plus full commit details.</summary>
    private async Task<List<TracebackEvent>> MapPullRequestWithCommitsAsync(
        string owner, string name, GitHubApiPullRequest pr, GitHubEventMapper mapper, CancellationToken ct)
    {
        var pageSize = options.Current.PageSize;
        var shas = new List<string>();
        List<TracebackEvent>? commitEvents = null;

        string? nextUrl = null;
        while (true)
        {
            var page = await api.GetPullRequestCommitsPageAsync(owner, name, pr.Number, nextUrl, pageSize, notFoundAsEmpty: true, cancellationToken: ct);
            if (page is not { } current || current.Items.Count == 0)
                break;
            foreach (var commit in current.Items)
            {
                var evt = mapper.MapCommit(commit);
                shas.Add(evt.Sha);
                (commitEvents ??= []).Add(evt);
            }
            if (!current.HasNext)
                break;
            nextUrl = current.NextUrl;
        }

        return [.. commitEvents ?? [], mapper.MapPullRequest(pr, shas)];
    }

    private async Task<ResourceFetchResult> FetchCommitsAsync(
        string owner, string name, GitHubEventMapper mapper, ResourceFetchRequest request, CancellationToken ct)
    {
        var opts = options.Current;
        var since = CommitsCursor.TryParse(request.Cursor)?.Since
            ?? request.Now.AddDays(-request.InitialLookbackDays);
        var effectiveSince = since - TimeSpan.FromDays(opts.IncrementalOverlapDays);

        var events = new List<TracebackEvent>();
        var inspected = 0;
        var newestSeen = since;
        var truncated = false;

        string? nextUrl = null;
        var pagesWalked = 0;
        while (true)
        {
            DateTimeOffset? sinceArg = effectiveSince == DateTimeOffset.MinValue ? null : effectiveSince;
            var page = await api.GetCommitsPageAsync(owner, name, sinceArg, nextUrl, opts.PageSize, ct);
            pagesWalked++;
            if (page.Items.Count == 0)
                break;

            foreach (var commit in page.Items)
            {
                inspected++;
                var when = commit.Details?.Committer?.Date ?? commit.Details?.Author?.Date ?? DateTimeOffset.MinValue;
                if (when > newestSeen)
                    newestSeen = when;
                events.Add(mapper.MapCommit(commit));
            }

            if (!page.HasNext)
                break;
            if (pagesWalked >= opts.MaxPagesPerFetch)
            {
                truncated = true;
                break;
            }
            nextUrl = page.NextUrl;
        }

        DateTimeOffset? next;
        if (truncated)
            next = since;
        else
            next = newestSeen > since ? newestSeen : since == DateTimeOffset.MinValue ? null : since;
        return new ResourceFetchResult(events, CommitsCursor.Write(next)) { InspectedCount = inspected };
    }

    private async Task<ResourceFetchResult> FetchWorkflowRunsAsync(
        string owner, string name, GitHubEventMapper mapper, ResourceFetchRequest request, CancellationToken ct)
    {
        var opts = options.Current;
        var createdFrom = RunsCursor.TryParse(request.Cursor)?.CreatedFrom
            ?? request.Now.AddDays(-request.InitialLookbackDays);
        var effectiveFrom = createdFrom - TimeSpan.FromDays(opts.IncrementalOverlapDays);

        var events = new List<TracebackEvent>();
        var inspected = 0;
        var newestSeen = createdFrom;
        var truncated = false;

        string? nextUrl = null;
        var pagesWalked = 0;
        while (true)
        {
            DateTimeOffset? fromArg = effectiveFrom == DateTimeOffset.MinValue ? null : effectiveFrom;
            var page = await api.GetWorkflowRunsPageAsync(owner, name, fromArg, nextUrl, opts.PageSize, ct);
            pagesWalked++;
            if (page.Items.Count == 0)
                break;

            foreach (var run in page.Items)
            {
                inspected++;
                var createdAt = run.CreatedAt ?? DateTimeOffset.MinValue;
                if (createdAt > newestSeen)
                    newestSeen = createdAt;

                // Reruns: enumerate every attempt so no attempt's history is
                // rewritten or lost; single-attempt runs emit directly.
                IReadOnlyList<GitHubApiWorkflowRun> attemptRuns = run.RunAttempt > 1
                    ? await api.GetRunAttemptsAsync(owner, name, run.Id, notFoundAsEmpty: true, cancellationToken: ct) ?? []
                    : [run];

                var artifacts = await api.GetRunArtifactsAsync(owner, name, run.Id, notFoundAsEmpty: true, cancellationToken: ct);
                var descriptors = artifacts.Select(mapper.MapArtifact).ToList();

                // Artifacts belong to the run as a whole; attach them to the
                // highest attempt observed in this pass (deterministic rule).
                var orderedAttempts = attemptRuns.OrderBy(a => Math.Max(1, a.RunAttempt)).ToList();
                for (var i = 0; i < orderedAttempts.Count; i++)
                {
                    var isHighest = i == orderedAttempts.Count - 1;
                    events.Add(mapper.MapWorkflowRun(orderedAttempts[i], isHighest ? descriptors : []));
                }
            }

            if (!page.HasNext)
                break;
            if (pagesWalked >= opts.MaxPagesPerFetch)
            {
                truncated = true;
                break;
            }
            nextUrl = page.NextUrl;
        }

        DateTimeOffset? next;
        if (truncated)
            next = createdFrom;
        else
            next = newestSeen > createdFrom ? newestSeen : createdFrom == DateTimeOffset.MinValue ? null : createdFrom;
        return new ResourceFetchResult(events, RunsCursor.Write(next)) { InspectedCount = inspected };
    }
}

internal static class SyncCursors
{
    private static string? Write(string property, DateTimeOffset? value) =>
        value is null ? null : $$"""{"{{property}}":"{{value:O}}"}""";

    private static DateTimeOffset? Parse(string? cursor, string property)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(cursor);
            if (doc.RootElement.TryGetProperty(property, out var element) && element.TryGetDateTimeOffset(out var time))
                return time;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public static string? WritePullRequests(DateTimeOffset? notBefore) => Write("notBefore", notBefore);
    public static DateTimeOffset? ReadPullRequests(string? cursor) => Parse(cursor, "notBefore");
    public static string? WriteCommits(DateTimeOffset? since) => Write("since", since);
    public static DateTimeOffset? ReadCommits(string? cursor) => Parse(cursor, "since");
    public static string? WriteRuns(DateTimeOffset? createdFrom) => Write("createdFrom", createdFrom);
    public static DateTimeOffset? ReadRuns(string? cursor) => Parse(cursor, "createdFrom");
}

internal readonly record struct PullRequestCursor(DateTimeOffset NotBefore)
{
    public static PullRequestCursor? TryParse(string? cursor)
    {
        var time = SyncCursors.ReadPullRequests(cursor);
        return time is null ? null : new PullRequestCursor(time.Value);
    }

    public static string? Write(DateTimeOffset? notBefore) => SyncCursors.WritePullRequests(notBefore);
}

internal readonly record struct CommitsCursor(DateTimeOffset Since)
{
    public static CommitsCursor? TryParse(string? cursor)
    {
        var time = SyncCursors.ReadCommits(cursor);
        return time is null ? null : new CommitsCursor(time.Value);
    }

    public static string? Write(DateTimeOffset? since) => SyncCursors.WriteCommits(since);
}

internal readonly record struct RunsCursor(DateTimeOffset CreatedFrom)
{
    public static RunsCursor? TryParse(string? cursor)
    {
        var time = SyncCursors.ReadRuns(cursor);
        return time is null ? null : new RunsCursor(time.Value);
    }

    public static string? Write(DateTimeOffset? createdFrom) => SyncCursors.WriteRuns(createdFrom);
}
