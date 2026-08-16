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

using System.IO;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// RDP-006. The Citrix launcher is not the session: it exits once Workspace has taken the request,
/// and on a shared session it never owned the window at all. Front, health, terminate and the
/// displayed PID must therefore be driven by the real ICA window and its owning process.
/// </summary>
/// <remarks>
/// No Citrix binary is installed on the build machine, so the operating-system facts are injected
/// and the wiring inside the WPF view - which cannot be instantiated here - is pinned as a source
/// invariant over the four lifecycle paths.
/// </remarks>
public sealed class CitrixSessionHandleTests
{
    private const IntPtr SessionHwnd = 0x1234;
    private const int OwnerPid = 4242;
    private const int OtherPid = 9999;

    [Fact]
    public void TryCreate_LiveSessionWindow_CapturesTheHandleAndItsOwner()
    {
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);

        bool created = CitrixSessionHandle.TryCreate(
            new CitrixSessionWindow(SessionHwnd, OwnerPid),
            "Transparent Windows Client",
            win32.Environment(process),
            out CitrixSessionHandle? handle);

        Assert.True(created);
        Assert.NotNull(handle);
        Assert.Equal(SessionHwnd, handle.Hwnd);
        Assert.Equal(OwnerPid, handle.OwnerProcessId);
        Assert.True(handle.IsAlive);
    }

    [Theory]
    // A window that was never resolved.
    [InlineData(0, OwnerPid)]
    // A null owner id: nothing to open, nothing to terminate.
    [InlineData((int)SessionHwnd, 0)]
    public void TryCreate_UnresolvedWindow_Refuses(int hwnd, int ownerPid)
    {
        FakeWin32 win32 = new((IntPtr)hwnd, ownerPid);

        bool created = CitrixSessionHandle.TryCreate(
            new CitrixSessionWindow((IntPtr)hwnd, ownerPid),
            "Transparent Windows Client",
            win32.Environment(new FakeSessionProcess(ownerPid)),
            out CitrixSessionHandle? handle);

        Assert.False(created);
        Assert.Null(handle);
    }

    [Fact]
    public void TryCreate_OwnerThatCannotBeResolved_Refuses()
    {
        FakeWin32 win32 = new(SessionHwnd, currentOwnerPid: null);

        bool created = CitrixSessionHandle.TryCreate(
            new CitrixSessionWindow(SessionHwnd, OwnerPid),
            "Transparent Windows Client",
            win32.Environment(new FakeSessionProcess(OwnerPid)),
            out CitrixSessionHandle? handle);

        Assert.False(created);
        Assert.Null(handle);
    }

    [Fact]
    public void TryCreate_OwnerInconsistentWithTheCapture_Refuses()
    {
        // The window changed hands between the scan and the adoption. Adopting it anyway would
        // bind the session to a process that never owned the window we found.
        FakeWin32 win32 = new(SessionHwnd, OtherPid);

        bool created = CitrixSessionHandle.TryCreate(
            new CitrixSessionWindow(SessionHwnd, OwnerPid),
            "Transparent Windows Client",
            win32.Environment(new FakeSessionProcess(OtherPid)),
            out CitrixSessionHandle? handle);

        Assert.False(created);
        Assert.Null(handle);
    }

    [Fact]
    public void TryCreate_ProcessAlreadyGone_RefusesAndReleasesIt()
    {
        FakeSessionProcess process = new(OwnerPid) { HasExited = true };
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);

        bool created = CitrixSessionHandle.TryCreate(
            new CitrixSessionWindow(SessionHwnd, OwnerPid),
            "Transparent Windows Client",
            win32.Environment(process),
            out CitrixSessionHandle? handle);

        Assert.False(created);
        Assert.Null(handle);
        Assert.True(process.Disposed);
    }

    [Theory]
    [InlineData("HwndWrapper[SelfService;main;abc]")]
    [InlineData("hwndwrapper[selfservice;main;abc]")]
    public void TryCreate_WorkspaceSignInWindow_IsNeverAdoptedAsTheSession(string windowClassName)
    {
        // The sign-in shell is legitimately embedded while the user authenticates, but it is not
        // the published application: adopting it would make terminate kill Workspace itself.
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);

        bool created = CitrixSessionHandle.TryCreate(
            new CitrixSessionWindow(SessionHwnd, OwnerPid),
            windowClassName,
            win32.Environment(new FakeSessionProcess(OwnerPid)),
            out CitrixSessionHandle? handle);

        Assert.False(created);
        Assert.Null(handle);
    }

    [Fact]
    public void IsAlive_OwningProcessExited_ReadsDead()
    {
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);
        CitrixSessionHandle handle = CreateHandle(win32, process);

        process.HasExited = true;

        Assert.False(handle.IsAlive);
    }

    [Fact]
    public void IsAlive_WindowDestroyed_ReadsDead()
    {
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);
        CitrixSessionHandle handle = CreateHandle(win32, process);

        win32.WindowExists = false;

        Assert.False(handle.IsAlive);
    }

    [Fact]
    public void IsAlive_WindowHandleRecycledByAnotherProcess_ReadsDead()
    {
        // The discriminating case. The handle still exists and the process we opened is still
        // running, so "process alive AND IsWindow" reports a live session - but the window now
        // belongs to someone else, and the session it designated is gone. Only re-reading the
        // owner catches it.
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);
        CitrixSessionHandle handle = CreateHandle(win32, process);

        win32.CurrentOwnerPid = OtherPid;

        Assert.True(win32.WindowExists);
        Assert.False(process.HasExited);
        Assert.False(handle.IsAlive);
    }

    [Fact]
    public void TryGetSessionWindow_LiveSession_YieldsTheRealIcaWindow()
    {
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);
        CitrixSessionHandle handle = CreateHandle(win32, process);

        bool resolved = handle.TryGetSessionWindow(out IntPtr hwnd);

        Assert.True(resolved);
        Assert.Equal(SessionHwnd, hwnd);
    }

    [Fact]
    public void TryGetSessionWindow_DeadSession_YieldsNothingRatherThanAFallbackWindow()
    {
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);
        CitrixSessionHandle handle = CreateHandle(win32, process);

        win32.WindowExists = false;

        Assert.False(handle.TryGetSessionWindow(out IntPtr hwnd));
        Assert.Equal(IntPtr.Zero, hwnd);
    }

    [Fact]
    public void Terminate_LiveSession_KillsTheIcaOwnerOnly()
    {
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);
        CitrixSessionHandle handle = CreateHandle(win32, process);

        Assert.True(handle.Terminate());
        Assert.Equal(1, process.KillCount);
        Assert.Equal(OwnerPid, process.Id);
    }

    [Fact]
    public void Terminate_DeadSession_KillsNothing()
    {
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);
        CitrixSessionHandle handle = CreateHandle(win32, process);

        // Killed via the window disappearing, deliberately NOT via a recycled owner: the recycled
        // case belongs to exactly one oracle, so a regression there points at one line.
        win32.WindowExists = false;

        Assert.False(handle.Terminate());
        Assert.Equal(0, process.KillCount);
    }

    [Fact]
    public void Dispose_ReleasesTheProcessWithoutKillingIt()
    {
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);
        CitrixSessionHandle handle = CreateHandle(win32, process);

        handle.Dispose();

        Assert.True(process.Disposed);
        Assert.Equal(0, process.KillCount);
        Assert.False(handle.IsAlive);
    }

    [Fact]
    public void EmbeddedHealth_AdoptionRefusedForARealSessionWindow_ReadsDead()
    {
        // The failure this rule exists for. A session window was found - so an HWND and an owner
        // PID were both resolved - but adoption refused it, so there is no handle. The window
        // itself still exists, and reading that existence would report a live session backed by
        // nothing: no owning process, no owner revalidation. Fail closed instead.
        bool alive = CitrixSessionHandle.IsEmbeddedWindowAlive(
            handle: null,
            capturedHwnd: SessionHwnd,
            authHwnd: IntPtr.Zero,
            isWindow: _ => true);

        Assert.False(alive);
    }

    [Fact]
    public void EmbeddedHealth_SignInShell_IsTheOnlyWindowJudgedByExistenceAlone()
    {
        // The shell is embedded on purpose during authentication and has no owning ICA process to
        // validate, so its existence is all there is to read - and only for it.
        IntPtr authHwnd = 0x77;

        Assert.True(CitrixSessionHandle.IsEmbeddedWindowAlive(null, authHwnd, authHwnd, _ => true));
        Assert.False(CitrixSessionHandle.IsEmbeddedWindowAlive(null, authHwnd, authHwnd, _ => false));
    }

    [Fact]
    public void EmbeddedHealth_HandleForADifferentWindow_ReadsDead()
    {
        // A stale handle must not vouch for whatever happens to be embedded now.
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);
        CitrixSessionHandle handle = CreateHandle(win32, process);

        Assert.True(handle.IsAlive);
        Assert.False(CitrixSessionHandle.IsEmbeddedWindowAlive(handle, 0x99, IntPtr.Zero, _ => true));
    }

    [Fact]
    public void EmbeddedHealth_ValidatedHandleForThisWindow_DefersToTheHandle()
    {
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);
        CitrixSessionHandle handle = CreateHandle(win32, process);

        // The probe says the window is gone and the handle says the session is live: the handle
        // wins, which is what "defers to the handle" means. The handle's own liveness rules,
        // recycling included, are pinned by the IsAlive oracles above and deliberately not
        // re-observed here, so a regression there points at one line rather than several.
        Assert.True(CitrixSessionHandle.IsEmbeddedWindowAlive(
            handle, SessionHwnd, IntPtr.Zero, _ => false));
    }

    [Fact]
    public void EmbeddedHealth_NothingEmbedded_ReadsDead()
        => Assert.False(CitrixSessionHandle.IsEmbeddedWindowAlive(
            null, IntPtr.Zero, IntPtr.Zero, _ => true));

    [Fact]
    public void ExternalMode_WithoutAnAdoptedSession_IsNotAConnectedOutcome()
    {
        // Reached by the capture timeout and by the user cancelling the search. Nothing was
        // adopted, so there is no session to be "external" about: reporting connected here would
        // fabricate liveness, and the health tick would contradict it on the next poll.
        Assert.False(CitrixSessionHandle.IsExternalModeConnected(null));
    }

    [Fact]
    public void ExternalMode_WithALiveSessionAfterAFailedEmbed_StaysConnected()
    {
        // The one path that arrives with a handle: the session exists and is running, Win32 simply
        // refused to reparent its window. That is a genuine connected session in its own window.
        FakeSessionProcess process = new(OwnerPid);
        FakeWin32 win32 = new(SessionHwnd, OwnerPid);
        CitrixSessionHandle handle = CreateHandle(win32, process);

        Assert.True(CitrixSessionHandle.IsExternalModeConnected(handle));

        // And it stops being connected exactly when the session does.
        process.HasExited = true;
        Assert.False(CitrixSessionHandle.IsExternalModeConnected(handle));
    }

    /// <summary>
    /// A refused adoption must stop the embed, not merely be logged.
    /// </summary>
    /// <remarks>
    /// The health rule above closes the reporting half of the defect; this closes the other half.
    /// Both capture paths must gate <c>EmbedWindow</c> on the adoption, and the post-auth path must
    /// additionally adopt before releasing the sign-in window, or a refusal would hand that window
    /// back for a session it could not drive.
    /// </remarks>
    [Fact]
    public void CaptureAndPostAuthPaths_EmbedOnlyAfterASuccessfulAdoption()
    {
        string source = ReadAppSource("Views/EmbeddedCitrixView.xaml.cs");

        foreach (string signature in new[]
        {
            "private async Task TryCaptureWindowAsync(",
            "private async Task WatchForSessionAfterAuthAsync("
        })
        {
            string body = ExtractMethodBody(source, signature);

            int adoptionGuard = body.IndexOf("if (!AdoptSessionWindow(", StringComparison.Ordinal);
            Assert.True(adoptionGuard >= 0, $"No adoption guard in {signature}");

            // Every session embed in these bodies sits after that guard. The sign-in embed is the
            // one exception and is asserted separately below.
            foreach (int embedIndex in IndexesOf(body, "EmbedWindow("))
            {
                bool isSignInEmbed = body.LastIndexOf(
                    "_authHwnd = authHwnd;",
                    embedIndex,
                    StringComparison.Ordinal) >= 0
                    && body[..embedIndex].Contains("_authHwnd = authHwnd;", StringComparison.Ordinal);

                Assert.True(
                    isSignInEmbed || embedIndex > adoptionGuard,
                    $"Unguarded EmbedWindow at offset {embedIndex} in {signature}");
            }
        }

        // Adoption precedes the release in the post-auth swap.
        string postAuth = ExtractMethodBody(source, "private async Task WatchForSessionAfterAuthAsync(");
        Assert.True(
            postAuth.IndexOf("if (!AdoptSessionWindow(", StringComparison.Ordinal)
                < postAuth.IndexOf("ReleaseEmbeddedWindow();", StringComparison.Ordinal),
            "The sign-in window is released before the replacement session is adopted.");

        // And the fail-closed health decision is the one the view asks.
        Assert.Contains(
            "bool windowAlive = CitrixSessionHandle.IsEmbeddedWindowAlive(",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The external fallback must not announce a connection it has no session for.
    /// </summary>
    /// <remarks>
    /// The gate and the emission both live in a WPF method, so the ordering between them is pinned
    /// here: an emission that stopped being gated would otherwise still satisfy every behavioural
    /// test, because <c>EmitConnect</c> cannot be observed from this project.
    /// </remarks>
    [Fact]
    public void ExternalFallback_EmitsConnectedOnlyBehindTheSessionGate()
    {
        string body = ExtractMethodBody(
            ReadAppSource("Views/EmbeddedCitrixView.xaml.cs"),
            "private void ShowExternalFallback()");

        int gate = body.IndexOf(
            "bool connected = CitrixSessionHandle.IsExternalModeConnected(_sessionHandle);",
            StringComparison.Ordinal);
        int refusal = body.IndexOf("if (!connected)", StringComparison.Ordinal);
        int emission = body.IndexOf("EmitConnect();", StringComparison.Ordinal);

        // Asserted present before being ordered: a missing gate yields -1, which would otherwise
        // compare as "before" the emission and let the defect through.
        Assert.True(gate >= 0, "The external fallback does not consult the session handle.");
        Assert.True(refusal > gate, "The external fallback does not refuse a session-less outcome.");
        Assert.True(emission > refusal, "Connected is emitted before the session gate.");

        // The session-less branch reports disconnected rather than an external window.
        Assert.Contains("UpdateStatus(false);", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The four lifecycle paths, pinned as a source invariant.
    /// </summary>
    /// <remarks>
    /// <c>EmbeddedCitrixView</c> is a <c>UserControl</c> that cannot be built without a desktop, so
    /// no test here can observe its wiring at runtime. What must not come back is precise and
    /// greppable: a kill or a liveness read on the launcher process, or the launcher's main window
    /// standing in for the session window. The scan is bounded to the four method bodies so that
    /// unrelated code in the file can never satisfy or break it by accident.
    /// </remarks>
    [Theory]
    [InlineData("private void OnBringToFrontClick(")]
    [InlineData("private async void OnTerminateClick(")]
    [InlineData("private void OnHealthTimerTick(")]
    [InlineData("public void Dispose()")]
    public void CitrixLifecyclePaths_NeverDriveTheLauncherProcess(string signature)
    {
        string body = ExtractMethodBody(ReadAppSource("Views/EmbeddedCitrixView.xaml.cs"), signature);

        Assert.DoesNotContain("MainWindowHandle", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Kill()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("HasExited", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// External-mode health and the displayed PID must both read the handle. A source assertion
    /// because both are set on WPF elements this project cannot instantiate.
    /// </summary>
    [Fact]
    public void ExternalHealthAndDisplayedPid_ComeFromTheSessionHandle()
    {
        string source = ReadAppSource("Views/EmbeddedCitrixView.xaml.cs");

        Assert.Contains("bool alive = _sessionHandle?.IsAlive == true;", source, StringComparison.Ordinal);
        Assert.Contains(
            "SessionInfoText.Text = _sessionHandle is { OwnerProcessId: > 0 } liveHandle",
            source,
            StringComparison.Ordinal);

        // And the launcher is released, never terminated, on teardown.
        Assert.Contains("_session?.Process?.Dispose();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_session.Process.Kill()", source, StringComparison.Ordinal);
    }

    private static CitrixSessionHandle CreateHandle(FakeWin32 win32, FakeSessionProcess process)
    {
        bool created = CitrixSessionHandle.TryCreate(
            new CitrixSessionWindow(SessionHwnd, OwnerPid),
            "Transparent Windows Client",
            win32.Environment(process),
            out CitrixSessionHandle? handle);

        Assert.True(created);
        Assert.NotNull(handle);
        return handle;
    }

    /// <summary>
    /// Returns the body of the method whose signature starts with <paramref name="signature"/>,
    /// by brace matching from its opening brace.
    /// </summary>
    private static string ExtractMethodBody(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Signature not found: {signature}");

        int openBrace = source.IndexOf('{', signatureIndex);
        Assert.True(openBrace >= 0, $"No body found for: {signature}");

        int depth = 0;
        for (int index = openBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[openBrace..(index + 1)];
                }
            }
        }

        Assert.Fail($"Unbalanced braces while reading the body of: {signature}");
        return string.Empty;
    }

    private static IEnumerable<int> IndexesOf(string haystack, string needle)
    {
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            yield return index;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
    }

    private static string ReadAppSource(string relativePath)
    {
        string full = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Heimdall.App",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from {AppContext.BaseDirectory}.");
    }

    private sealed class FakeWin32
    {
        internal FakeWin32(IntPtr hwnd, int? currentOwnerPid)
        {
            Hwnd = hwnd;
            CurrentOwnerPid = currentOwnerPid;
        }

        internal IntPtr Hwnd { get; }

        internal int? CurrentOwnerPid { get; set; }

        internal bool WindowExists { get; set; } = true;

        internal CitrixSessionEnvironment Environment(ICitrixSessionProcess process) =>
            new(
                hwnd => WindowExists && hwnd == Hwnd,
                hwnd => hwnd == Hwnd ? CurrentOwnerPid : null,
                _ => process);
    }

    private sealed class FakeSessionProcess : ICitrixSessionProcess
    {
        internal FakeSessionProcess(int id)
        {
            Id = id;
        }

        public int Id { get; }

        public bool HasExited { get; set; }

        internal int KillCount { get; private set; }

        internal bool Disposed { get; private set; }

        public void Kill()
        {
            KillCount++;
            HasExited = true;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
