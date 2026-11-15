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
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Sftp;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// The question asking to delete as root names its two answers after the acts, declines by
/// default, and cannot be forged by the name of the file that was refused.
/// </summary>
public sealed class EmbeddedSftpSudoDeleteLabeledConfirmTests
{
    private static readonly char RightToLeftOverride = (char)0x202E;

    private static readonly string ForgedName = "tmp\nDelete everything?" + RightToLeftOverride + "gpj.txt";

    [Fact]
    public async Task EscalationQuestion_UsesTheLabeledOverloadOnceWithTheTwoAnswers()
    {
        FiveArgConfirmDialogService dialog = new() { Answer = false };
        LocalizationManager localizer = await CreateLocalizerAsync();

        await DeleteRefusedEntryAsync(dialog, localizer, "denied");

        // The deletion itself is asked with the plain overload; the escalation never is.
        Assert.DoesNotContain(localizer["SftpConfirmSudoDeleteTitle"], dialog.PlainTitles);
        LabeledConfirmCall call = Assert.Single(dialog.LabeledCalls);
        Assert.Equal("danger", call.Severity);
        Assert.Equal(localizer["SftpConfirmSudoDeleteTitle"], call.Title);
        Assert.Equal(localizer["SftpConfirmSudoDeleteAccept"], call.ConfirmLabel);
        Assert.Equal(localizer["SftpConfirmSudoDeleteDecline"], call.DeclineLabel);
        Assert.NotEqual("SftpConfirmSudoDeleteAccept", call.ConfirmLabel);
        Assert.NotEqual("SftpConfirmSudoDeleteDecline", call.DeclineLabel);
    }

    [Fact]
    public async Task EscalationQuestion_ShowsARefusedNameWithoutALineBreakOrABidiOverride()
    {
        FiveArgConfirmDialogService dialog = new() { Answer = false };
        LocalizationManager localizer = await CreateLocalizerAsync();

        EmbeddedSftpViewModel viewModel = await DeleteRefusedEntryAsync(dialog, localizer, ForgedName);

        string shown = Assert.Single(dialog.LabeledCalls).Message;
        Assert.DoesNotContain('\n', shown);
        Assert.DoesNotContain(RightToLeftOverride, shown);
        Assert.Equal(
            localizer.Format("SftpConfirmSudoDelete", ExecutionTrustDescriber.Visible(ForgedName)),
            shown);
        Assert.DoesNotContain('\n', viewModel.StatusText);
        Assert.DoesNotContain(RightToLeftOverride, viewModel.StatusText);
    }

    [Fact]
    public async Task FirstDeleteQuestion_ShowsAForgedNameWithoutLineBreakBidiOrRawQuote()
    {
        FiveArgConfirmDialogService dialog = new() { Answer = false, PlainAnswer = false };
        LocalizationManager localizer = await CreateLocalizerAsync();
        string name = "a\nb" + RightToLeftOverride + "c\"d";

        await DeleteRefusedEntryAsync(dialog, localizer, name);

        string shown = Assert.Single(dialog.PlainMessages);
        Assert.DoesNotContain('\n', shown);
        Assert.DoesNotContain(RightToLeftOverride, shown);
        Assert.Equal(localizer.Format("SftpConfirmDelete", ExecutionTrustDescriber.Visible(name)), shown);
        Assert.DoesNotContain("c\"d", shown, StringComparison.Ordinal);
    }

    private static async Task<EmbeddedSftpViewModel> DeleteRefusedEntryAsync(
        FiveArgConfirmDialogService dialog,
        LocalizationManager localizer,
        string name)
    {
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher());
        SetField(viewModel, "_browser", new DeniedDeleteBrowser());
        SetField(viewModel, "_localizer", localizer);
        SetField(
            viewModel,
            "_sshParams",
            new SshConnectionParams { Host = "example.test", Username = "tester" });
        SetField(
            viewModel,
            "_sudoDeleteCommandExecutor",
            new Func<string, CancellationToken, Task>((_, _) => Task.CompletedTask));
        viewModel.SetDialogService(dialog);

        SftpFileInfo entry = new(
            name,
            "/srv/" + name,
            RemoteEntryKind.Directory,
            0,
            DateTime.UnixEpoch,
            "rwxr-xr-x",
            "1000",
            "1000");
        await viewModel.DeleteEntriesAsync([entry]);
        return viewModel;
    }

    private static void SetField(EmbeddedSftpViewModel viewModel, string name, object value)
    {
        FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(viewModel, value);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync()
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return manager;
    }

    /// <summary>A browser whose every delete is refused for lack of permission.</summary>
    private sealed class DeniedDeleteBrowser : IRemoteBrowser
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

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(
            string? path = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);

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

        public Task DeleteAsync(string path, CancellationToken ct = default)
            => Task.FromException(new RemoteRecursiveDeleteException(
                RemoteRecursiveDeleteFailureReason.PermissionDenied));

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
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
}
