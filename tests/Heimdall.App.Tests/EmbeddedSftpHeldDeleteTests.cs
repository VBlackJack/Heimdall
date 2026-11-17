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
using System.Text.Json;
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
/// The root delete of the browser runs the held delete script, and a refusal of the entry is read
/// per entry: the batch goes on and a single failure names the reason.
/// </summary>
public sealed class EmbeddedSftpHeldDeleteTests
{
    private const string PathLinkKey = "SftpRootRefusedPathLink";
    private const string DeleteRefusedKey = "SftpDeleteRootRefusedEntry";

    [Fact]
    public async Task Delete_BatchWhereTheMiddleEntryIsRefusedForItsPath_RunsEveryCommandAndKeepsGoing()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.DeleteHandler = (_, _) => Task.FromException(DeniedDelete());
        harness.DeleteExecutor = (command, _) => command == PrivilegedFileCommands.BuildHeldDeleteBody("/srv/b")
            ? Task.FromException(RefusalOf("pathlink"))
            : Task.CompletedTask;

        await harness.ViewModel.DeleteEntriesAsync(
        [
            Entry("a", "/srv/a", RemoteEntryKind.Directory),
            Entry("b", "/srv/b", RemoteEntryKind.Directory),
            Entry("c", "/srv/c", RemoteEntryKind.File),
        ]);

        Assert.Equal(
            [
                PrivilegedFileCommands.BuildHeldDeleteBody("/srv/a"),
                PrivilegedFileCommands.BuildHeldDeleteBody("/srv/b"),
                PrivilegedFileCommands.BuildHeldDeleteBody("/srv/c", recursive: false),
            ],
            harness.DeleteCommands);
        Assert.Equal(
            harness.Localizer.Format(PathLinkKey, "b"),
            harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Delete_SingleEntryRefusedForItsPath_NamesThePathLinkMessage()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.DeleteHandler = (_, _) => Task.FromException(DeniedDelete());
        harness.DeleteExecutor = (_, _) => Task.FromException(RefusalOf("pathlink"));

        await harness.ViewModel.DeleteEntriesAsync([Entry("b", "/srv/b", RemoteEntryKind.Directory)]);

        Assert.Equal(harness.Localizer.Format(PathLinkKey, "b"), harness.ViewModel.StatusText);
        Assert.NotEqual(PathLinkKey, harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Delete_SingleEntryRefusedForAnotherReason_UsesTheDeleteMessage()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.DeleteHandler = (_, _) => Task.FromException(DeniedDelete());
        harness.DeleteExecutor = (_, _) => Task.FromException(RefusalOf("special"));

        await harness.ViewModel.DeleteEntriesAsync([Entry("b", "/srv/b", RemoteEntryKind.File)]);

        Assert.Equal(harness.Localizer.Format(DeleteRefusedKey, "b"), harness.ViewModel.StatusText);
        Assert.NotEqual(DeleteRefusedKey, harness.ViewModel.StatusText);
    }

    [Theory]
    [InlineData("link")]
    [InlineData("special")]
    [InlineData("changed")]
    [InlineData("unverifiable")]
    [InlineData("invalid")]
    public async Task Delete_EachEntryReasonOtherThanPathOrHardLink_MapsToTheDeleteMessage(string token)
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.DeleteHandler = (_, _) => Task.FromException(DeniedDelete());
        harness.DeleteExecutor = (_, _) => Task.FromException(RefusalOf(token));

        await harness.ViewModel.DeleteEntriesAsync([Entry("b", "/srv/b", RemoteEntryKind.File)]);

        Assert.Equal(harness.Localizer.Format(DeleteRefusedKey, "b"), harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Delete_ARefusedEntry_DoesNotStopTheBatch()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Browser.DeleteHandler = (_, _) => Task.FromException(DeniedDelete());
        harness.DeleteExecutor = (_, _) => Task.FromException(RefusalOf("pathlink"));

        await harness.ViewModel.DeleteEntriesAsync(
        [
            Entry("a", "/srv/a", RemoteEntryKind.File),
            Entry("b", "/srv/b", RemoteEntryKind.File),
        ]);

        Assert.Equal(2, harness.DeleteCommands.Count);
        Assert.Equal(
            harness.Localizer.FormatCount(2, "SftpDeletePartialSummaryOne", "SftpDeletePartialSummary", 2, 2),
            harness.ViewModel.StatusText);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void DeleteRefusedKey_ExistsInEveryLanguageWithItsPlaceholder(string language)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "locales", language + ".json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        string value = document.RootElement.GetProperty(DeleteRefusedKey).GetString()!;

        Assert.Contains("{0}", value, StringComparison.Ordinal);
    }

    private static string StatusOfRefusal(string token)
        => string.Join('\n', PrivilegedFileCommands.EntryRefusedMarker + " " + token, "Refusing: entry.", string.Empty);

    private static Exception RefusalOf(string token)
    {
        try
        {
            EmbeddedSftpViewModel.EnsureSudoSucceeded(
                PrivilegedFileCommands.NonRegularTargetExitStatus,
                StatusOfRefusal(token),
                "command");
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The refusal was not raised.");
    }

    private static RemoteRecursiveDeleteException DeniedDelete()
        => new(RemoteRecursiveDeleteFailureReason.PermissionDenied);

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

        public int RootPromptCount => Dialog.ConfirmCalls.Count(
            call => string.Equals(call.Title, Localizer["SftpConfirmSudoDeleteTitle"], StringComparison.Ordinal));

        public static async Task<Harness> CreateAsync()
        {
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher());
            ScriptedBrowser browser = new();
            ConfirmingDialogService dialog = new("640");
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
