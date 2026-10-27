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

using System.ComponentModel;
using System.IO;
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// On a multi-user RDS host the Citrix process lookup returns every session's processes, and reading
/// <c>HasExited</c> on another user's process throws (access denied). That exception used to escape
/// the capture scan, kill the discarded capture task and leave the pane spinning for good.
/// </summary>
/// <remarks>
/// The scan is exercised with fake processes. The view cannot be instantiated here, so the wiring
/// (the capture entry point runs through the guard, the scan is used, nothing calls
/// <c>GetProcessesByName</c> directly) is pinned as a source invariant. Mutants proved red, each
/// green once restored: dropping the session comparison, dropping the guard around
/// <c>HasExited</c>, not disposing the process, and removing the catch in <c>CitrixCaptureGuard</c>.
/// </remarks>
public sealed class CitrixProcessScanTests
{
    private const int OwnSession = 2;
    private const int OtherSession = 5;

    [Fact]
    public void AProcessOfAnotherSession_IsSkipped()
    {
        FakeScanProcess mine = new(100, OwnSession);
        FakeScanProcess theirs = new(200, OtherSession);

        Assert.Equal([100], Scan(mine, theirs));
    }

    [Fact]
    public void AnotherUsersProcessWhoseStateIsDenied_DoesNotFailTheScan()
    {
        FakeScanProcess denied = new(200, OwnSession) { HasExitedFailure = new Win32Exception(5, "Access is denied") };
        FakeScanProcess mine = new(100, OwnSession);

        Assert.Equal([100], Scan(denied, mine));
    }

    [Fact]
    public void AProcessThatVanishedDuringTheScan_IsSkipped()
    {
        FakeScanProcess gone = new(200, OwnSession) { HasExitedFailure = new InvalidOperationException("No process") };
        FakeScanProcess mine = new(100, OwnSession);

        Assert.Equal([100], Scan(gone, mine));
    }

    [Fact]
    public void AProcessWhoseSessionCannotBeRead_IsSkipped()
    {
        FakeScanProcess denied = new(200, OwnSession) { SessionFailure = new Win32Exception(5, "Access is denied") };
        FakeScanProcess mine = new(100, OwnSession);

        Assert.Equal([100], Scan(denied, mine));
    }

    [Fact]
    public void AnExitedProcess_IsSkipped()
    {
        FakeScanProcess exited = new(200, OwnSession) { Exited = true };
        FakeScanProcess mine = new(100, OwnSession);

        Assert.Equal([100], Scan(exited, mine));
    }

    [Fact]
    public void EveryProcessReturnedByTheLookup_IsDisposed()
    {
        FakeScanProcess mine = new(100, OwnSession);
        FakeScanProcess theirs = new(200, OtherSession);
        FakeScanProcess denied = new(300, OwnSession) { HasExitedFailure = new Win32Exception(5, "Access is denied") };
        FakeScanProcess exited = new(400, OwnSession) { Exited = true };

        _ = Scan(mine, theirs, denied, exited);

        Assert.All([mine, theirs, denied, exited], p => Assert.True(p.Disposed, $"process {p.Id} was not disposed"));
    }

    [Fact]
    public void ALookupThatFails_DoesNotHideTheOtherNames()
    {
        FakeScanProcess mine = new(100, OwnSession);

        int[] pids = [.. CitrixProcessScan.LivePids(
            ["broken", "wfica32"],
            name => name == "broken"
                ? throw new InvalidOperationException("Performance counters unavailable")
                : [mine],
            OwnSession).Order()];

        Assert.Equal([100], pids);
    }

    [Fact]
    public void EveryNameIsLookedUp_AndTheIdsAreMerged()
    {
        FakeScanProcess first = new(100, OwnSession);
        FakeScanProcess second = new(101, OwnSession);

        int[] pids = [.. CitrixProcessScan.LivePids(
            ["wfica32", "CDViewer"],
            name => name == "wfica32" ? [first] : [second],
            OwnSession).Order()];

        Assert.Equal([100, 101], pids);
    }

    [Fact]
    public async Task AFailingCapture_FallsBackAndDoesNotEscape()
    {
        int fallbacks = 0;

        await CitrixCaptureGuard.RunAsync(
            () => throw new Win32Exception(5, "Access is denied"),
            () =>
            {
                fallbacks++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, fallbacks);
    }

    [Fact]
    public async Task AFailureAfterAnAwait_FallsBackToo()
    {
        int fallbacks = 0;

        await CitrixCaptureGuard.RunAsync(
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("late failure");
            },
            () =>
            {
                fallbacks++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, fallbacks);
    }

    [Fact]
    public async Task ASuccessfulCapture_DoesNotFallBack()
    {
        int fallbacks = 0;

        await CitrixCaptureGuard.RunAsync(
            () => Task.CompletedTask,
            () =>
            {
                fallbacks++;
                return Task.CompletedTask;
            });

        Assert.Equal(0, fallbacks);
    }

    [Fact]
    public async Task AFailingFallback_DoesNotEscapeEither()
    {
        await CitrixCaptureGuard.RunAsync(
            () => throw new InvalidOperationException("capture"),
            () => throw new InvalidOperationException("fallback"));
    }

    [Fact]
    public void TheCaptureEntryPoint_RunsThroughTheGuard_AndTheFallbackShowsExternalMode()
    {
        string code = ViewSource.WithoutCommentsAndLiterals(ReadView());

        string entry = ViewSource.HandlerBody(code, "private async Task TryCaptureWindowAsync()");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                entry,
                "await CitrixCaptureGuard.RunAsync(CaptureWindowAsync, FallBackToExternalModeAfterFailureAsync);"),
            "The capture entry point no longer runs the capture through the guard.");

        string fallback = ViewSource.HandlerBody(code, "private async Task FallBackToExternalModeAfterFailureAsync()");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(fallback, "_captureInProgress = false;"));
        Assert.True(ViewSource.IsStatementOfTheMethodBody(fallback, "await Dispatcher.InvokeAsync(ShowExternalFallbackUnlessDisposed);"));

        string show = ViewSource.HandlerBody(code, "private void ShowExternalFallbackUnlessDisposed()");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(show, "ShowExternalFallback();"));
    }

    [Fact]
    public void TheView_ScansThroughTheSessionFilter_AndNeverEnumeratesProcessesItself()
    {
        string code = ViewSource.WithoutCommentsAndLiterals(ReadView());

        Assert.DoesNotContain("GetProcessesByName", code, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(code, "CitrixProcessScan.LivePidsInCurrentSession("));
    }

    private static int[] Scan(params FakeScanProcess[] processes)
        => [.. CitrixProcessScan.LivePids(["wfica32"], _ => processes, OwnSession).Order()];

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int index = text.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ReadView() => File.ReadAllText(Path.Combine(
        ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedCitrixView.xaml.cs"));

    private sealed class FakeScanProcess(int id, int sessionId) : ICitrixScanProcess
    {
        public int Id { get; } = id;

        public int SessionId => SessionFailure is null ? sessionId : throw SessionFailure;

        public bool HasExited => HasExitedFailure is null ? Exited : throw HasExitedFailure;

        public Exception? SessionFailure { get; init; }

        public Exception? HasExitedFailure { get; init; }

        public bool Exited { get; init; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
