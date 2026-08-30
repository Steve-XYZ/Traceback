using System.Diagnostics;
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
///   attempt. Artifacts come from whichever GitHub listing is cheaper for the
///   pass (see FetchArtifactsAsync). GitHub scopes artifacts to logical runs,
///   not attempts: they attach only when the pass proves a single attempt;
///   ambiguous artifacts remain standalone artifact observations until the
///   model can represent a logical-run association.
///
/// A pass never reports success after truncated data: if the page cap is hit
/// before a stream finishes walking its window, the source throws a typed
/// failure. The synchronizer then leaves the stream checkpoint and batch
/// untouched so a retry cannot mistake the capped window for completion.
/// </summary>
internal sealed class GitHubRepositorySyncSource(
    IGitHubApiClient api,
    GitHubRepositorySyncSource.IOptionsMonitorHolder options) : IRepositorySyncSource
{
    /// <summary>
    /// Shared with the synchronizer so connector fetch/normalize spans nest
    /// under the same <c>github.sync</c> trace.
    /// </summary>
    internal static readonly ActivitySource Activity = new("Traceback.Sync");

    public string Provider => "github";

    public IReadOnlyList<string> OrderedResourceTypes { get; } =
        ["repository", "pull_requests", "commits", "workflow_runs"];

    /// <summary>Span covering DTO-to-event translation only; fetching happens outside it.</summary>
    private static Activity? StartNormalize(string resourceType, int inputCount)
    {
        var span = Activity.StartActivity("traceback.normalize");
        span?.SetTag("traceback.normalize.resource", resourceType);
        span?.SetTag("traceback.normalize.inputs", inputCount);
        return span;
    }

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
        using var normalize = StartNormalize("repository", 1);
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
                throw new GitHubPageLimitException("pull_requests", pagesWalked, opts.MaxPagesPerFetch);
            nextUrl = page.NextUrl;
        }

    walkComplete:
        DateTimeOffset? next = newestSeen == DateTimeOffset.MinValue ? null : newestSeen;
        return new ResourceFetchResult(events, PullRequestCursor.Write(next)) { InspectedCount = inspected };
    }

    /// <summary>Fetches a PR's commit membership (authoritative evidence) plus full commit details.</summary>
    private async Task<List<TracebackEvent>> MapPullRequestWithCommitsAsync(
        string owner, string name, GitHubApiPullRequest pr, GitHubEventMapper mapper, CancellationToken ct)
    {
        var pageSize = options.Current.PageSize;
        var members = new List<GitHubApiCommit>();

        using (var fetchSpan = Activity.StartActivity("github.fetch.pull_request_commits"))
        {
            fetchSpan?.SetTag("traceback.github.pull_request", pr.Number);
            string? nextUrl = null;
            while (true)
            {
                var page = await api.GetPullRequestCommitsPageAsync(owner, name, pr.Number, nextUrl, pageSize, notFoundAsEmpty: true, cancellationToken: ct);
                if (page is not { } current || current.Items.Count == 0)
                    break;
                members.AddRange(current.Items);
                if (!current.HasNext)
                    break;
                nextUrl = current.NextUrl;
            }
            fetchSpan?.SetTag("traceback.github.commits", members.Count);
        }

        using var normalize = StartNormalize("pull_requests", members.Count + 1);
        var shas = new List<string>(members.Count);
        var events = new List<TracebackEvent>(members.Count + 1);
        foreach (var commit in members)
        {
            var evt = mapper.MapCommit(commit);
            shas.Add(evt.Sha);
            events.Add(evt);
        }
        events.Add(mapper.MapPullRequest(pr, shas));
        return events;
    }

    private async Task<ResourceFetchResult> FetchCommitsAsync(
        string owner, string name, GitHubEventMapper mapper, ResourceFetchRequest request, CancellationToken ct)
    {
        var opts = options.Current;
        var cursor = CommitsCursor.TryParse(request.Cursor);
        var initial = cursor is null;
        var since = cursor?.Since ?? request.Now.AddDays(-request.InitialLookbackDays);
        var effectiveSince = initial
            ? since
            : since - TimeSpan.FromDays(opts.IncrementalOverlapDays);

        var events = new List<TracebackEvent>();
        var inspected = 0;
        var newestSeen = cursor?.Since ?? DateTimeOffset.MinValue;

        string? nextUrl = null;
        var pagesWalked = 0;
        while (true)
        {
            DateTimeOffset? sinceArg = effectiveSince == DateTimeOffset.MinValue ? null : effectiveSince;
            var page = await api.GetCommitsPageAsync(owner, name, sinceArg, nextUrl, opts.PageSize, ct);
            pagesWalked++;
            if (page.Items.Count == 0)
                break;

            using (StartNormalize("commits", page.Items.Count))
            {
                foreach (var commit in page.Items)
                {
                    inspected++;
                    var when = commit.Details?.Committer?.Date ?? commit.Details?.Author?.Date ?? DateTimeOffset.MinValue;
                    if (when > newestSeen && (!initial || when >= since))
                        newestSeen = when;
                    events.Add(mapper.MapCommit(commit));
                }
            }

            if (!page.HasNext)
                break;
            if (pagesWalked >= opts.MaxPagesPerFetch)
                throw new GitHubPageLimitException("commits", pagesWalked, opts.MaxPagesPerFetch);
            nextUrl = page.NextUrl;
        }

        DateTimeOffset? next = newestSeen == DateTimeOffset.MinValue ? null : newestSeen;
        return new ResourceFetchResult(events, CommitsCursor.Write(next)) { InspectedCount = inspected };
    }

    private async Task<ResourceFetchResult> FetchWorkflowRunsAsync(
        string owner, string name, GitHubEventMapper mapper, ResourceFetchRequest request, CancellationToken ct)
    {
        var opts = options.Current;
        var cursor = RunsCursor.TryParse(request.Cursor);
        var initial = cursor is null;
        var createdFrom = cursor?.CreatedFrom ?? request.Now.AddDays(-request.InitialLookbackDays);
        var effectiveFrom = initial
            ? createdFrom
            : createdFrom - TimeSpan.FromDays(opts.IncrementalOverlapDays);

        var inspected = 0;
        var newestSeen = cursor?.CreatedFrom ?? DateTimeOffset.MinValue;

        // Collect the attempts first and emit afterwards: knowing how many runs
        // the pass covers is what lets the artifact fetch pick a strategy.
        var runs = new List<(long RunId, List<GitHubApiWorkflowRun> Attempts, bool ArtifactsAttributable)>();

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
                if (createdAt > newestSeen && (!initial || createdAt >= createdFrom))
                    newestSeen = createdAt;

                // Reruns: enumerate every attempt so no attempt's history is
                // rewritten or lost; single-attempt runs need no extra request.
                IReadOnlyList<GitHubApiWorkflowRun> attempts;
                if (run.RunAttempt > 1)
                {
                    using var attemptsSpan = Activity.StartActivity("github.fetch.run_attempts");
                    attemptsSpan?.SetTag("traceback.github.run_id", run.Id);
                    attempts = await api.GetRunAttemptsAsync(owner, name, run.Id, notFoundAsEmpty: true, cancellationToken: ct);
                    attemptsSpan?.SetTag("traceback.github.attempts", attempts.Count);
                    if (attempts.Count == 0)
                        attempts = [run];
                }
                else
                {
                    attempts = [run];
                }

                var orderedAttempts = attempts.OrderBy(a => Math.Max(1, a.RunAttempt)).ToList();
                var artifactsAttributable = Math.Max(1, run.RunAttempt) == 1 &&
                    orderedAttempts.Count == 1 &&
                    Math.Max(1, orderedAttempts[0].RunAttempt) == 1;
                runs.Add((run.Id, orderedAttempts, artifactsAttributable));
            }

            if (!page.HasNext)
                break;
            if (pagesWalked >= opts.MaxPagesPerFetch)
                throw new GitHubPageLimitException("workflow_runs", pagesWalked, opts.MaxPagesPerFetch);
            nextUrl = page.NextUrl;
        }

        var artifactsByRun = await FetchArtifactsAsync(owner, name, runs.ConvertAll(r => r.RunId), ct);

        var events = new List<TracebackEvent>(runs.Sum(r => r.Attempts.Count));
        using (StartNormalize("workflow_runs", runs.Count))
        {
            foreach (var (runId, attempts, artifactsAttributable) in runs)
            {
                var descriptors = artifactsByRun.TryGetValue(runId, out var found)
                    ? artifactsAttributable ? found.ConvertAll(mapper.MapArtifact) : []
                    : [];

                if (!artifactsAttributable && found is { Count: > 0 })
                {
                    var fallbackArtifactTime = attempts.LastOrDefault()?.UpdatedAt ?? attempts.LastOrDefault()?.CreatedAt;
                    foreach (var artifact in found)
                        events.Add(mapper.MapArtifactObserved(artifact, fallbackArtifactTime));
                }

                for (var i = 0; i < attempts.Count; i++)
                {
                    events.Add(mapper.MapWorkflowRun(attempts[i], descriptors));
                }
            }
        }

        DateTimeOffset? next = newestSeen == DateTimeOffset.MinValue ? null : newestSeen;
        return new ResourceFetchResult(events, RunsCursor.Write(next)) { InspectedCount = inspected };
    }

    /// <summary>
    /// Artifacts for every run in this pass. GitHub exposes them two ways and
    /// which is cheaper depends on the shape of the pass: one request per run,
    /// or a repository-wide listing paged 100 at a time. A single probe request
    /// reports the repository's artifact total, which is enough to choose.
    ///
    /// It matters: a 90-day first sync of 3000 runs costs 3000 requests the
    /// per-run way and a handful the repository way, and GitHub allows 5000
    /// requests an hour. A small overlap window is the opposite case, so the
    /// per-run path stays.
    /// </summary>
    private async Task<Dictionary<long, List<GitHubApiArtifact>>> FetchArtifactsAsync(
        string owner, string name, List<long> runIds, CancellationToken ct)
    {
        var byRun = new Dictionary<long, List<GitHubApiArtifact>>();
        if (runIds.Count == 0)
            return byRun;

        var opts = options.Current;
        using var span = Activity.StartActivity("github.fetch.artifacts");
        span?.SetTag("traceback.github.runs", runIds.Count);

        // A single run can never be beaten by a repository-wide walk, so do not
        // spend the probe request on it.
        if (runIds.Count > 1)
        {
            var probe = await api.GetRepositoryArtifactsPageAsync(owner, name, null, opts.PageSize, notFoundAsEmpty: true, cancellationToken: ct);
            var pagesNeeded = probe.TotalCount <= 0 ? 1 : (probe.TotalCount + opts.PageSize - 1) / opts.PageSize;
            if (pagesNeeded <= Math.Min(runIds.Count, opts.MaxPagesPerFetch))
            {
                span?.SetTag("traceback.github.artifact_strategy", "repository");
                var wanted = runIds.ToHashSet();
                var page = probe;
                var walked = 1;
                while (true)
                {
                    foreach (var artifact in page.Items)
                    {
                        // Artifacts of runs outside this pass are ignored; their
                        // runs are not being emitted, so there is nothing to
                        // attach them to.
                        if (artifact.WorkflowRun?.Id is not { } id || !wanted.Contains(id))
                            continue;
                        if (!byRun.TryGetValue(id, out var list))
                            byRun[id] = list = [];
                        list.Add(artifact);
                    }
                    if (!page.HasNext)
                        break;
                    if (walked >= opts.MaxPagesPerFetch)
                        throw new GitHubPageLimitException("workflow_runs", walked, opts.MaxPagesPerFetch);
                    page = await api.GetRepositoryArtifactsPageAsync(owner, name, page.NextUrl, opts.PageSize, notFoundAsEmpty: true, cancellationToken: ct);
                    walked++;
                }
                span?.SetTag("traceback.github.artifact_requests", walked);
                return byRun;
            }
        }

        span?.SetTag("traceback.github.artifact_strategy", "per_run");
        foreach (var runId in runIds)
        {
            var artifacts = await api.GetRunArtifactsAsync(owner, name, runId, notFoundAsEmpty: true, cancellationToken: ct);
            if (artifacts.Count > 0)
                byRun[runId] = [.. artifacts];
        }
        span?.SetTag("traceback.github.artifact_requests", runIds.Count);
        return byRun;
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
