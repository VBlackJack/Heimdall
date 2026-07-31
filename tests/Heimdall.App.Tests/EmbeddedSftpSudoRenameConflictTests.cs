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

using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Import;
using Heimdall.Core.Ssh;
using Heimdall.Sftp;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class EmbeddedSftpSudoRenameConflictTests
{
    [Fact]
    public async Task RenameEntryAsync_SudoTargetAbsent_ProbesPrivilegedChannelWithoutDialog()
    {
        RecordingConflictPresenter presenter = new(_ =>
            throw new InvalidOperationException("The conflict dialog must not be shown."));
        RecordingSudoExecutor executor = new(
        [
            Result(exitStatus: 1),
            Result(exitStatus: 0),
            Result(exitStatus: 1),
        ]);
        PermissionDeniedRenameBrowser browser = new();
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter, executor);

        await viewModel.RenameEntryAsync(CreateFile());

        Assert.Equal(0, presenter.CallCount);
        Assert.Equal(
        [
            "test -e '/remote/new.txt' -o -L '/remote/new.txt'",
            "mv -nT '/remote/old.txt' '/remote/new.txt'",
            "test -e '/remote/old.txt' -o -L '/remote/old.txt'",
        ], executor.Commands);
        Assert.Equal("Ready", viewModel.StatusText);
        Assert.False(viewModel.IsErrorStatus);
        Assert.Equal(1, browser.ListDirectoryCallCount);
    }

    [Fact]
    public async Task RenameEntryAsync_SudoConflictCancelled_DoesNotMove()
    {
        RecordingConflictPresenter presenter = new(_ => null);
        RecordingSudoExecutor executor = new(
        [
            Result(exitStatus: 0),
            Result(exitStatus: 1),
        ]);
        PermissionDeniedRenameBrowser browser = new();
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter, executor);

        await viewModel.RenameEntryAsync(CreateFile());

        Assert.Equal(1, presenter.CallCount);
        Assert.Equal(
        [
            "test -e '/remote/new.txt' -o -L '/remote/new.txt'",
            "test -d '/remote/new.txt'",
        ], executor.Commands);
        Assert.DoesNotContain(executor.Commands, command => command.StartsWith("mv ", StringComparison.Ordinal));
        Assert.Equal("SftpStatusTransferCancelled", viewModel.StatusText);
        Assert.Equal(0, browser.ListDirectoryCallCount);
    }

    [Fact]
    public async Task RenameEntryAsync_SudoAutoRename_ProbesCandidatesThroughPrivilegedChannel()
    {
        RecordingConflictPresenter presenter = new(viewModel =>
            new FileConflictDialogResult(
            [
                new FileConflictDecision(
                    viewModel.Rows.Single().ItemIndex,
                    FileConflictResolutionChoice.AutoRename),
            ]));
        RecordingSudoExecutor executor = new(
        [
            Result(exitStatus: 0),
            Result(exitStatus: 1),
            Result(exitStatus: 0),
            Result(exitStatus: 1),
            Result(exitStatus: 1),
            Result(exitStatus: 0),
            Result(exitStatus: 1),
        ]);
        PermissionDeniedRenameBrowser browser = new();
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter, executor);

        await viewModel.RenameEntryAsync(CreateFile());

        Assert.Equal(1, presenter.CallCount);
        Assert.Equal(
        [
            "test -e '/remote/new.txt' -o -L '/remote/new.txt'",
            "test -d '/remote/new.txt'",
            "test -e '/remote/new (copy).txt' -o -L '/remote/new (copy).txt'",
            "test -d '/remote/new (copy).txt'",
            "test -e '/remote/new (copy 2).txt' -o -L '/remote/new (copy 2).txt'",
            "mv -nT '/remote/old.txt' '/remote/new (copy 2).txt'",
            "test -e '/remote/old.txt' -o -L '/remote/old.txt'",
        ], executor.Commands);
        Assert.False(viewModel.IsErrorStatus);
        Assert.Equal(1, browser.ListDirectoryCallCount);
    }

    [Fact]
    public async Task RenameEntryAsync_SudoNoClobberLeavesSource_ReportsCollisionFailure()
    {
        RecordingConflictPresenter presenter = new(_ =>
            throw new InvalidOperationException("The conflict dialog must not be shown."));
        RecordingSudoExecutor executor = new(
        [
            Result(exitStatus: 1),
            Result(exitStatus: 0),
            Result(exitStatus: 0),
        ]);
        PermissionDeniedRenameBrowser browser = new();
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter, executor);

        await viewModel.RenameEntryAsync(CreateFile());

        Assert.Equal(
            "SftpErrorSudoRenameCollision",
            viewModel.StatusText);
        Assert.True(viewModel.IsErrorStatus);
        Assert.NotEqual("SftpSuccessRename", viewModel.StatusText);
        Assert.Equal(0, browser.ListDirectoryCallCount);
    }

    private static EmbeddedSftpViewModel CreateViewModel(
        IRemoteBrowser browser,
        IFileConflictDialogPresenter presenter,
        RecordingSudoExecutor executor)
    {
        EmbeddedSftpViewModel viewModel = new(
            new FakeUiDispatcher(),
            new RemoteClipboardService(),
            presenter)
        {
            CurrentPath = "/remote",
        };
        SetPrivateField(viewModel, "_browser", browser);
        SetPrivateField(
            viewModel,
            "_sshParams",
            new SshConnectionParams
            {
                Host = "test.example",
                Username = "tester",
            });
        viewModel.SetDialogService(new RenameInputDialogService("new.txt"));
        viewModel.SetSudoRenameCommandExecutor(executor.ExecuteAsync);
        return viewModel;
    }

    private static void SetPrivateField<T>(EmbeddedSftpViewModel viewModel, string name, T value)
    {
        FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(viewModel, value);
    }

    private static SftpFileInfo CreateFile()
        => new(
            "old.txt",
            "/remote/old.txt",
            false,
            1,
            DateTime.UnixEpoch,
            "rw-r--r--",
            "1000",
            "1000");

    private static SudoRenameCommandResult Result(int exitStatus)
        => new(exitStatus, string.Empty, string.Empty);

    private sealed class RecordingConflictPresenter(
        Func<FileConflictDialogViewModel, FileConflictDialogResult?> show)
        : IFileConflictDialogPresenter
    {
        public int CallCount { get; private set; }

        public Task<FileConflictDialogResult?> ShowAsync(FileConflictDialogViewModel viewModel)
        {
            CallCount++;
            return Task.FromResult(show(viewModel));
        }
    }

    private sealed class RecordingSudoExecutor(IEnumerable<SudoRenameCommandResult> results)
    {
        private readonly Queue<SudoRenameCommandResult> _results = new(results);

        public List<string> Commands { get; } = [];

        public Task<SudoRenameCommandResult> ExecuteAsync(string command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (_results.Count == 0)
            {
                throw new InvalidOperationException($"No result was configured for '{command}'.");
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class PermissionDeniedRenameBrowser : IRemoteBrowser
    {
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

        public event Action<string?>? Disconnected
        {
            add { }
            remove { }
        }

        public string CurrentDirectory => "/remote";

        public bool IsConnected => true;

        public int ListDirectoryCallCount { get; private set; }

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(
            string? path = null,
            CancellationToken ct = default)
        {
            ListDirectoryCallCount++;
            return Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);
        }

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
            => Task.FromResult(CurrentDirectory);

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
            => Task.FromException(new UnauthorizedAccessException("rename denied"));

        public Task CopyAsync(
            string sourcePath,
            string destinationPath,
            bool recursive,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Disconnect()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RenameInputDialogService(string input) : IDialogService
    {
        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
            => throw new NotSupportedException();

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

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(
            SnapshotRestoreDialogViewModel viewModel)
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

        public Task<int?> ShowBulkEditPortAsync(
            int count,
            int? initialPort,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ShowBulkEditUsernameAsync(
            int count,
            string? initialUsername,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ShowBulkEditPasswordAsync(
            int count,
            CancellationToken cancellationToken)
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
