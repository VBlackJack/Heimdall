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
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Sftp;
using Heimdall.Ssh;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// The root create-folder and rename of the browser run the held scripts: the commands they send,
/// how the script's own statuses and refusals are told to the user, and the texts they use.
/// </summary>
public sealed class EmbeddedSftpHeldMkdirRenameTests
{
    private const string FolderPath = "/remote/newdir";
    private const string ExistsKey = "SftpErrorMkdirExists";
    private const string ChangedKey = "SftpRootRefusedChanged";
    private const string EntryKey = "SftpRootRefusedEntry";
    private const string PathLinkKey = "SftpRootRefusedPathLink";
    private const string HardLinkKey = "SftpRootRefusedHardLink";

    // --- Create folder ------------------------------------------------------------------------

    [Fact]
    public async Task CreateFolder_AsRoot_SendsTheHeldMkdirBodyForThePathAndReportsSuccess()
    {
        Harness harness = await Harness.CreateAsync();

        await harness.ViewModel.CreateFolderAsync();

        Assert.Equal([PrivilegedFileCommands.BuildHeldMkdirBody(FolderPath)], harness.MkdirCommands);
        Assert.False(harness.ViewModel.IsErrorStatus);
        Assert.Equal(1, harness.ViewModel.RootActivity.Snapshot().OperationCount);
    }

    [Fact]
    public async Task CreateFolder_WhenTheScriptFindsTheNameTaken_SaysTheFolderAlreadyExists()
    {
        Harness harness = await Harness.CreateAsync();
        harness.MkdirExecutor = (_, _) => Task.FromException(
            CaughtBy(PrivilegedFileCommands.DestinationExistsExitStatus, "Refusing: an entry with that name already exists."));

        await harness.ViewModel.CreateFolderAsync();

        Assert.True(harness.ViewModel.IsErrorStatus);
        Assert.Equal(harness.Localizer.Format(ExistsKey, FolderPath), harness.ViewModel.StatusText);
        Assert.NotEqual(ExistsKey, harness.ViewModel.StatusText);
    }

    [Theory]
    [InlineData("pathlink", PathLinkKey)]
    [InlineData("hardlink", HardLinkKey)]
    [InlineData("changed", ChangedKey)]
    [InlineData("invalid", EntryKey)]
    [InlineData("special", EntryKey)]
    [InlineData("unverifiable", EntryKey)]
    public async Task CreateFolder_WhenTheScriptRefuses_NamesTheReasonAndThePath(string token, string key)
    {
        Harness harness = await Harness.CreateAsync();
        harness.MkdirExecutor = (_, _) => Task.FromException(CaughtBy(
            PrivilegedFileCommands.NonRegularTargetExitStatus,
            PrivilegedFileCommands.EntryRefusedMarker + " " + token + "\nRefusing: reason."));

        await harness.ViewModel.CreateFolderAsync();

        Assert.True(harness.ViewModel.IsErrorStatus);
        Assert.Equal(harness.Localizer.Format(key, FolderPath), harness.ViewModel.StatusText);
        Assert.NotEqual(key, harness.ViewModel.StatusText);
    }

    // --- The statuses -------------------------------------------------------------------------

    [Fact]
    public void EnsureSudoSucceeded_Status78_IsTheDestinationExistsFailure()
    {
        Assert.Throws<SudoDestinationExistsException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(78, "Refusing: an entry with that name already exists.", "command"));
    }

    [Fact]
    public void Probes_EscapeABangOutsideTheQuotes()
    {
        const string expectedPath = "'/srv/a'\\!'b'";

        Assert.Equal($"test -e {expectedPath} -o -L {expectedPath}", SudoRenameCommands.BuildExistenceProbe("/srv/a!b"));
        Assert.Equal($"test -d {expectedPath}", SudoRenameCommands.BuildDirectoryProbe("/srv/a!b"));
    }

    [Fact]
    public void EnsureSudoSucceeded_AnEntryRefusal_CarriesThePathItWasGiven()
    {
        string stderr = PrivilegedFileCommands.EntryRefusedMarker + " changed\nRefusing: gone.";

        SudoEntryRefusedException refusal = Assert.Throws<SudoEntryRefusedException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(73, stderr, "rename", "/srv/old.txt"));

        Assert.Equal(EntryRefusalReason.Changed, refusal.Reason);
        Assert.Equal("/srv/old.txt", refusal.RemotePath);
    }

    [Theory]
    [InlineData(EntryRefusalReason.PathLink, PathLinkKey)]
    [InlineData(EntryRefusalReason.HardLink, HardLinkKey)]
    [InlineData(EntryRefusalReason.Changed, ChangedKey)]
    [InlineData(EntryRefusalReason.Invalid, EntryKey)]
    [InlineData(EntryRefusalReason.Special, EntryKey)]
    [InlineData(EntryRefusalReason.Unverifiable, EntryKey)]
    [InlineData(EntryRefusalReason.Link, EntryKey)]
    public async Task DescribeTransferError_OfAnEntryRefusal_UsesTheKeyOfItsReasonWithThePath(
        EntryRefusalReason reason,
        string key)
    {
        Harness harness = await Harness.CreateAsync();

        string message = harness.ViewModel.DescribeTransferError(
            new SudoEntryRefusedException(reason, "diagnostic", "/srv/old.txt"));

        Assert.Equal(harness.Localizer.Format(key, "/srv/old.txt"), message);
    }

    // --- Rename -------------------------------------------------------------------------------

    [Fact]
    public async Task Rename_TheTargetIsDerivedFromTheFolderOfTheEntryNotFromTheCurrentPath()
    {
        Harness harness = await Harness.CreateAsync();
        harness.ViewModel.CurrentPath = "/elsewhere";
        harness.Dialog.InputAnswer = "new.txt";

        await harness.ViewModel.RenameEntryAsync(SftpTestKit.File("/remote/old.txt"));

        Assert.Equal(
        [
            "test -e '/remote/new.txt' -o -L '/remote/new.txt'",
            PrivilegedFileCommands.BuildHeldRenameBody("/remote/old.txt", "new.txt", replace: false),
        ], harness.RenameCommands);
        Assert.False(harness.ViewModel.IsErrorStatus);
    }

    [Fact]
    public async Task Rename_WhenTheScriptRefusesThePath_NamesTheReasonAndTheEntry()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Dialog.InputAnswer = "new.txt";
        harness.RenameResults.Enqueue(new SudoRenameCommandResult(1, string.Empty, string.Empty));
        harness.RenameResults.Enqueue(new SudoRenameCommandResult(
            PrivilegedFileCommands.NonRegularTargetExitStatus,
            string.Empty,
            PrivilegedFileCommands.EntryRefusedMarker + " pathlink\nRefusing: link."));

        await harness.ViewModel.RenameEntryAsync(SftpTestKit.File("/remote/old.txt"));

        Assert.True(harness.ViewModel.IsErrorStatus);
        Assert.Equal(harness.Localizer.Format(PathLinkKey, "/remote/old.txt"), harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Rename_WhenTheScriptFindsBothNames_ReportsTheCollision()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Dialog.InputAnswer = "new.txt";
        harness.RenameResults.Enqueue(new SudoRenameCommandResult(1, string.Empty, string.Empty));
        harness.RenameResults.Enqueue(new SudoRenameCommandResult(
            PrivilegedFileCommands.DestinationExistsExitStatus,
            string.Empty,
            "Refusing: an entry with the new name already exists."));

        await harness.ViewModel.RenameEntryAsync(SftpTestKit.File("/remote/old.txt"));

        Assert.True(harness.ViewModel.IsErrorStatus);
        Assert.Equal(harness.Localizer["SftpErrorSudoRenameCollision"], harness.ViewModel.StatusText);
        Assert.Equal(2, harness.RenameCommands.Count);
    }

    [Fact]
    public void RenameTargetName_OfAnotherFolderIsRefused()
    {
        MethodInfo method = typeof(EmbeddedSftpViewModel).GetMethod(
            "ExtractRenameTargetName",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Equal("new.txt", method.Invoke(null, ["/remote/old.txt", "/remote/new.txt"]));
        TargetInvocationException otherFolder = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, ["/remote/old.txt", "/other/new.txt"]));
        Assert.IsType<ArgumentException>(otherFolder.InnerException);
        TargetInvocationException deeper = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, ["/remote/old.txt", "/remote/sub/new.txt"]));
        Assert.IsType<ArgumentException>(deeper.InnerException);
    }

    // --- Texts --------------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void TheNewKeysExistInEveryLanguageWithTheirPlaceholder(string language)
    {
        foreach (string key in new[] { ExistsKey, ChangedKey, EntryKey })
        {
            Assert.Contains("{0}", LocaleValue(language, key), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(ExistsKey)]
    [InlineData(ChangedKey)]
    [InlineData(EntryKey)]
    public void TheFrenchTextsCarryAccentsAndDifferFromTheOtherLanguages(string key)
    {
        string french = LocaleValue("fr", key);

        Assert.Contains(french, c => c > '\u007f');
        Assert.NotEqual(LocaleValue("en", key), french);
        Assert.NotEqual(LocaleValue("en", key), LocaleValue("es", key));
    }

    // --- Helpers ------------------------------------------------------------------------------

    private static string LocaleValue(string language, string key)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "locales", language + ".json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(key).GetString()!;
    }

    // What the sudo runner throws for a script that ended with this status and this stderr.
    private static Exception CaughtBy(int exitStatus, string stderr)
    {
        try
        {
            EmbeddedSftpViewModel.EnsureSudoSucceeded(exitStatus, stderr, "command");
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The status did not fail.");
    }

    private sealed class Harness
    {
        private readonly List<string> _mkdirCommands = [];
        private readonly List<string> _renameCommands = [];

        private Harness(
            EmbeddedSftpViewModel viewModel,
            LocalizationManager localizer,
            EmbeddedSftpSudoConsentTests.ConsentDialog dialog)
        {
            ViewModel = viewModel;
            Localizer = localizer;
            Dialog = dialog;
        }

        public EmbeddedSftpViewModel ViewModel { get; }

        public LocalizationManager Localizer { get; }

        public EmbeddedSftpSudoConsentTests.ConsentDialog Dialog { get; }

        public IReadOnlyList<string> MkdirCommands => _mkdirCommands;

        public IReadOnlyList<string> RenameCommands => _renameCommands;

        /// <summary>What the sudo channel does for a folder creation, after the command is recorded.</summary>
        public Func<string, CancellationToken, Task>? MkdirExecutor { get; set; }

        /// <summary>The results the sudo channel gives to the rename commands, in order; when empty, a probe finds nothing and a move succeeds.</summary>
        public Queue<SudoRenameCommandResult> RenameResults { get; } = new();

        public static async Task<Harness> CreateAsync()
        {
            LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync("en");
            ScriptedRemoteBrowser scripted = new()
            {
                RenameHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied"),
            };
            IDialogService service = EmbeddedSftpSudoConsentTests.ConsentDialog.Create(
                out EmbeddedSftpSudoConsentTests.ConsentDialog dialog);
            dialog.InputAnswer = "newdir";
            EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(
                new DeniedMkdirBrowser(scripted),
                localizer: localizer,
                dialogService: service);
            Harness harness = new(viewModel, localizer, dialog);

            viewModel.CurrentPath = "/remote";
            SetField(viewModel, "_sshParams", new SshConnectionParams { Host = "example.test", Username = "tester" });
            viewModel.SetSudoFallbackModeProvider(() => SftpSudoFallbackMode.Automatic);
            SetField(
                viewModel,
                "_sudoMkdirCommandExecutor",
                new Func<string, CancellationToken, Task>((command, ct) =>
                {
                    harness._mkdirCommands.Add(command);
                    return harness.MkdirExecutor?.Invoke(command, ct) ?? Task.CompletedTask;
                }));
            viewModel.SetSudoRenameCommandExecutor((command, _) =>
            {
                harness._renameCommands.Add(command);
                if (harness.RenameResults.Count > 0)
                {
                    return Task.FromResult(harness.RenameResults.Dequeue());
                }

                int exit = command.StartsWith("test -e", StringComparison.Ordinal) ? 1 : 0;
                return Task.FromResult(new SudoRenameCommandResult(exit, string.Empty, string.Empty));
            });
            return harness;
        }

        private static void SetField(EmbeddedSftpViewModel viewModel, string name, object value)
        {
            FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(viewModel, value);
        }
    }

    /// <summary>Wraps the scripted browser and refuses folder creation, so the root path is taken.</summary>
    private sealed class DeniedMkdirBrowser(ScriptedRemoteBrowser inner) : IRemoteBrowser
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

        public string CurrentDirectory => inner.CurrentDirectory;

        public bool IsConnected => inner.IsConnected;

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
            => inner.ListDirectoryAsync(path, ct);

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
            => inner.GetCurrentDirectoryAsync(ct);

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
            => inner.ChangeDirectoryAsync(path, ct);

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
            => inner.DownloadFileAsync(remotePath, localPath, ct);

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
            => inner.UploadFileAsync(localPath, remotePath, ct);

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromException(new SftpPermissionDeniedException("denied"));

        public Task DeleteAsync(string path, CancellationToken ct = default)
            => inner.DeleteAsync(path, ct);

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
            => inner.ChmodAsync(path, mode, ct);

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
            => inner.RenameAsync(oldPath, newPath, ct);

        public Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default)
            => inner.CopyAsync(sourcePath, destinationPath, recursive, ct);

        public void Disconnect() => inner.Disconnect();

        public void Dispose()
        {
        }
    }
}
