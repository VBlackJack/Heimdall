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
using System.Text.RegularExpressions;
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
/// What the pane's view model shows of the acts it ran as root: the badge state, the sentence, the
/// list, in the real English, French and Spanish texts, and that the indicator changes nothing
/// about what runs or what the status line says.
/// </summary>
public sealed class EmbeddedSftpRootActivityViewModelTests
{
    private const string ModeText = "640";
    private static readonly string[] Locales = ["en", "fr", "es"];
    private static readonly Regex s_placeholder = new(@"\{\d+\}", RegexOptions.Compiled);

    // --- What each act does to the badge ---------------------------------------------------------

    [Fact]
    public async Task ASudoRename_GivesOneRenameEntryAndTheStatusIsStillReady()
    {
        Harness harness = await Harness.CreateAsync("en");
        harness.Dialog.Input = "new.txt";
        harness.ViewModel.CurrentPath = "/remote";
        Queue<int> exitStatuses = new([1, 0]);
        List<string> commands = [];
        harness.ViewModel.SetSudoRenameCommandExecutor((command, _) =>
        {
            commands.Add(command);
            return Task.FromResult(new SudoRenameCommandResult(exitStatuses.Dequeue(), string.Empty, string.Empty));
        });

        await harness.ViewModel.RenameEntryAsync(Entry("old.txt", "/remote/old.txt", RemoteEntryKind.File));

        Assert.Equal(2, commands.Count);
        SftpRootActivityEntry entry = Assert.Single(harness.ViewModel.RootActivityEntries);
        Assert.Equal("Rename: /remote/old.txt to /remote/new.txt (succeeded)", entry.Text);
        Assert.False(entry.IsSummary);
        Assert.True(harness.ViewModel.HasRootActivity);
        Assert.True(harness.ViewModel.HasRootOperations);
        Assert.Equal("1", harness.ViewModel.RootOperationCountText);
        Assert.Equal(harness.Localizer["SftpStatusReady"], harness.ViewModel.StatusText);
        Assert.False(harness.ViewModel.IsErrorStatus);
    }

    [Theory]
    [InlineData(RemoteEntryKind.SymbolicLink)]
    [InlineData(RemoteEntryKind.Device)]
    public async Task AChmodRefusedOnALink_RecordsNothing(RemoteEntryKind kind)
    {
        Harness harness = await Harness.CreateAsync("en");
        harness.Dialog.Input = ModeText;
        harness.Browser.Deny = true;

        await harness.ViewModel.ChmodEntriesAsync([Entry("lnk", "/srv/lnk", kind)]);

        Assert.Empty(harness.ChmodCommands);
        Assert.False(harness.ViewModel.HasRootActivity);
        Assert.False(harness.ViewModel.HasRootOperations);
        Assert.Empty(harness.ViewModel.RootActivityEntries);
        Assert.Equal(0, harness.ViewModel.RootActivity.Snapshot().OperationCount);
    }

    [Fact]
    public async Task AnAcceptedChmod_IsRecordedAsAPermissionChange()
    {
        Harness harness = await Harness.CreateAsync("en");
        harness.Dialog.Input = ModeText;
        harness.Browser.Deny = true;

        await harness.ViewModel.ChmodEntriesAsync([Entry("notes", "/srv/notes", RemoteEntryKind.File)]);

        Assert.Single(harness.ChmodCommands);
        SftpRootActivityEntry entry = Assert.Single(harness.ViewModel.RootActivityEntries);
        Assert.Equal("Permission change: /srv/notes (succeeded)", entry.Text);
    }

    [Fact]
    public async Task AFailedChmod_IsRecordedAsFailedAndCountedInTheSummary()
    {
        Harness harness = await Harness.CreateAsync("en");
        harness.Dialog.Input = ModeText;
        harness.Browser.Deny = true;
        harness.ChmodExecutor = (_, _) => Task.FromException(new InvalidOperationException("sudo chmod failed"));

        await harness.ViewModel.ChmodEntriesAsync([Entry("notes", "/srv/notes", RemoteEntryKind.File)]);

        SftpRootActivityEntry entry = Assert.Single(harness.ViewModel.RootActivityEntries);
        Assert.Equal("Permission change: /srv/notes (failed)", entry.Text);
        Assert.Contains("1 failed", harness.ViewModel.RootActivitySummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeclinedSudoDelete_RecordsNothing()
    {
        Harness harness = await Harness.CreateAsync("en");
        harness.Browser.Deny = true;
        harness.Dialog.EscalationAnswer = false;

        await harness.ViewModel.DeleteEntriesAsync([Entry("data", "/srv/data", RemoteEntryKind.Directory)]);

        Assert.Empty(harness.DeleteCommands);
        Assert.False(harness.ViewModel.HasRootActivity);
        Assert.Equal(0, harness.ViewModel.RootActivity.Snapshot().OperationCount);
    }

    [Fact]
    public async Task AnAcceptedSudoDelete_IsRecordedAsADelete()
    {
        Harness harness = await Harness.CreateAsync("en");
        harness.Browser.Deny = true;
        harness.Dialog.EscalationAnswer = true;

        await harness.ViewModel.DeleteEntriesAsync([Entry("data", "/srv/data", RemoteEntryKind.Directory)]);

        Assert.Single(harness.DeleteCommands);
        SftpRootActivityEntry entry = Assert.Single(harness.ViewModel.RootActivityEntries);
        Assert.Equal("Delete: /srv/data (succeeded)", entry.Text);
    }

    [Fact]
    public async Task ASudoDeleteThatFails_IsRecordedAsFailed()
    {
        Harness harness = await Harness.CreateAsync("en");
        harness.Browser.Deny = true;
        harness.Dialog.EscalationAnswer = true;
        harness.DeleteExecutor = (_, _) => Task.FromException(new InvalidOperationException("sudo rm failed"));

        await harness.ViewModel.DeleteEntriesAsync([Entry("data", "/srv/data", RemoteEntryKind.File)]);

        SftpRootActivityEntry entry = Assert.Single(harness.ViewModel.RootActivityEntries);
        Assert.Equal("Delete: /srv/data (failed)", entry.Text);
    }

    [Fact]
    public async Task ARootListingThatFails_IsAFailedListingAndNeverAnOperation()
    {
        Harness harness = await Harness.CreateAsync("en");
        MethodInfo listing = typeof(EmbeddedSftpViewModel).GetMethod(
            "ListDirectoryViaSudoAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        harness.SetSshParams(null);

        // With no connection settings the privileged channel cannot even be opened: the listing is
        // attempted, fails at once, and must be counted as such.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => (Task<IReadOnlyList<SftpFileInfo>>)listing.Invoke(harness.ViewModel, ["/root", CancellationToken.None])!);

        SftpRootActivitySnapshot snapshot = harness.ViewModel.RootActivity.Snapshot();
        Assert.Equal(1, snapshot.ListingCount);
        Assert.Equal(1, snapshot.FailedListingCount);
        Assert.Equal(0, snapshot.OperationCount);
        Assert.True(harness.ViewModel.HasRootActivity);
        Assert.False(harness.ViewModel.HasRootOperations);
        SftpRootActivityEntry summaryLine = Assert.Single(harness.ViewModel.RootActivityEntries);
        Assert.True(summaryLine.IsSummary);
        Assert.Equal("1 directory listing as root, latest: /root", summaryLine.Text);
        Assert.Contains("1 directory listing", harness.ViewModel.RootActivitySummary, StringComparison.Ordinal);
    }

    [Fact]
    public void BeforeAnyRootAct_NothingIsShown()
    {
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher());

        Assert.False(viewModel.HasRootActivity);
        Assert.False(viewModel.HasRootOperations);
        Assert.Empty(viewModel.RootActivityEntries);
        Assert.Equal(string.Empty, viewModel.RootActivitySummary);
    }

    [Fact]
    public async Task APathWithAnUnderscoreOrControlCharacters_IsShownWhole()
    {
        Harness harness = await Harness.CreateAsync("en");
        string path = "/srv/_a_b\nc" + (char)0x202E + "d";

        harness.ViewModel.RootActivity.Record(SftpRootActKind.Delete, path, null, SftpRootActOutcome.Succeeded);

        SftpRootActivityEntry entry = Assert.Single(harness.ViewModel.RootActivityEntries);
        Assert.Contains("/srv/_a_b", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', entry.Text);
        Assert.DoesNotContain((char)0x202E, entry.Text);
        Assert.DoesNotContain('\n', entry.AutomationName);
    }

    [Fact]
    public async Task AfterTheViewModelIsDisposed_TheBadgeNoLongerMoves()
    {
        Harness harness = await Harness.CreateAsync("en");

        harness.ViewModel.MarkDisposed();
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Delete, "/a", null, SftpRootActOutcome.Succeeded);

        Assert.False(harness.ViewModel.HasRootActivity);
    }

    // --- The texts, in the real localizers -------------------------------------------------------

    [Fact]
    public async Task TheSummary_PluralisesInEnglish()
    {
        Harness harness = await Harness.CreateAsync("en");
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Delete, "/a", null, SftpRootActOutcome.Succeeded);
        string one = harness.ViewModel.RootActivitySummary;
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Delete, "/b", null, SftpRootActOutcome.Failed);
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Listing, "/c", null, SftpRootActOutcome.Succeeded);
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Listing, "/d", null, SftpRootActOutcome.Succeeded);

        Assert.Contains("1 operation.", one, StringComparison.Ordinal);
        Assert.StartsWith("Root (sudo) used in this pane: 1 operation.", one, StringComparison.Ordinal);
        Assert.StartsWith(
            "Root (sudo) used in this pane: 2 operations, 1 failed, 2 directory listings.",
            harness.ViewModel.RootActivitySummary,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummary_PluralisesInFrench_WhereZeroTakesTheSingular()
    {
        Harness harness = await Harness.CreateAsync("fr");
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Listing, "/a", null, SftpRootActOutcome.Succeeded);
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Delete, "/b", null, SftpRootActOutcome.Succeeded);
        string one = harness.ViewModel.RootActivitySummary;
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Delete, "/c", null, SftpRootActOutcome.Succeeded);

        Assert.Contains("1 op\u00e9ration,", one, StringComparison.Ordinal);
        Assert.Contains("1 listage de dossier", one, StringComparison.Ordinal);
        Assert.Contains("2 op\u00e9rations,", harness.ViewModel.RootActivitySummary, StringComparison.Ordinal);
        Assert.DoesNotContain("SftpRoot", harness.ViewModel.RootActivitySummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "attempted as root", "The number")]
    [InlineData("fr", "t\u00e9es en tant que root", "Le nombre")]
    [InlineData("es", "intentadas como root", "El n\u00famero")]
    public async Task TheSummary_SaysOperationsAreAttemptedAndTalksOfTheNumberOnlyWhenItIsShown(
        string locale,
        string attempted,
        string numberSentence)
    {
        Harness harness = await Harness.CreateAsync(locale);
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Listing, "/a", null, SftpRootActOutcome.Succeeded);
        string listingsOnly = harness.ViewModel.RootActivitySummary;
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Delete, "/b", null, SftpRootActOutcome.Failed);
        string withOperations = harness.ViewModel.RootActivitySummary;

        Assert.DoesNotContain(numberSentence, listingsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("SftpRoot", listingsOnly, StringComparison.Ordinal);
        Assert.Contains(numberSentence, withOperations, StringComparison.Ordinal);
        Assert.Contains(attempted, withOperations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheListingsOnlySummary_IsTheShortEnglishSentence()
    {
        Harness harness = await Harness.CreateAsync("en");
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Listing, "/a", null, SftpRootActOutcome.Succeeded);

        Assert.Equal(
            "Root (sudo) used in this pane: 1 directory listing. Open the list for the latest root actions.",
            harness.ViewModel.RootActivitySummary);
        Assert.False(harness.ViewModel.HasRootOperations);
    }

    [Fact]
    public async Task TheSpanishFailedOutcome_IsFallo()
    {
        Harness harness = await Harness.CreateAsync("es");
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Delete, "/a", null, SftpRootActOutcome.Failed);

        Assert.Equal("Eliminaci\u00f3n: /a (fallo)", Assert.Single(harness.ViewModel.RootActivityEntries).Text);
    }

    [Fact]
    public void ADispatchThatThrowsOnce_DoesNotFreezeTheBadge()
    {
        bool thrown = false;
        FakeUiDispatcher dispatcher = new()
        {
            InvokeAsyncActionHandler = action =>
            {
                if (!thrown)
                {
                    thrown = true;
                    throw new InvalidOperationException("the dispatcher is shutting down");
                }

                action();
            },
        };
        EmbeddedSftpViewModel viewModel = new(dispatcher);

        viewModel.RootActivity.Record(SftpRootActKind.Delete, "/a", null, SftpRootActOutcome.Succeeded);
        Assert.False(viewModel.HasRootActivity);

        viewModel.RootActivity.Record(SftpRootActKind.Delete, "/b", null, SftpRootActOutcome.Succeeded);

        Assert.True(thrown);
        Assert.True(viewModel.HasRootActivity);
        Assert.Equal("2", viewModel.RootOperationCountText);
    }

    [Fact]
    public async Task TheListingLine_ComesFirstAndCarriesTheLatestListing()
    {
        Harness harness = await Harness.CreateAsync("en");
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Upload, "/srv/up", null, SftpRootActOutcome.Succeeded);
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Listing, "/etc", null, SftpRootActOutcome.Succeeded);
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Listing, "/var", null, SftpRootActOutcome.Succeeded);
        harness.ViewModel.RootActivity.Record(SftpRootActKind.Delete, "/srv/gone", null, SftpRootActOutcome.Cancelled);

        IReadOnlyList<SftpRootActivityEntry> entries = harness.ViewModel.RootActivityEntries;

        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsSummary);
        Assert.Equal("2 directory listings as root, latest: /var", entries[0].Text);
        Assert.Equal("Delete: /srv/gone (cancelled)", entries[1].Text);
        Assert.Equal("Upload: /srv/up (succeeded)", entries[2].Text);
        Assert.Equal(
            "Delete: /srv/gone, cancelled, " + entries[1].TimeText,
            entries[1].AutomationName);
    }

    [Fact]
    public async Task AtMostTwentyOperationsAreListed()
    {
        Harness harness = await Harness.CreateAsync("en");
        for (int index = 0; index < SftpRootActivityLog.MaxRecentOperations + 5; index++)
        {
            harness.ViewModel.RootActivity.Record(SftpRootActKind.Delete, "/f" + index, null, SftpRootActOutcome.Succeeded);
        }

        Assert.Equal(SftpRootActivityLog.MaxRecentOperations, harness.ViewModel.RootActivityEntries.Count);
        Assert.Equal(
            (SftpRootActivityLog.MaxRecentOperations + 5).ToString(System.Globalization.CultureInfo.CurrentCulture),
            harness.ViewModel.RootOperationCountText);
    }

    [Fact]
    public async Task ChangingTheLocale_RewritesTheSummaryAndTheList()
    {
        Harness harness = await Harness.CreateAsync("en");
        harness.ViewModel.RootActivity.Record(SftpRootActKind.NewFolder, "/srv/new", null, SftpRootActOutcome.Succeeded);
        Assert.Equal("New folder: /srv/new (succeeded)", Assert.Single(harness.ViewModel.RootActivityEntries).Text);

        await harness.Localizer.SwitchLocaleAsync("fr");

        Assert.Equal(
            "Nouveau dossier : /srv/new (succ\u00e8s)",
            Assert.Single(harness.ViewModel.RootActivityEntries).Text);
        Assert.StartsWith("Root (sudo) utilis\u00e9 dans ce panneau", harness.ViewModel.RootActivitySummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task EveryKind_IsNamedByAKeyTheLocaleDefines(string locale)
    {
        Harness harness = await Harness.CreateAsync(locale);
        HashSet<string> unmapped = [];

        foreach (SftpRootActKind kind in Enum.GetValues<SftpRootActKind>().Where(kind => kind != SftpRootActKind.Listing))
        {
            harness.ViewModel.RootActivity.Record(kind, "/srv/a", "/srv/b", SftpRootActOutcome.Succeeded);
        }

        foreach (SftpRootActivityEntry entry in harness.ViewModel.RootActivityEntries)
        {
            if (entry.Text.Contains("SftpRoot", StringComparison.Ordinal))
            {
                _ = unmapped.Add(entry.Text);
            }
        }

        Assert.Empty(unmapped);
        Assert.Equal(
            Enum.GetValues<SftpRootActKind>().Length - 1,
            harness.ViewModel.RootActivityEntries.Count);
        Assert.Equal(0, harness.Localizer.MissedKeyCount);
    }

    [Fact]
    public void EveryKeyOfTheIndicator_IsDefinedInTheThreeFilesWithTheSamePlaceholders()
    {
        Dictionary<string, Dictionary<string, string>> files = Locales.ToDictionary(
            locale => locale,
            locale => JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(LocalesPath(), locale + ".json")))!);

        foreach (string key in SftpRootActivityLocaleKeys.All)
        {
            string[] placeholders = [];
            foreach (string locale in Locales)
            {
                Assert.True(files[locale].TryGetValue(key, out string? value), $"{locale} lacks {key}");
                string[] found = s_placeholder.Matches(value!).Select(match => match.Value).Distinct().Order().ToArray();
                if (locale == Locales[0])
                {
                    placeholders = found;
                }

                Assert.True(
                    placeholders.SequenceEqual(found),
                    $"{key} in {locale} has the placeholders [{string.Join(",", found)}], English has [{string.Join(",", placeholders)}]");
            }
        }
    }

    [Fact]
    public void TheKeyList_HoldsEveryKindAndOutcomeKey()
    {
        foreach (SftpRootActKind kind in Enum.GetValues<SftpRootActKind>().Where(kind => kind != SftpRootActKind.Listing))
        {
            Assert.Contains(SftpRootActivityLocaleKeys.ForKind(kind), SftpRootActivityLocaleKeys.All);
        }

        foreach (SftpRootActOutcome outcome in Enum.GetValues<SftpRootActOutcome>())
        {
            Assert.Contains(SftpRootActivityLocaleKeys.ForOutcome(outcome), SftpRootActivityLocaleKeys.All);
        }
    }

    // --- Support ---------------------------------------------------------------------------------

    private static string LocalesPath() => Path.Combine(AppContext.BaseDirectory, "locales");

    private static SftpFileInfo Entry(string name, string fullPath, RemoteEntryKind kind)
        => new(name, fullPath, kind, 1, DateTime.UnixEpoch, "rw-r--r--", "1000", "1000");

    private sealed class Harness
    {
        private readonly List<string> _chmodCommands = [];
        private readonly List<string> _deleteCommands = [];

        private Harness(EmbeddedSftpViewModel viewModel, LocalizationManager? localizer, ScriptedBrowser browser, ScriptedDialog dialog)
        {
            ViewModel = viewModel;
            Localizer = localizer!;
            Browser = browser;
            Dialog = dialog;
        }

        public EmbeddedSftpViewModel ViewModel { get; }

        public LocalizationManager Localizer { get; }

        public ScriptedBrowser Browser { get; }

        public ScriptedDialog Dialog { get; }

        public IReadOnlyList<string> ChmodCommands => _chmodCommands;

        public IReadOnlyList<string> DeleteCommands => _deleteCommands;

        public Func<string, CancellationToken, Task>? ChmodExecutor { get; set; }

        public Func<string, CancellationToken, Task>? DeleteExecutor { get; set; }

        public static async Task<Harness> CreateAsync(string locale)
        {
            LocalizationManager localizer = new();
            await localizer.LoadAsync(LocalesPath(), locale);
            EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher());
            ScriptedBrowser browser = new();
            ScriptedDialog dialog = new();
            Harness harness = new(viewModel, localizer, browser, dialog);

            SetField(viewModel, "_browser", browser);
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

            SetField(viewModel, "_localizer", localizer);
            return harness;
        }

        public void SetSshParams(SshConnectionParams? value) => SetField(ViewModel, "_sshParams", value);

        private static void SetField(EmbeddedSftpViewModel viewModel, string name, object? value)
        {
            FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(viewModel, value);
        }
    }

    private sealed class ScriptedBrowser : IRemoteBrowser
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

        /// <summary>Whether chmod, delete and rename are refused for lack of permission.</summary>
        public bool Deny { get; set; }

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
            => Deny ? Task.FromException(new SftpPermissionDeniedException("denied")) : Task.CompletedTask;

        public Task DeleteAsync(string path, CancellationToken ct = default)
            => Deny ? Task.FromException(new SftpPermissionDeniedException("denied")) : Task.CompletedTask;

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
            => Task.FromException(new SftpPermissionDeniedException("denied"));

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
            => Task.FromResult(CurrentDirectory);

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
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

    /// <summary>Answers the input prompt with a fixed text, the first question with yes, and the root question as set.</summary>
    private sealed class ScriptedDialog : IDialogService
    {
        public string? Input { get; set; }

        public bool EscalationAnswer { get; set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
            => Task.FromResult(true);

        public Task<bool> ShowConfirmAsync(
            string title,
            string message,
            string severity,
            string confirmLabel,
            string declineLabel)
            => Task.FromResult(EscalationAnswer);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => throw new NotSupportedException();

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
            => Task.FromResult(Input);

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
