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
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Sftp;
using Heimdall.Ssh;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// Root changes permissions without following a link, and a batch stops calling sudo once sudo has
/// shown it cannot succeed. The server side of both is a script that cannot run here, so these
/// tests fix what the application sends and how it reads what comes back.
/// </summary>
public sealed class EmbeddedSftpSudoChmodAndBatchTests
{
    private const string ModeText = "640";

    private const string ConfigPath = "/srv/shared/notes";

    // --- Defect 1: root chmod never follows a link --------------------------------------------

    [Theory]
    [InlineData(RemoteEntryKind.SymbolicLink)]
    [InlineData(RemoteEntryKind.Fifo)]
    [InlineData(RemoteEntryKind.Socket)]
    [InlineData(RemoteEntryKind.Device)]
    [InlineData(RemoteEntryKind.Unknown)]
    public async Task Chmod_DeniedEntryThatIsNotAFileOrDirectory_IsRefusedWithoutSudo(RemoteEntryKind kind)
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.ChmodHandler = _ => new SftpPermissionDeniedException("denied");

        await harness.ViewModel.ChmodEntriesAsync([Entry("lnk", "/srv/lnk", kind)]);

        Assert.Empty(harness.ChmodCommands);
        Assert.Equal(harness.Localizer.Format("SftpChmodRootRefusedEntry", "lnk"), harness.ViewModel.StatusText);
        Assert.True(harness.ViewModel.IsErrorStatus);
    }

    [Theory]
    [InlineData(RemoteEntryKind.File)]
    [InlineData(RemoteEntryKind.Directory)]
    public async Task Chmod_DeniedFileOrDirectory_EscalatesWithExactlyTheNoFollowBody(RemoteEntryKind kind)
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.ChmodHandler = _ => new SftpPermissionDeniedException("denied");

        await harness.ViewModel.ChmodEntriesAsync([Entry("notes", ConfigPath, kind)]);

        string command = Assert.Single(harness.ChmodCommands);
        Assert.Equal(PrivilegedFileCommands.BuildNoFollowChmodBody(ConfigPath, ModeText), command);
        Assert.DoesNotContain("chmod 640", command, StringComparison.Ordinal);
        Assert.False(harness.ViewModel.IsErrorStatus);

        // The success is announced, then the refresh that follows it puts the listing status back.
        Assert.Equal(1, harness.Browser.ListDirectoryCallCount);
    }

    [Fact]
    public async Task Chmod_ServerRefusal_ShowsTheRefusalAndNeverTheRemoteText()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.ChmodHandler = _ => new SftpPermissionDeniedException("denied");
        harness.ChmodExecutor = (_, _) =>
        {
            EmbeddedSftpViewModel.EnsureSudoSucceeded(
                PrivilegedFileCommands.NonRegularTargetExitStatus,
                PrivilegedFileCommands.EntryRefusedMarker + " link\nRefusing: symbolic link.\n",
                "command");
            return Task.CompletedTask;
        };

        await harness.ViewModel.ChmodEntriesAsync([Entry("notes", ConfigPath, RemoteEntryKind.File)]);

        Assert.Equal(harness.Localizer.Format("SftpChmodRootRefusedEntry", "notes"), harness.ViewModel.StatusText);
        Assert.DoesNotContain("Refusing", harness.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivilegedFileCommands.EntryRefusedMarker, harness.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.True(harness.ViewModel.IsErrorStatus);
    }

    [Fact]
    public void EnsureSudoSucceeded_StatusAndMarkerTogetherAreAnEntryRefusal()
    {
        string marked = PrivilegedFileCommands.EntryRefusedMarker + " link\nRefusing: symbolic link.\n";

        SudoEntryRefusedException refusal = Assert.Throws<SudoEntryRefusedException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(73, marked, "command"));
        Assert.Equal(EntryRefusalReason.Link, refusal.Reason);
        Assert.Contains("symbolic link", refusal.RemoteDiagnostic, StringComparison.Ordinal);

        // Either one alone is a failure like any other: uploads also exit 73, and nothing else
        // prints the marker.
        Assert.Throws<InvalidOperationException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(73, "x", "command"));
        Assert.Throws<InvalidOperationException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(1, marked, "command"));
        EmbeddedSftpViewModel.EnsureSudoSucceeded(0, marked, "command");
    }

    [Theory]
    [InlineData("link", "SftpChmodRootRefusedEntry")]
    [InlineData("special", "SftpChmodRootRefusedEntry")]
    [InlineData("changed", "SftpChmodRootRefusedEntry")]
    [InlineData("unverifiable", "SftpChmodRootRefusedUnverifiable")]
    [InlineData("invalid", "SftpChmodRootRefusedUnverifiable")]
    [InlineData("", "SftpChmodRootRefusedUnverifiable")]
    [InlineData("unknown-token", "SftpChmodRootRefusedUnverifiable")]
    public async Task Chmod_ServerRefusalReason_ChoosesTheMessage(string token, string key)
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.ChmodHandler = _ => new SftpPermissionDeniedException("denied");
        harness.ChmodExecutor = (_, _) =>
        {
            try
            {
                EmbeddedSftpViewModel.EnsureSudoSucceeded(
                    73,
                    (PrivilegedFileCommands.EntryRefusedMarker + " " + token).TrimEnd() + "\nRefusing: x\n",
                    "command");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        };

        await harness.ViewModel.ChmodEntriesAsync([Entry("notes", ConfigPath, RemoteEntryKind.File)]);

        Assert.Equal(harness.Localizer.Format(key, "notes"), harness.ViewModel.StatusText);
        Assert.NotEqual(key, harness.ViewModel.StatusText);
        Assert.DoesNotContain("Refusing", harness.ViewModel.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sorry, try again")]
    [InlineData("a password is required")]
    [InlineData("alice is not in the sudoers")]
    [InlineData("no tty present")]
    public void EnsureSudoSucceeded_TheMarkerComesBeforeAnyAuthenticationReading(string hostileFileName)
    {
        // A file named like one of sudo's own refusals, echoed by a failing ln of the script.
        string stderr = PrivilegedFileCommands.EntryRefusedMarker
            + " unverifiable\nRefusing: the file could not be pinned.\n"
            + "ln: failed to create hard link 'source' => '../" + hostileFileName + "': Operation not permitted\n";

        SudoEntryRefusedException refusal = Assert.Throws<SudoEntryRefusedException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(73, stderr, "command"));

        Assert.Equal(EntryRefusalReason.Unverifiable, refusal.Reason);

        // Without the marker and the status the same text is still read as sudo's own refusal.
        Assert.Throws<SudoAuthenticationException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(1, hostileFileName, "command"));
    }

    [Fact]
    public async Task Chmod_ClientSideRefusal_UsesTheLinkOrSpecialMessage()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.ChmodHandler = _ => new SftpPermissionDeniedException("denied");

        await harness.ViewModel.ChmodEntriesAsync([Entry("pipe", "/srv/pipe", RemoteEntryKind.Fifo)]);

        Assert.Equal(harness.Localizer.Format("SftpChmodRootRefusedEntry", "pipe"), harness.ViewModel.StatusText);
    }

    [Theory]
    [InlineData("PasswordRejected", true)]
    [InlineData("PasswordUnavailable", true)]
    [InlineData("WritePasswordRejected", true)]
    [InlineData("WritePasswordUnavailable", true)]
    [InlineData("NotPermitted", false)]
    [InlineData("SudoMissing", false)]
    [InlineData("WriteNotPermitted", false)]
    [InlineData("Tooling", false)]
    [InlineData("SshAuthentication", false)]
    [InlineData("HostKey", false)]
    public async Task StoppedBatchInSudoBrowsing_SkipsTheRefreshOnlyWhenItStoppedOnThePassword(
        string failureName,
        bool skipsRefresh)
    {
        // The listing in sudo browsing mode is a sudo call with the same password: it is skipped
        // when that password is what failed, and made for any other stop so the pane shows what
        // the batch did.
        Harness harness = await Harness.CreateAsync();
        SudoBatchGuard guard = new();
        Assert.False(harness.ViewModel.ShouldSkipRefreshAfterStoppedBatch(guard));

        guard.Record(BuildFailure(failureName));
        Assert.True(guard.IsStopped);

        harness.ViewModel.SudoMode = true;
        Assert.Equal(skipsRefresh, harness.ViewModel.ShouldSkipRefreshAfterStoppedBatch(guard));
        Assert.Equal(skipsRefresh, guard.StoppedOnAuthentication);

        harness.ViewModel.SudoMode = false;
        Assert.False(harness.ViewModel.ShouldSkipRefreshAfterStoppedBatch(guard));
    }

    [Fact]
    public async Task Chmod_BatchWithALink_RefusesOnlyTheLink()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.ChmodHandler = _ => new SftpPermissionDeniedException("denied");

        await harness.ViewModel.ChmodEntriesAsync(
        [
            Entry("one", "/srv/one", RemoteEntryKind.File),
            Entry("lnk", "/srv/lnk", RemoteEntryKind.SymbolicLink),
            Entry("two", "/srv/two", RemoteEntryKind.File),
        ]);

        Assert.Equal(
            [
                PrivilegedFileCommands.BuildNoFollowChmodBody("/srv/one", ModeText),
                PrivilegedFileCommands.BuildNoFollowChmodBody("/srv/two", ModeText),
            ],
            harness.ChmodCommands);
        Assert.Equal(PartialChmodSummary(harness.Localizer, 1, 3), harness.ViewModel.StatusText);
        Assert.False(harness.ViewModel.IsErrorStatus);
        Assert.Equal(1, harness.Browser.ListDirectoryCallCount);
    }

    // --- Defect 4: a batch stops calling sudo once sudo cannot succeed -------------------------

    [Theory]
    [InlineData("PasswordRejected")]
    [InlineData("PasswordUnavailable")]
    [InlineData("NotPermitted")]
    [InlineData("SudoMissing")]
    [InlineData("Tooling")]
    [InlineData("SshAuthentication")]
    [InlineData("HostKey")]
    [InlineData("WriteNotPermitted")]
    [InlineData("WritePasswordRejected")]
    [InlineData("WriteSudoMissing")]
    [InlineData("WritePasswordUnavailable")]
    public async Task Delete_BatchFatalFailure_StopsEscalatingAfterTheFirstEntry(string failureName)
    {
        Harness harness = await Harness.CreateAsync();
        Exception failure = BuildFailure(failureName);
        harness.Browser.DeleteHandler = (_, _) => Task.FromException(DeniedDelete());
        harness.DeleteExecutor = (_, _) => Task.FromException(failure);

        await harness.ViewModel.DeleteEntriesAsync(
        [
            Entry("a", "/srv/a", RemoteEntryKind.Directory),
            Entry("b", "/srv/b", RemoteEntryKind.Directory),
            Entry("c", "/srv/c", RemoteEntryKind.Directory),
        ]);

        Assert.Equal(["/srv/a", "/srv/b", "/srv/c"], harness.Browser.DeleteCalls);
        Assert.Equal([PrivilegedFileCommands.BuildHeldDeleteBody("/srv/a")], harness.DeleteCommands);
        Assert.Equal(1, harness.EscalationPromptCount);
        Assert.Equal(
            harness.Localizer.FormatCount(
                2,
                "SftpSudoBatchStoppedOne",
                "SftpSudoBatchStopped",
                harness.ViewModel.DescribeTransferError(failure),
                2),
            harness.ViewModel.StatusText);
        Assert.True(harness.ViewModel.IsErrorStatus);
    }

    [Theory]
    [InlineData("Generic")]
    [InlineData("Io")]
    [InlineData("EntryRefused")]
    [InlineData("WriteStagingFailed")]
    [InlineData("WriteSudoFailed")]
    public async Task Delete_PerEntryFailure_KeepsEscalatingForEveryEntry(string failureName)
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.DeleteHandler = (_, _) => Task.FromException(DeniedDelete());
        harness.DeleteExecutor = (_, _) => Task.FromException(BuildFailure(failureName));

        await harness.ViewModel.DeleteEntriesAsync(
        [
            Entry("a", "/srv/a", RemoteEntryKind.Directory),
            Entry("b", "/srv/b", RemoteEntryKind.Directory),
            Entry("c", "/srv/c", RemoteEntryKind.Directory),
        ]);

        Assert.Equal(
            [
                PrivilegedFileCommands.BuildHeldDeleteBody("/srv/a"),
                PrivilegedFileCommands.BuildHeldDeleteBody("/srv/b"),
                PrivilegedFileCommands.BuildHeldDeleteBody("/srv/c"),
            ],
            harness.DeleteCommands);
        Assert.Equal(1, harness.EscalationPromptCount);
        Assert.Equal(
            harness.Localizer.FormatCount(3, "SftpDeletePartialSummaryOne", "SftpDeletePartialSummary", 3, 3),
            harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Delete_StoppedBatch_StillDeletesWhatTheAccountCanAndSaysWhatWasNotRetried()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.DeleteHandler = (path, _) => path.StartsWith("/srv/denied", StringComparison.Ordinal)
            ? Task.FromException(DeniedDelete())
            : Task.CompletedTask;
        SudoAuthenticationException failure = new(SudoFailureKind.PasswordRejected, "Sorry, try again.");
        harness.DeleteExecutor = (_, _) => Task.FromException(failure);

        await harness.ViewModel.DeleteEntriesAsync(
        [
            Entry("denied1", "/srv/denied1", RemoteEntryKind.Directory),
            Entry("plain.txt", "/srv/plain.txt", RemoteEntryKind.File),
            Entry("denied2", "/srv/denied2", RemoteEntryKind.Directory),
        ]);

        Assert.Equal(["/srv/denied1", "/srv/plain.txt", "/srv/denied2"], harness.Browser.DeleteCalls);
        Assert.Single(harness.DeleteCommands);
        Assert.Equal(
            harness.Localizer.Format(
                "SftpSudoBatchStoppedOne",
                harness.Localizer["ErrorSudoPasswordRejected"],
                1),
            harness.ViewModel.StatusText);
        Assert.False(harness.ViewModel.IsErrorStatus);
        Assert.Equal(1, harness.Browser.ListDirectoryCallCount);
    }

    [Fact]
    public async Task Delete_SingleEntryThatStoppedTheBatch_ShowsTheReasonItself()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.DeleteHandler = (_, _) => Task.FromException(DeniedDelete());
        harness.DeleteExecutor = (_, _) => Task.FromException(
            new SudoAuthenticationException(SudoFailureKind.NotPermitted, "alice is not in the sudoers file."));

        await harness.ViewModel.DeleteEntriesAsync([Entry("a", "/srv/a", RemoteEntryKind.Directory)]);

        Assert.Equal(harness.Localizer["ErrorSudoNotPermitted"], harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Chmod_RejectedPassword_StopsEscalatingButStillTriesTheAccountForEveryEntry()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.ChmodHandler = _ => new SftpPermissionDeniedException("denied");
        SudoAuthenticationException failure = new(SudoFailureKind.PasswordRejected, "Sorry, try again.");
        harness.ChmodExecutor = (_, _) => Task.FromException(failure);

        await harness.ViewModel.ChmodEntriesAsync(
        [
            Entry("a", "/srv/a", RemoteEntryKind.File),
            Entry("b", "/srv/b", RemoteEntryKind.File),
            Entry("c", "/srv/c", RemoteEntryKind.File),
        ]);

        Assert.Equal(3, harness.Browser.ChmodCallCount);
        Assert.Single(harness.ChmodCommands);
        Assert.Equal(
            harness.Localizer.Format(
                "SftpSudoBatchStopped",
                harness.Localizer["ErrorSudoPasswordRejected"],
                2),
            harness.ViewModel.StatusText);
        Assert.True(harness.ViewModel.IsErrorStatus);
    }

    [Fact]
    public async Task Chmod_StoppedBatchWithSomeSuccess_WarnsAndRefreshes()
    {
        foreach (bool sudoMode in new[] { false })
        {
            Harness harness = await Harness.CreateAsync();
            harness.ViewModel.SudoMode = sudoMode;
            harness.Browser.ChmodHandler = path => path == "/srv/a"
                ? null
                : new SftpPermissionDeniedException("denied");
            SudoAuthenticationException failure = new(SudoFailureKind.PasswordRejected, "Sorry, try again.");
            harness.ChmodExecutor = (_, _) => Task.FromException(failure);

            await harness.ViewModel.ChmodEntriesAsync(
            [
                Entry("a", "/srv/a", RemoteEntryKind.File),
                Entry("b", "/srv/b", RemoteEntryKind.File),
                Entry("c", "/srv/c", RemoteEntryKind.File),
            ]);

            Assert.Equal(
                harness.Localizer.Format(
                    "SftpSudoBatchStoppedOne",
                    harness.Localizer["ErrorSudoPasswordRejected"],
                    1),
                harness.ViewModel.StatusText);
            Assert.False(harness.ViewModel.IsErrorStatus);

            // The listing itself would go through sudo with the same password.
            Assert.Equal(sudoMode ? 0 : 1, harness.Browser.ListDirectoryCallCount);
        }
    }

    [Fact]
    public async Task Chmod_ImmutableFile_IsAPerEntryFailureAndDoesNotStopTheBatch()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.ChmodHandler = _ => new SftpPermissionDeniedException("denied");
        int calls = 0;
        harness.ChmodExecutor = (_, _) =>
        {
            calls++;
            if (calls > 1)
            {
                return Task.CompletedTask;
            }

            try
            {
                EmbeddedSftpViewModel.EnsureSudoSucceeded(
                    1,
                    "chmod: changing permissions of 'source': Operation not permitted",
                    "command");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        };

        await harness.ViewModel.ChmodEntriesAsync(
        [
            Entry("locked", "/srv/locked", RemoteEntryKind.File),
            Entry("free", "/srv/free", RemoteEntryKind.File),
        ]);

        Assert.Equal(2, harness.ChmodCommands.Count);
        Assert.Equal(PartialChmodSummary(harness.Localizer, 1, 2), harness.ViewModel.StatusText);
        Assert.False(harness.ViewModel.IsErrorStatus);
    }

    [Fact]
    public void SudoBatchGuard_OnlyAFailureThatCannotChangeStopsTheBatch()
    {
        Dictionary<string, bool> table = new()
        {
            ["PasswordRejected"] = true,
            ["PasswordUnavailable"] = true,
            ["NotPermitted"] = true,
            ["SudoMissing"] = true,
            ["Tooling"] = true,
            ["SshAuthentication"] = true,
            ["HostKey"] = true,
            ["WriteNotPermitted"] = true,
            ["WritePasswordRejected"] = true,
            ["WriteSudoMissing"] = true,
            ["WritePasswordUnavailable"] = true,
            ["Generic"] = false,
            ["Io"] = false,
            ["EntryRefused"] = false,
            ["WriteStagingFailed"] = false,
            ["WriteSudoFailed"] = false,
        };

        foreach ((string name, bool fatal) in table)
        {
            Exception failure = BuildFailure(name);
            Assert.True(fatal == SudoBatchGuard.IsBatchFatal(failure), $"{name} is classified wrongly.");

            SudoBatchGuard guard = new();
            Assert.False(guard.IsStopped);

            guard.Record(failure);
            Assert.Equal(fatal, guard.IsStopped);
            Assert.Same(fatal ? failure : null, guard.StoppingFailure);
        }
    }

    [Fact]
    public void SudoBatchGuard_KeepsTheFirstStoppingFailureAndIgnoresPerEntryOnes()
    {
        SudoBatchGuard guard = new();
        SudoAuthenticationException first = new(SudoFailureKind.PasswordRejected, "a");
        SudoAuthenticationException second = new(SudoFailureKind.NotPermitted, "b");

        guard.Record(new InvalidOperationException("per entry"));
        Assert.False(guard.IsStopped);
        guard.Record(first);
        guard.Record(second);
        guard.Record(new InvalidOperationException("later"));

        Assert.Same(first, guard.StoppingFailure);
    }

    // --- Classification of the new kinds --------------------------------------------------------

    [Theory]
    [InlineData(1, "alice is not in the sudoers file.  This incident will be reported.", nameof(SudoFailureKind.NotPermitted))]
    [InlineData(1, "Sorry, user alice may not run sudo on host.", nameof(SudoFailureKind.NotPermitted))]
    [InlineData(1, "alice is not allowed to execute '/bin/sh' as root on host.", nameof(SudoFailureKind.NotPermitted))]
    [InlineData(127, "sh: 1: sudo: not found", nameof(SudoFailureKind.SudoMissing))]
    public void EnsureSudoSucceeded_NamesTheKindsSettledByTheAccountOrTheServer(
        int exitStatus,
        string stderr,
        string expectedKind)
    {
        SudoAuthenticationException error = Assert.Throws<SudoAuthenticationException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(exitStatus, stderr, "command"));

        Assert.Equal(Enum.Parse<SudoFailureKind>(expectedKind), error.Kind);
    }

    [Fact]
    public void EnsureSudoSucceeded_AMissingCommandThatIsNotSudoStaysGeneric()
    {
        Assert.Throws<InvalidOperationException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(127, "sh: 1: foo: not found", "command"));
        Assert.Throws<InvalidOperationException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(1, "boom", "command"));
    }

    [Theory]
    [InlineData(nameof(SudoFailureKind.NotPermitted), "ErrorSudoNotPermitted")]
    [InlineData(nameof(SudoFailureKind.SudoMissing), "ErrorSudoMissing")]
    public async Task DescribeTransferError_TheNewKindsUseTheExistingMessages(string kindName, string key)
    {
        Harness harness = await Harness.CreateAsync();

        string message = harness.ViewModel.DescribeTransferError(new SudoAuthenticationException(Enum.Parse<SudoFailureKind>(kindName), "raw"));

        Assert.Equal(harness.Localizer[key], message);
        Assert.NotEqual(key, message);
    }

    // --- Fixtures -------------------------------------------------------------------------------

    private static string PartialChmodSummary(LocalizationManager localizer, int failed, int total)
        => localizer.FormatCount(failed, "SftpChmodPartialSummaryOne", "SftpChmodPartialSummary", failed, total);

    private static RemoteRecursiveDeleteException DeniedDelete()
        => new(RemoteRecursiveDeleteFailureReason.PermissionDenied);

    private static Exception BuildFailure(string name) => name switch
    {
        "PasswordRejected" => new SudoAuthenticationException(SudoFailureKind.PasswordRejected, "Sorry, try again."),
        "PasswordUnavailable" => new SudoAuthenticationException(SudoFailureKind.PasswordUnavailable, "a terminal is required"),
        "NotPermitted" => new SudoAuthenticationException(SudoFailureKind.NotPermitted, "alice is not in the sudoers file."),
        "SudoMissing" => new SudoAuthenticationException(SudoFailureKind.SudoMissing, "sudo: not found"),
        "Tooling" => new SudoToolingUnavailableException("Refusing: cp is not available on the server."),
        "SshAuthentication" => new SshAuthenticationException("Permission denied."),
        "HostKey" => new HostKeyRejectedException("host.test", 22, "ssh-ed25519", "SHA256:presented", "SHA256:stored"),
        "WriteNotPermitted" => new PrivilegedWriteFailedException(PrivilegedWriteFailureKind.NotPermitted, "not in the sudoers"),
        "WritePasswordRejected" => new PrivilegedWriteFailedException(PrivilegedWriteFailureKind.PasswordRejected, "Sorry, try again."),
        "WriteSudoMissing" => new PrivilegedWriteFailedException(PrivilegedWriteFailureKind.SudoMissing, "sudo: not found"),
        "WritePasswordUnavailable" => new PrivilegedWriteFailedException(PrivilegedWriteFailureKind.PasswordUnavailable, "a terminal is required"),
        "WriteStagingFailed" => new PrivilegedWriteFailedException(PrivilegedWriteFailureKind.StagingFailed, "no space"),
        "WriteSudoFailed" => new PrivilegedWriteFailedException(PrivilegedWriteFailureKind.SudoFailed, "failed"),
        "EntryRefused" => new SudoEntryRefusedException(EntryRefusalReason.Link, "Refusing: symbolic link."),
        "Io" => new IOException("sudo failed"),
        "Generic" => new InvalidOperationException("sudo rm failed (exit 1): x"),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown failure name."),
    };

    private static SftpFileInfo Entry(string name, string fullPath, RemoteEntryKind kind)
        => new(
            name,
            fullPath,
            kind,
            0,
            DateTime.UnixEpoch,
            kind == RemoteEntryKind.Directory ? "rwxr-xr-x" : "rw-r--r--",
            "1000",
            "1000");

    /// <summary>A view model wired to a scripted browser, a scripted sudo channel and a dialog.</summary>
    private sealed class Harness
    {
        private readonly List<string> _chmodCommands = [];
        private readonly List<string> _deleteCommands = [];

        private Harness(EmbeddedSftpViewModel viewModel, LocalizationManager localizer, ScriptedBrowser browser, ConfirmingDialogService dialog)
        {
            ViewModel = viewModel;
            Localizer = localizer;
            Browser = browser;
            Dialog = dialog;
        }

        public EmbeddedSftpViewModel ViewModel { get; }

        public LocalizationManager Localizer { get; }

        public ScriptedBrowser Browser { get; }

        public ConfirmingDialogService Dialog { get; }

        public IReadOnlyList<string> ChmodCommands => _chmodCommands;

        public IReadOnlyList<string> DeleteCommands => _deleteCommands;

        /// <summary>What the sudo channel does for a chmod, after the command is recorded.</summary>
        public Func<string, CancellationToken, Task>? ChmodExecutor { get; set; }

        /// <summary>What the sudo channel does for a delete, after the command is recorded.</summary>
        public Func<string, CancellationToken, Task>? DeleteExecutor { get; set; }

        public int EscalationPromptCount => Dialog.ConfirmCalls.Count(
            call => string.Equals(call.Title, Localizer["SftpConfirmSudoDeleteTitle"], StringComparison.Ordinal));

        public static async Task<Harness> CreateAsync()
        {
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher());
            ScriptedBrowser browser = new();
            ConfirmingDialogService dialog = new(ModeText);
            Harness harness = new(viewModel, localizer, browser, dialog);

            SetField(viewModel, "_browser", browser);
            SetField(viewModel, "_localizer", localizer);
            SetField(
                viewModel,
                "_sshParams",
                new SshConnectionParams { Host = "example.test", Username = "tester" });
            viewModel.SetDialogService(dialog);
            viewModel.SetSudoChmodCommandExecutor((command, ct) =>
            {
                harness._chmodCommands.Add(command);
                return harness.ChmodExecutor?.Invoke(command, ct) ?? Task.CompletedTask;
            });
            SetField(
                viewModel,
                "_sudoDeleteCommandExecutor",
                new Func<string, CancellationToken, Task>((command, ct) =>
                {
                    harness._deleteCommands.Add(command);
                    return harness.DeleteExecutor?.Invoke(command, ct) ?? Task.CompletedTask;
                }));
            return harness;
        }

        private static void SetField(EmbeddedSftpViewModel viewModel, string name, object value)
        {
            FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(viewModel, value);
        }
    }

    /// <summary>A browser whose chmod and delete are scripted per path, and that counts what it is asked.</summary>
    private sealed class ScriptedBrowser : IRemoteBrowser
    {
        private int _chmodCallCount;
        private int _listDirectoryCallCount;

        public event Action<string>? DirectoryChanged
        {
            add { }
            remove { }
        }

        public event Action<SftpTransferProgress>? TransferProgress
        {
            add { }
            remove { }
        }

        public event Action<RemoteOperationWarning>? OperationWarningRaised
        {
            add { }
            remove { }
        }

        public event Action<string?>? Disconnected
        {
            add { }
            remove { }
        }

        public string CurrentDirectory => "/";

        public bool IsConnected => true;

        /// <summary>The failure a chmod of a path ends in, or null when the account may do it.</summary>
        public Func<string, Exception?>? ChmodHandler { get; set; }

        public Func<string, CancellationToken, Task>? DeleteHandler { get; set; }

        public List<string> DeleteCalls { get; } = [];

        public int ChmodCallCount => Volatile.Read(ref _chmodCallCount);

        public int ListDirectoryCallCount => Volatile.Read(ref _listDirectoryCallCount);

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _chmodCallCount);
            Exception? failure = ChmodHandler?.Invoke(path);
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            DeleteCalls.Add(path);
            return DeleteHandler is null ? Task.CompletedTask : DeleteHandler(path, ct);
        }

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _listDirectoryCallCount);
            return Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);
        }

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
            => Task.FromResult(CurrentDirectory);

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DownloadFileAsync(string remotePath, string localPath, bool overwrite, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UploadFileAsync(string localPath, string remotePath, bool overwrite, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default)
            => Task.CompletedTask;

        public void Disconnect()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Answers the permission prompt with a fixed mode and every confirmation with yes.</summary>
    private sealed class ConfirmingDialogService(string input) : IDialogService
    {
        public List<(string Title, string Message, string Severity)> ConfirmCalls { get; } = [];

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            ConfirmCalls.Add((title, message, severity));
            return Task.FromResult(true);
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => throw new NotSupportedException();

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
            => Task.FromResult<string?>(input);

        public Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
            => throw new NotSupportedException();

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
            => throw new NotSupportedException();

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
            => throw new NotSupportedException();

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => throw new NotSupportedException();

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void ShowError(string title, string message)
        {
        }

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowWarning(string title, string message)
        {
        }
    }
}
