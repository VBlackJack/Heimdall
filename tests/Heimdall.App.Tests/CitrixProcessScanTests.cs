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
/// (the capture entry point catches and falls back, the scan is used, nothing calls
/// <c>GetProcessesByName</c> directly) is pinned as a source invariant. Mutants proved red, each
/// green once restored: dropping the session comparison, dropping the guard around
/// <c>HasExited</c>, not disposing the process, and removing the catch in the capture entry point.
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
    public void TheCaptureEntryPoint_NeverLetsAFailureEscapeTheDiscardedTask()
    {
        string code = ReadView();
        string entry = ViewSource.HandlerBody(code, "private async Task TryCaptureWindowAsync()");

        int tryIndex = entry.IndexOf("try", StringComparison.Ordinal);
        int callIndex = entry.IndexOf("await CaptureWindowAsync()", StringComparison.Ordinal);
        int catchIndex = entry.IndexOf("catch (Exception", StringComparison.Ordinal);
        int fallbackIndex = entry.IndexOf("FallBackToExternalModeAfterFailureAsync()", StringComparison.Ordinal);

        Assert.True(tryIndex >= 0 && callIndex > tryIndex, "The capture is not inside a try block.");
        Assert.True(catchIndex > callIndex, "The capture has no catch (Exception) after it.");
        Assert.True(fallbackIndex > catchIndex, "The catch does not fall back to external mode.");

        string fallback = ViewSource.HandlerBody(code, "private async Task FallBackToExternalModeAfterFailureAsync()");
        Assert.Contains("_captureInProgress = false;", fallback, StringComparison.Ordinal);
        Assert.Contains("ShowExternalFallback();", fallback, StringComparison.Ordinal);
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
