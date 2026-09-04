/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Heimdall.App.Services;
using Heimdall.App.Services.SessionSnapshot;

namespace Heimdall.App.Tests;

/// <summary>
/// The exit sequence closes the sessions before it first yields, and remembers them as they were
/// before the close. Both halves are asserted from the fake's own record of when it was called,
/// because the order is the whole fix: WPF clears the application singleton at the first await
/// of an asynchronous OnExit, and a close placed after that await runs with nothing to close in.
/// </summary>
public sealed class ApplicationExitSequenceTests
{
    private static readonly TimeSpan GenerousBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OpenSessions_AreClosedBeforeTheSaveIsAwaited_AndSavedAsTheyWere()
    {
        List<SessionSnapshotEntry> open = [Entry("alpha"), Entry("beta")];
        bool closed = false;
        RecordingSnapshotService service = new(() => closed);
        List<string> warnings = [];

        await ApplicationExitSequence.SaveSnapshotAndCloseSessionsAsync(
            () => open.ToArray(),
            () =>
            {
                closed = true;
                open.Clear();
            },
            service,
            GenerousBudget,
            warnings.Add);

        // Closed first. The mutant that closes after the await reads false here.
        Assert.True(service.ClosedWhenSaveStarted);

        // Captured before the close emptied the source. The mutant that captures afterwards
        // saves nothing, and would clear the file instead of writing it.
        Assert.NotNull(service.Saved);
        Assert.Equal(["alpha", "beta"], service.Saved.Select(entry => entry.ServerId));
        Assert.False(service.Cleared);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task NoSessions_ClearsTheSnapshot_AfterClosing()
    {
        bool closed = false;
        RecordingSnapshotService service = new(() => closed);

        await ApplicationExitSequence.SaveSnapshotAndCloseSessionsAsync(
            () => [],
            () => closed = true,
            service,
            GenerousBudget,
            _ => { });

        Assert.True(service.Cleared);
        Assert.True(service.ClosedWhenSaveStarted);
        Assert.Null(service.Saved);
    }

    [Fact]
    public async Task CloseFailure_IsLogged_AndTheSnapshotIsStillWritten()
    {
        RecordingSnapshotService service = new(() => true);
        List<string> warnings = [];

        await ApplicationExitSequence.SaveSnapshotAndCloseSessionsAsync(
            () => [Entry("alpha")],
            () => throw new InvalidOperationException("host refused"),
            service,
            GenerousBudget,
            warnings.Add);

        Assert.NotNull(service.Saved);
        Assert.Contains(
            warnings,
            warning => warning.Contains("session cleanup", StringComparison.Ordinal)
                && warning.Contains("host refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureFailure_IsLogged_AndDoesNotClearAnExistingSnapshot()
    {
        bool closed = false;
        RecordingSnapshotService service = new(() => closed);
        List<string> warnings = [];

        await ApplicationExitSequence.SaveSnapshotAndCloseSessionsAsync(
            () => throw new InvalidOperationException("projection failed"),
            () => closed = true,
            service,
            GenerousBudget,
            warnings.Add);

        // The sessions are still closed, and the file that was there is left alone: an empty
        // capture standing in for a failed one would have deleted it.
        Assert.True(closed);
        Assert.False(service.Cleared);
        Assert.Null(service.Saved);
        Assert.Contains(warnings, warning => warning.Contains("projection failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveOverBudget_IsLoggedAsATimeout_AndDoesNotThrow()
    {
        RecordingSnapshotService service = new(() => true)
        {
            // Ends only when the budget's token fires, so the outcome is the cancellation's, not
            // a race against a fixed window.
            SaveBehaviour = token => Task.Delay(Timeout.InfiniteTimeSpan, token)
        };
        List<string> warnings = [];

        await ApplicationExitSequence.SaveSnapshotAndCloseSessionsAsync(
            () => [Entry("alpha")],
            () => { },
            service,
            TimeSpan.FromMilliseconds(20),
            warnings.Add);

        Assert.Contains(warnings, warning => warning.Contains("timed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoSnapshotService_StillClosesTheSessions_AndCapturesNothing()
    {
        bool closed = false;

        await ApplicationExitSequence.SaveSnapshotAndCloseSessionsAsync(
            () => throw new InvalidOperationException("must not be captured"),
            () => closed = true,
            snapshotService: null,
            GenerousBudget,
            _ => { });

        Assert.True(closed);
    }

    private static SessionSnapshotEntry Entry(string serverId)
        => new() { ServerId = serverId, ConnectionType = "SSH" };

    private sealed class RecordingSnapshotService(Func<bool> closedProbe) : ISessionSnapshotService
    {
        public string SnapshotPath => "in-memory";

        public bool? ClosedWhenSaveStarted { get; private set; }

        public IReadOnlyList<SessionSnapshotEntry>? Saved { get; private set; }

        public bool Cleared { get; private set; }

        public Func<CancellationToken, Task>? SaveBehaviour { get; init; }

        public Task SaveAsync(
            IReadOnlyList<SessionSnapshotEntry> sessions,
            CancellationToken cancellationToken = default)
        {
            ClosedWhenSaveStarted = closedProbe();
            Saved = sessions;
            return SaveBehaviour?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task<SessionSnapshotFile?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SessionSnapshotFile?>(null);

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            ClosedWhenSaveStarted = closedProbe();
            Cleared = true;
            return Task.CompletedTask;
        }
    }
}
