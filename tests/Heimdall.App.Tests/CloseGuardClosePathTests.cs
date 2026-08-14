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
using System.Reflection;
using System.Text.RegularExpressions;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The close protocol as the five close paths must honour it, plus the static guarantees that no
/// unit test can express: that no close primitive blocks on the asynchronous decision, and that a
/// veto is never undone by a later force-removal.
/// </summary>
public sealed class CloseGuardClosePathTests
{
    /// <summary>
    /// A close primitive must never block on the decision it delegates. This is the one property
    /// no test can falsify at runtime: the fake dispatcher inlines every invoke and every dialog
    /// double returns an already-completed task, so a real deadlock cannot be reproduced here.
    /// It is therefore pinned as a source invariant instead.
    /// </summary>
    [Theory]
    [InlineData("Services/ISplitService.cs")]
    [InlineData("Services/SplitService.cs")]
    [InlineData("Services/CloseGuard/PaneCloseArbiter.cs")]
    [InlineData("Services/CloseGuard/CloseGuardContracts.cs")]
    public void CloseSurface_NeverBlocksOnATask(string relativePath)
    {
        string source = ReadAppSource(relativePath);

        Assert.DoesNotContain(".GetAwaiter().GetResult()", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherFrame", source, StringComparison.Ordinal);

        // ".Result" needs care: it is a legitimate substring of identifiers such as "closeResult".
        // Only a blocking property read on a task-shaped expression is forbidden.
        foreach (Match match in Regex.Matches(source, @"\w+(?<!\bclose)Result\b\s*;", RegexOptions.None))
        {
            Assert.DoesNotContain("await", match.Value, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The synchronous close primitives keep a synchronous signature. A future edit that made one
    /// of them return a Task would push the asynchronous decision back across the boundary this
    /// whole design exists to keep it out of.
    /// </summary>
    [Fact]
    public void ISplitService_ClosePrimitives_StaySynchronous()
    {
        MethodInfo closePane = typeof(ISplitService).GetMethod(nameof(ISplitService.ClosePane))!;
        MethodInfo closeAll = typeof(ISplitService).GetMethod(nameof(ISplitService.CloseAllPanes))!;

        Assert.Equal(typeof(PaneCloseResult), closePane.ReturnType);
        Assert.Equal(typeof(PaneCloseResult), closeAll.ReturnType);

        // And both take the request explicitly, so no call site can silently keep the old shape.
        Assert.Contains(closePane.GetParameters(), p => p.ParameterType == typeof(CloseRequest));
        Assert.Contains(closeAll.GetParameters(), p => p.ParameterType == typeof(CloseRequest));
    }

    /// <summary>
    /// Both force-removal blocks must be gated on the close having actually happened. A tab that
    /// survives because a guard withheld it is not the failure they exist to paper over: forcing
    /// it out would make it vanish from the UI while its panes were never torn down, leaving the
    /// host undisposed, the tunnel reference unreleased and the transfer still running.
    /// </summary>
    [Fact]
    public void SessionCoordinator_ForceRemoval_IsGatedOnTheCloseOutcome()
    {
        string source = ReadAppSource("ViewModels/Session/SessionCoordinator.cs");

        string[] forcedRemovalGuards =
        [
            "if (result.IsClosed && _main.Connection.ActiveSessions.Contains(tab))",
            "if (closeResult.IsClosed && stillPresentAfterClose)"
        ];

        foreach (string guard in forcedRemovalGuards)
        {
            Assert.Contains(guard, source, StringComparison.Ordinal);
        }

        // Both call sites must capture the outcome: a discarded result cannot gate anything, and
        // an un-gated force-removal is precisely how a veto gets silently undone.
        Assert.Contains(
            "PaneCloseResult result = await _main.Connection.CloseSessionAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PaneCloseResult closeResult = await _main.Connection.CloseSessionAsync(",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The floating window must not re-issue its close on the stack that decided it.
    /// </summary>
    /// <remarks>
    /// The confirmation returns an already-completed task, so the await inside the resume path can
    /// resume synchronously inside <c>OnClosing</c>, and closing a window from its own
    /// <c>OnClosing</c> throws from <c>Window.VerifyNotClosing</c> - on every floating window, not
    /// only guarded ones. The re-issue therefore has to go through the dispatcher.
    /// </remarks>
    [Fact]
    public void FloatingSessionWindow_ReIssuesItsCloseThroughTheDispatcher()
    {
        string source = ReadAppSource("Views/FloatingSessionWindow.xaml.cs");

        Assert.Contains("Dispatcher.BeginInvoke(Close)", source, StringComparison.Ordinal);

        // A bare Close() anywhere in the resume path would be the defect. The only bare calls left
        // are in the reattach path, which never awaits.
        int resumeStart = source.IndexOf("private async Task ResumeCloseAsync()", StringComparison.Ordinal);
        Assert.True(resumeStart > 0, "ResumeCloseAsync not found.");
        int resumeEnd = source.IndexOf("private void ReportBlocked", StringComparison.Ordinal);
        Assert.True(resumeEnd > resumeStart, "ReportBlocked not found after ResumeCloseAsync.");

        string resume = source[resumeStart..resumeEnd];
        Assert.DoesNotContain("\n            Close();", resume, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true;", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every close path must go through the arbiter rather than around it.
    /// </summary>
    [Theory]
    [InlineData("Services/SplitService.cs", "_closeArbiter.Poll(request")]
    [InlineData("ViewModels/ConnectionViewModel.cs", "_closeArbiter.ResolveAsync(request")]
    [InlineData("ViewModels/MainViewModel.cs", "_closeArbiter.ResolveAsync(request")]
    [InlineData("Views/FloatingSessionWindow.xaml.cs", "_closeArbiter.ResolveAsync(request")]
    public void ClosePaths_ConsultTheArbiter(string relativePath, string expected)
        => Assert.Contains(expected, ReadAppSource(relativePath), StringComparison.Ordinal);

    /// <summary>
    /// <c>CloseAllSessionsSilently</c> bypasses every guard, so nothing but application exit may
    /// call it. The Close All command is a user gesture and must drive the interactive path.
    /// </summary>
    /// <remarks>
    /// A counting oracle cannot cover this. Swapping two intents leaves any total unchanged, which
    /// is exactly how a user gesture ended up on the silent path in the first place - so the
    /// intent each producer actually emits is asserted from the observed request instead, in
    /// <see cref="CloseIntentProducerTests"/>.
    /// </remarks>
    [Fact]
    public void CloseAllSessionsSilently_IsCalledOnlyFromApplicationExit()
    {
        string[] sources =
        [
            ReadAppSource("ViewModels/ConnectionViewModel.cs"),
            ReadAppSource("ViewModels/MainViewModel.cs"),
            ReadAppSource("ViewModels/Session/SessionCoordinator.cs"),
            ReadAppSource("Views/FloatingSessionWindow.xaml.cs"),
            ReadAppSource("Views/SessionPaneControl.xaml.cs")
        ];

        foreach (string source in sources)
        {
            // The declaration is not a call. Only invocations are forbidden here.
            string withoutDeclaration = source.Replace(
                "public void CloseAllSessionsSilently()",
                "public void <declaration>()",
                StringComparison.Ordinal);

            Assert.DoesNotContain("CloseAllSessionsSilently()", withoutDeclaration, StringComparison.Ordinal);
        }

        Assert.Contains(
            "public void CloseAllSessionsSilently()",
            ReadAppSource("ViewModels/ConnectionViewModel.cs"),
            StringComparison.Ordinal);
        Assert.Contains("CloseAllSessionsSilently()", ReadAppSource("App.xaml.cs"), StringComparison.Ordinal);
    }

    private static string ReadAppSource(string relativePath)
    {
        string full = Path.Combine(FindRepositoryRoot(), "src", "Heimdall.App", relativePath.Replace('/', Path.DirectorySeparatorChar));
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
}
