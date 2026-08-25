using System.Text.Json;
using Traceback.Connectors.Abstractions;

namespace Traceback.Tests.GitHub;

/// <summary>Opaque checkpoint serialization: round-trips, tolerates garbage, stays provider-agnostic.</summary>
public sealed class SyncCursorTests
{
    [Fact]
    public void Cursors_round_trip_through_serialization()
    {
        var time = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(time, Traceback.Connectors.GitHub.PullRequestCursor.TryParse(Traceback.Connectors.GitHub.PullRequestCursor.Write(time))!.Value.NotBefore);
        Assert.Equal(time, Traceback.Connectors.GitHub.CommitsCursor.TryParse(Traceback.Connectors.GitHub.CommitsCursor.Write(time))!.Value.Since);
        Assert.Equal(time, Traceback.Connectors.GitHub.RunsCursor.TryParse(Traceback.Connectors.GitHub.RunsCursor.Write(time))!.Value.CreatedFrom);
    }

    [Fact]
    public void Writing_null_yields_null_and_parsing_garbage_is_tolerated()
    {
        Assert.Null(Traceback.Connectors.GitHub.PullRequestCursor.Write(null));
        Assert.Null(Traceback.Connectors.GitHub.PullRequestCursor.TryParse("not-json-at-all"));
        Assert.Null(Traceback.Connectors.GitHub.CommitsCursor.TryParse("{}"));
        Assert.Null(Traceback.Connectors.GitHub.RunsCursor.TryParse(""));
        Assert.Null(Traceback.Connectors.GitHub.RunsCursor.TryParse(null));
    }
}
