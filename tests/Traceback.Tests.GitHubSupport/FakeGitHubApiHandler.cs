using System.Net;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;

namespace Traceback.Tests.GitHubSupport;

/// <summary>
/// An HttpMessageHandler that serves a <see cref="FakeGitHubRepository"/> as
/// GitHub REST API responses, including Link-header pagination exactly like
/// the real API. Supports scripted failures (rate limits, 5xx, route-scoped
/// failures) and logs every request for assertions.
/// </summary>
public sealed class FakeGitHubApiHandler : HttpMessageHandler
{
    public required FakeGitHubRepository Repository { get; init; }

    /// <summary>
    /// Serves several repositories from one handler; lookups fall back to this
    /// list when <see cref="Repository"/> does not match the request path.
    /// </summary>
    public List<FakeGitHubRepository> ExtraRepositories { get; } = [];

    private FakeGitHubRepository? WorldFor(string[] segments)
    {
        if (segments.Length >= 3 && segments[0] == "repos")
        {
            var (owner, name) = (segments[1], segments[2]);
            if (Repository.Owner == owner && Repository.Name == name)
                return Repository;
            foreach (var extra in ExtraRepositories)
            {
                if (extra.Owner == owner && extra.Name == name)
                    return extra;
            }
            return null;
        }
        return Repository;
    }

    /// <summary>Every request path+query served, in order.</summary>
    public List<string> RequestLog { get; } = [];

    /// <summary>Responses queued to return before any normal handling (status + optional headers).</summary>
    private readonly Queue<(int Status, Dictionary<string, string>? Headers)> _scriptedFailures = new();

    /// <summary>Persistent route failure: requests whose path contains this fragment fail until cleared.</summary>
    public string? FailRouteContaining { get; set; }
    public int FailRouteStatus { get; set; } = 500;

    public void ScriptFailure(int status, Dictionary<string, string>? headers = null) =>
        _scriptedFailures.Enqueue((status, headers));

    public void ClearScriptedFailures() => _scriptedFailures.Clear();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var pathAndQuery = uri.PathAndQuery;
        RequestLog.Add(pathAndQuery);

        if (_scriptedFailures.TryDequeue(out var scripted))
            return Task.FromResult(Response(scripted.Status, "{}", scripted.Headers));

        if (FailRouteContaining is not null && uri.AbsolutePath.Contains(FailRouteContaining, StringComparison.Ordinal))
            return Task.FromResult(Response(FailRouteStatus, "{}"));

        // Strip base-address prefix; work with /api/v3-style paths.
        var path = uri.AbsolutePath;
        foreach (var prefix in new[] { "/api/v3" })
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
                path = path[prefix.Length..];
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var page = int.TryParse(query["page"], out var p) ? Math.Max(1, p) : 1;
        var perPage = int.TryParse(query["per_page"], out var pp) ? pp : 30;

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Route(pathAndQuery, query, page, perPage));
    }

    private HttpResponseMessage Route(string fullPathAndQuery, System.Collections.Specialized.NameValueCollection query, int page, int perPage)
    {
        var path = fullPathAndQuery.Split('?')[0];
        var segments = path.Trim('/').Split('/');

        // GET /repos/{owner}/{name}
        if (segments is ["repos", var owner, var name] && WorldFor(segments) is { } world)
        {
            var body = JsonSerializer.Serialize(new
            {
                id = 1L,
                full_name = world.FullName,
                owner = new { login = world.Owner },
                name = world.Name,
                description = world.Description,
                @private = world.Private,
                default_branch = world.DefaultBranch,
                html_url = $"https://github.com/{world.FullName}",
                created_at = "2026-01-01T00:00:00Z",
                updated_at = world.UpdatedAt.ToString("O"),
                pushed_at = world.PushedAt.ToString("O"),
            });
            return Response(200, body);
        }

        // GET /repos/{o}/{r}/pulls (sorted by UpdatedAt desc)
        if (segments is ["repos", _, _, "pulls"] && query["state"] is not null)
        {
            var items = WorldFor(segments)!.PullRequests.OrderByDescending(pr => pr.UpdatedAt).ToList();
            return ArrayPage(fullPathAndQuery, items, page, perPage, PullRequestJson);
        }

        // GET /repos/{o}/{r}/pulls/{number}/commits
        if (segments is ["repos", _, _, "pulls", var numberText, "commits"] &&
            WorldFor(segments)!.PullRequestCommits.TryGetValue(int.Parse(numberText, System.Globalization.CultureInfo.InvariantCulture), out var prCommits))
        {
            return ArrayPage(fullPathAndQuery, prCommits, page, perPage, CommitJson);
        }

        // GET /repos/{o}/{r}/commits?since=...
        if (segments is ["repos", _, _, "commits"])
        {
            DateTimeOffset? since = DateTimeOffset.TryParse(query["since"], out var s) ? s : null;
            var commits = WorldFor(segments)!.Commits;
            var items = since is null
                ? commits.OrderByDescending(c => c.CommitterDate).ToList()
                : commits.Where(c => c.CommitterDate >= since.Value).OrderByDescending(c => c.CommitterDate).ToList();
            return ArrayPage(path, items, page, perPage, CommitJson);
        }

        // GET /repos/{o}/{r}/actions/runs?created=>=...
        if (segments is ["repos", _, _, "actions", "runs"])
        {
            var createdFilter = query["created"];
            DateTimeOffset? from = null;
            if (createdFilter is not null && createdFilter.StartsWith(">=", StringComparison.Ordinal))
                from = DateTimeOffset.TryParse(createdFilter[2..], out var f) ? f : null;
            // GitHub lists one entry per run id: the latest attempt.
            var allRuns = WorldFor(segments)!.Runs
                .GroupBy(r => r.Id)
                .Select(g => g.OrderByDescending(r => r.RunAttempt).First());
            var items = (from is null ? allRuns : allRuns.Where(r => r.CreatedAt >= from.Value))
                .OrderBy(r => r.Id)
                .ToList();
            var pageItems = items.Skip((page - 1) * perPage).Take(perPage).ToList();
            var hasNext = page * perPage < items.Count;
            var body = JsonSerializer.Serialize(new
            {
                total_count = items.Count,
                workflow_runs = pageItems.Select(RunJson).ToList(),
            });
            return Response(200, body, HeadersFor(fullPathAndQuery, hasNext, page, perPage));
        }

        // GET /repos/{o}/{r}/actions/runs/{id}/attempts (JSON array of runs)
        if (segments is ["repos", _, _, "actions", "runs", var runIdText, "attempts"] &&
            long.TryParse(runIdText, out var runId))
        {
            var attempts = WorldFor(segments)!.RunAttempts.TryGetValue(runId, out var list)
                ? list.OrderBy(a => a.RunAttempt).ToList()
                : [];
            return ArrayPage(path, attempts, page, perPage, RunJson);
        }

        // GET /repos/{o}/{r}/actions/runs/{id}/artifacts
        if (segments is ["repos", _, _, "actions", "runs", var artifactRunId, "artifacts"] &&
            long.TryParse(artifactRunId, out var artifactRun))
        {
            var artifacts = WorldFor(segments)!.Artifacts.TryGetValue(artifactRun, out var arts) ? arts : [];
            var body = JsonSerializer.Serialize(new
            {
                total_count = artifacts.Count,
                artifacts = artifacts.Select(ArtifactJson).ToList(),
            });
            return Response(200, body);
        }

        return Response(404, """{"message":"Not Found"}""");
    }

    private static HttpResponseMessage ArrayPage<T>(
        string path, IReadOnlyList<T> items, int page, int perPage, Func<T, object> shape)
    {
        var pageItems = items.Skip((page - 1) * perPage).Take(perPage).ToList();
        var hasNext = page * perPage < items.Count;
        var body = JsonSerializer.Serialize(pageItems.Select(shape).ToList());
        return Response(200, body, HeadersFor(path, hasNext, page, perPage));
    }

    private static Dictionary<string, string>? HeadersFor(string path, bool hasNext, int page, int perPage)
    {
        if (!hasNext)
            return null;
        // Strip any existing page parameter so links never accumulate duplicates.
        var stripped = Regex.Replace(path, @"([?&])page=\d+", "$1").TrimEnd('&', '?');
        var separator = stripped.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var nextUrl = $"https://api.github.test{stripped}{separator}page={page + 1}";
        return new Dictionary<string, string> { ["Link"] = $"<{nextUrl}>; rel=\"next\"" };
    }

    private static object HeadJson(string @ref, string sha) => new { @ref, sha };
    private static object BaseJson(string @ref) => new { @ref };

    private static object PullRequestJson(FakePullRequest pr) => new
    {
        number = pr.Number,
        title = pr.Title,
        state = pr.State,
        draft = pr.Draft,
        merged = pr.MergedAt is not null,
        merged_at = pr.MergedAt?.ToString("O"),
        closed_at = pr.ClosedAt?.ToString("O"),
        created_at = pr.CreatedAt.ToString("O"),
        updated_at = pr.UpdatedAt.ToString("O"),
        html_url = $"https://github.com/fake/pull/{pr.Number}",
        merge_commit_sha = pr.MergeCommitSha,
        user = new { login = pr.UserLogin },
        head = HeadJson(pr.HeadRef, pr.HeadSha),
        @base = BaseJson(pr.BaseRef),
    };

    private static object CommitJson(FakeCommit c) => new
    {
        sha = c.Sha,
        html_url = $"https://github.com/fake/commit/{c.Sha}",
        commit = new
        {
            message = c.Message,
            author = new { name = c.AuthorName, email = c.AuthorEmail, date = c.AuthorDate.ToString("O") },
            committer = new { name = c.CommitterName, email = c.CommitterEmail, date = c.CommitterDate.ToString("O") },
        },
        author = c.AuthorLogin is null ? null : new { login = c.AuthorLogin },
        committer = c.CommitterLogin is null ? null : new { login = c.CommitterLogin },
        parents = Array.Empty<object>(),
    };

    private static object RunJson(FakeRun r) => new
    {
        id = r.Id,
        name = r.Name,
        workflow_id = r.WorkflowId,
        path = r.Path,
        run_number = r.RunNumber,
        run_attempt = r.RunAttempt,
        @event = r.Event,
        status = r.Status,
        conclusion = r.Conclusion,
        head_branch = r.HeadBranch,
        head_sha = r.HeadSha,
        created_at = r.CreatedAt.ToString("O"),
        updated_at = r.UpdatedAt?.ToString("O"),
        run_started_at = r.RunStartedAt?.ToString("O"),
        html_url = $"https://github.com/fake/actions/runs/{r.Id}/attempts/{r.RunAttempt}",
    };

    private static object ArtifactJson(FakeArtifact a) => new
    {
        id = a.Id,
        name = a.Name,
        size_in_bytes = a.SizeInBytes,
        archive_download_url = $"https://api.github.test/download/{a.Id}",
        expired = a.Expired,
        created_at = a.CreatedAt?.ToString("O"),
        updated_at = a.UpdatedAt?.ToString("O"),
    };

    private static HttpResponseMessage Response(int status, string body, Dictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
                response.Headers.TryAddWithoutValidation(key, value);
        }
        return response;
    }
}
