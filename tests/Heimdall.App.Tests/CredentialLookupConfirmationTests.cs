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
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Session;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

public sealed class CredentialLookupConfirmationTests
{
    [Fact]
    public void LookupTitle_LocalProfile_IsTheVaultEntryName()
    {
        Assert.Equal("vault-entry", ServerListViewModel.CredentialLookupTitle(ImportedLikeProfile()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LookupTitle_SharedProfile_IsTheHostWhateverTheSourceSaysAboutConfirmation(bool confirmed)
    {
        ServerProfileDto source = ImportedLikeProfile();
        source.Id = SharedSessionIds.PrefixFor("teamsource") + "0123abcd";
        source.CredentialLookupConfirmed = confirmed;

        ServerProfileDto shared = Assert.IsType<ServerProfileDto>(SharedProfileSanitizer.Sanitize(source));

        Assert.Equal(confirmed, shared.CredentialLookupConfirmed);
        Assert.Equal("elsewhere.example.com", ServerListViewModel.CredentialLookupTitle(shared));
    }

    [Fact]
    public void ScannedHost_IsDiscoveredUnconfirmed()
    {
        ServerProfileDto profile = NetworkScannerService.BuildDiscoveredProfile(
            new NetworkScanner.ScanResult("10.0.0.9", true, 3, "prod-db.attacker.example", [22]));

        Assert.False(profile.CredentialLookupConfirmed);
    }

    [Fact]
    public void CitrixCacheProfiles_AreUnconfirmed()
    {
        IReadOnlyList<ServerProfileDto> profiles = CitrixCacheScanner.ToServerProfiles(
        [
            new CitrixResource { FriendlyName = "Calc", CitrixId = "store@@Calc", StoreFrontUrl = "https://sf.example.com" }
        ]);

        Assert.False(Assert.Single(profiles).CredentialLookupConfirmed);
    }

    [Fact]
    public void CompanionSftpProfile_KeepsTheLookupConfirmation()
    {
        ServerProfileDto ssh = ImportedLikeProfile();
        ssh.ConnectionType = "SSH";

        Assert.False(SessionCoordinator.CreateCompanionSftpProfile(ssh, "sftp-1").CredentialLookupConfirmed);

        ssh.CredentialLookupConfirmed = true;
        Assert.True(SessionCoordinator.CreateCompanionSftpProfile(ssh, "sftp-2").CredentialLookupConfirmed);
    }

    // ---- The session dialog -------------------------------------------------------------

    [Fact]
    public void Dialog_RoundTrip_KeepsAnUnconfirmedProfileUnconfirmed()
    {
        ServerProfileDto saved = ServerDialogViewModel.FromDto(ImportedLikeProfile()).ToDto();

        Assert.False(saved.CredentialLookupConfirmed);
    }

    [Fact]
    public void Dialog_NewProfile_IsConfirmed()
    {
        ServerDialogViewModel viewModel = new() { DisplayName = "New", RemoteServer = "new.example.com", ConnectionType = "SSH" };

        Assert.True(viewModel.ToDto().CredentialLookupConfirmed);
        Assert.False(viewModel.ShowCredentialLookupNotice);
    }

    [Fact]
    public void Dialog_AllowLookup_ThenSave_GivesAConfirmedProfile()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(ImportedLikeProfile());
        Assert.True(viewModel.ShowCredentialLookupNotice);

        viewModel.AllowCredentialLookupCommand.Execute(null);

        Assert.False(viewModel.ShowCredentialLookupNotice);
        Assert.True(viewModel.ToDto().CredentialLookupConfirmed);
    }

    [Fact]
    public void Dialog_Notice_NamesTheEntryAndTheDestination()
    {
        LocalizationManager localizer = LoadLocalizer();
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(ImportedLikeProfile());
        viewModel.Localizer = localizer;

        string text = viewModel.CredentialLookupNoticeText;

        Assert.Contains("vault-entry", text, StringComparison.Ordinal);
        Assert.Contains("elsewhere.example.com:22", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":3389", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Dialog_Notice_FollowsTheFieldsAsTheyChange()
    {
        ServerProfileDto profile = ImportedLikeProfile();
        profile.VaultEntryName = null;
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(profile);
        viewModel.Localizer = LoadLocalizer();
        List<string?> raised = [];
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.DisplayName = "Renamed";
        viewModel.RemoteServer = "moved.example.com";

        Assert.Contains(nameof(ServerDialogViewModel.CredentialLookupNoticeText), raised);
        string text = viewModel.CredentialLookupNoticeText;
        Assert.Contains("Renamed", text, StringComparison.Ordinal);
        Assert.Contains("moved.example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("prod-db", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Dialog_RenameThenAllow_SavesTheKeyThatWasShown()
    {
        ServerProfileDto profile = ImportedLikeProfile();
        profile.VaultEntryName = null;
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(profile);
        viewModel.Localizer = LoadLocalizer();
        viewModel.DisplayName = "Renamed";
        string shown = viewModel.CredentialLookupNoticeText;

        viewModel.AllowCredentialLookupCommand.Execute(null);
        ServerProfileDto saved = viewModel.ToDto();

        Assert.True(saved.CredentialLookupConfirmed);
        Assert.Equal("Renamed", CredentialLookupPolicy.NameBasedTitle(saved));
        Assert.Contains("Renamed", shown, StringComparison.Ordinal);
    }

    // ---- Untrusted values --------------------------------------------------------------

    [Fact]
    public void FormatRows_ANameWithLineBreaks_CannotStartAnotherRow()
    {
        CredentialLookupRow row = new("x", "SSH", "host.example.com", 22, string.Empty, [], null);
        row = row with { LookupKey = "first\r\nsecond\u2028third" };

        string text = CredentialLookupConfirmation.FormatRows([row], LoadLocalizer());

        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\u2028', text);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("\\u{000d}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRows_ABidiOverride_IsMadeVisible()
    {
        CredentialLookupRow row = new("a\u202eb", "SSH", "host.example.com", 22, string.Empty, [], null);

        string text = CredentialLookupConfirmation.FormatRows([row], LoadLocalizer());

        Assert.DoesNotContain('\u202e', text);
        Assert.Contains("\\u{202e}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRows_ALongValue_IsTruncated()
    {
        string longName = new('x', CredentialLookupConfirmation.MaxValueLength * 3);
        CredentialLookupRow row = new(longName, "SSH", "host.example.com", 22, string.Empty, [], null);

        string text = CredentialLookupConfirmation.FormatRows([row], LoadLocalizer());

        Assert.DoesNotContain(longName, text, StringComparison.Ordinal);
        Assert.Contains(new string('x', CredentialLookupConfirmation.MaxValueLength) + "...", text, StringComparison.Ordinal);
    }

    // ---- The confirmation rows ----------------------------------------------------------

    [Fact]
    public void Rows_ShowKeyProtocolDestinationUserAndTheGatewayChain()
    {
        AppSettings settings = new()
        {
            SshGateways =
            [
                new SshGatewayDto { Id = "g-edge", Name = "Edge", Host = "edge.example.com", Port = 2222, ParentGatewayId = "g-core" },
                new SshGatewayDto { Id = "g-core", Name = "Core", Host = "core.example.com", Port = 22 },
            ],
            GroupDefaults = new Dictionary<string, GroupDefaultsDto>(StringComparer.OrdinalIgnoreCase)
            {
                ["Imported"] = new GroupDefaultsDto { SshGatewayId = "g-edge" },
            },
        };
        ServerProfileDto profile = ImportedLikeProfile();
        profile.Group = "Imported";
        profile.SshUsername = "ops";
        profile.SshPort = 2200;

        CredentialLookupRow row = Assert.Single(CredentialLookupConfirmation.BuildRows([profile], settings));

        Assert.Equal("vault-entry", row.LookupKey);
        Assert.Equal("SSH", row.Protocol);
        Assert.Equal("elsewhere.example.com", row.Host);
        Assert.Equal(2200, row.Port);
        Assert.Equal("ops", row.User);
        Assert.Equal(["Edge (edge.example.com:2222)", "Core (core.example.com:22)"], row.GatewayChain);
        Assert.Null(row.RdGateway);

        string text = CredentialLookupConfirmation.FormatRows([row], LoadLocalizer());
        Assert.Contains("vault-entry", text, StringComparison.Ordinal);
        Assert.Contains("edge.example.com", text, StringComparison.Ordinal);
        Assert.Contains("core.example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_ShowTheRdGateway_AndTheDisplayNameWhenThereIsNoEntryName()
    {
        ServerProfileDto profile = new()
        {
            DisplayName = "Desk",
            ConnectionType = "RDP",
            RemoteServer = "desk.example.com",
            RdpGateway = "rdgw.example.com",
            CredentialLookupConfirmed = false,
        };

        CredentialLookupRow row = Assert.Single(CredentialLookupConfirmation.BuildRows([profile], new AppSettings()));

        Assert.Equal("Desk", row.LookupKey);
        Assert.Equal("rdgw.example.com", row.RdGateway);
        Assert.Contains("rdgw.example.com", CredentialLookupConfirmation.FormatRows([row], LoadLocalizer()), StringComparison.Ordinal);
    }

    // ---- The confirm command ------------------------------------------------------------

    [Fact]
    public async Task Confirm_Accepted_ConfirmsExactlyTheLocalUnconfirmedProfiles()
    {
        await using ListFixture fixture = await ListFixture.CreateAsync(accept: true);
        ServerProfileDto pending = Profile("pending", confirmed: false);
        ServerProfileDto alreadyConfirmed = Profile("done", confirmed: true);
        ServerProfileDto other = Profile("untouched", confirmed: false);
        await fixture.SeedAsync(pending, alreadyConfirmed, other);
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        IReadOnlyList<ServerItemViewModel> selection =
            [.. fixture.ViewModel.Servers.Where(server => server.Id is "pending" or "done")];

        int confirmed = await fixture.ViewModel.ConfirmCredentialLookupAsync(selection);

        Assert.Equal(1, confirmed);
        Dictionary<string, ServerProfileDto> stored = (await fixture.ConfigManager.LoadServersAsync())
            .ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        Assert.True(stored["pending"].CredentialLookupConfirmed);
        Assert.True(stored["done"].CredentialLookupConfirmed);
        Assert.False(stored["untouched"].CredentialLookupConfirmed);
        Assert.Contains("vault-pending", fixture.LastConfirmMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("vault-done", fixture.LastConfirmMessage, StringComparison.Ordinal);
        Assert.Equal(
            fixture.Localizer.Format("StatusCredentialLookupConfirmedOne", 1),
            Assert.Single(statuses));
        Assert.False(fixture.ViewModel.Servers.Single(server => server.Id == "pending").NeedsCredentialLookupConfirmation);
        Assert.True(fixture.ViewModel.Servers.Single(server => server.Id == "untouched").NeedsCredentialLookupConfirmation);
    }

    [Fact]
    public async Task Confirm_UsesTheNamedButtonOverload_WhereCancelIsTheDefault()
    {
        await using ListFixture fixture = await ListFixture.CreateAsync(accept: true);
        await fixture.SeedAsync(Profile("pending", confirmed: false));

        _ = await fixture.ViewModel.ConfirmCredentialLookupAsync([.. fixture.ViewModel.Servers]);

        Assert.Equal(5, fixture.Dialog.LastArgumentCount);
        Assert.Equal(fixture.Localizer["ServerDialogCredentialLookupAllow"], fixture.Dialog.LastConfirmLabel);
        Assert.Equal(fixture.Localizer["BtnCancel"], fixture.Dialog.LastDeclineLabel);
    }

    [Fact]
    public async Task Confirm_AProfileThatChangedWhileTheDialogWasOpen_IsNotConfirmed()
    {
        await using ListFixture fixture = await ListFixture.CreateAsync(accept: true);
        await fixture.SeedAsync(Profile("stable", confirmed: false), Profile("moving", confirmed: false));
        fixture.Dialog.Hook = async () =>
        {
            List<ServerProfileDto> stored = await fixture.ConfigManager.LoadServersAsync();
            stored.Single(profile => profile.Id == "moving").RemoteServer = "attacker.example.com";
            await fixture.ConfigManager.SaveServersAsync(stored);
        };
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;

        int confirmed = await fixture.ViewModel.ConfirmCredentialLookupAsync([.. fixture.ViewModel.Servers]);

        Assert.Equal(1, confirmed);
        Dictionary<string, ServerProfileDto> stored = (await fixture.ConfigManager.LoadServersAsync())
            .ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        Assert.True(stored["stable"].CredentialLookupConfirmed);
        Assert.False(stored["moving"].CredentialLookupConfirmed);
        Assert.True(fixture.ViewModel.Servers.Single(server => server.Id == "moving").NeedsCredentialLookupConfirmation);
        Assert.Contains(
            fixture.Localizer.Format("StatusCredentialLookupChangedOne", 1),
            Assert.Single(statuses),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_AboveTheCap_ConfirmsOnlyTheRowsShown_AndSaysHowManyRemain()
    {
        await using ListFixture fixture = await ListFixture.CreateAsync(accept: true);
        int total = CredentialLookupConfirmation.MaxRowsShown + 2;
        await fixture.SeedAsync([.. Enumerable.Range(1, total).Select(index => Profile($"p{index:00}", confirmed: false))]);
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;

        int confirmed = await fixture.ViewModel.ConfirmCredentialLookupAsync([.. fixture.ViewModel.Servers]);

        Assert.Equal(CredentialLookupConfirmation.MaxRowsShown, confirmed);
        List<ServerProfileDto> stored = await fixture.ConfigManager.LoadServersAsync();
        Assert.Equal(2, stored.Count(profile => !profile.CredentialLookupConfirmed));
        Assert.Contains(fixture.Localizer.Format("CredentialLookupConfirmMore", 2), fixture.Dialog.LastMessage, StringComparison.Ordinal);
        Assert.Contains(fixture.Localizer.Format("StatusCredentialLookupRemaining", 2), Assert.Single(statuses), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_OneOverTheCap_UsesTheSingularForTheRemainder()
    {
        await using ListFixture fixture = await ListFixture.CreateAsync(accept: true);
        int total = CredentialLookupConfirmation.MaxRowsShown + 1;
        await fixture.SeedAsync([.. Enumerable.Range(1, total).Select(index => Profile($"p{index:00}", confirmed: false))]);

        _ = await fixture.ViewModel.ConfirmCredentialLookupAsync([.. fixture.ViewModel.Servers]);

        Assert.Contains(fixture.Localizer.Format("CredentialLookupConfirmMoreOne", 1), fixture.Dialog.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_Cancelled_ChangesNothing()
    {
        await using ListFixture fixture = await ListFixture.CreateAsync(accept: false);
        await fixture.SeedAsync(Profile("pending", confirmed: false));

        int confirmed = await fixture.ViewModel.ConfirmCredentialLookupAsync([.. fixture.ViewModel.Servers]);

        Assert.Equal(0, confirmed);
        Assert.False(Assert.Single(await fixture.ConfigManager.LoadServersAsync()).CredentialLookupConfirmed);
        Assert.True(fixture.ViewModel.Servers.Single().NeedsCredentialLookupConfirmation);
    }

    [Fact]
    public async Task Confirm_NothingToConfirm_AsksNothing()
    {
        await using ListFixture fixture = await ListFixture.CreateAsync(accept: true);
        await fixture.SeedAsync(Profile("done", confirmed: true));

        int confirmed = await fixture.ViewModel.ConfirmCredentialLookupAsync([.. fixture.ViewModel.Servers]);

        Assert.Equal(0, confirmed);
        Assert.Equal(0, fixture.ConfirmCount);
    }

    [Fact]
    public async Task Confirm_SharedProfile_IsIgnored()
    {
        await using ListFixture fixture = await ListFixture.CreateAsync(accept: true);
        ServerItemViewModel shared = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = SharedSessionIds.PrefixFor("teamsource") + "0123abcd",
            DisplayName = "Shared",
            ConnectionType = "SSH",
            RemoteServer = "shared.example.com",
            CredentialLookupConfirmed = false,
        });

        int confirmed = await fixture.ViewModel.ConfirmCredentialLookupAsync([shared]);

        Assert.Equal(0, confirmed);
        Assert.False(shared.NeedsCredentialLookupConfirmation);
        Assert.Equal(0, fixture.ConfirmCount);
    }

    // ---- Renames ------------------------------------------------------------------------

    [Fact]
    public async Task RenameService_UnconfirmedProfile_DoesNotCopyTheOldNameIntoTheEntryName()
    {
        using TempConfig temp = new();
        await temp.ConfigManager.SaveServersAsync([Profile("p1", confirmed: false, vaultEntry: null)]);

        _ = await new ServerRenameService(temp.ConfigManager).RenameAsync("p1", "Renamed");

        ServerProfileDto stored = Assert.Single(await temp.ConfigManager.LoadServersAsync());
        Assert.Equal("Renamed", stored.DisplayName);
        Assert.Null(stored.VaultEntryName);
    }

    [Fact]
    public async Task RenameService_ConfirmedProfile_StillFreezesTheOldName()
    {
        using TempConfig temp = new();
        await temp.ConfigManager.SaveServersAsync([Profile("p1", confirmed: true, vaultEntry: null)]);

        _ = await new ServerRenameService(temp.ConfigManager).RenameAsync("p1", "Renamed");

        Assert.Equal("Name-p1", Assert.Single(await temp.ConfigManager.LoadServersAsync()).VaultEntryName);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "Name-p1")]
    public async Task ConfigManagerSave_Rename_FreezesTheEntryNameOnlyForAConfirmedProfile(bool confirmed, string? expected)
    {
        using TempConfig temp = new();
        await temp.ConfigManager.SaveServersAsync([Profile("p1", confirmed, vaultEntry: null)]);
        List<ServerProfileDto> renamed = await temp.ConfigManager.LoadServersAsync();
        renamed[0].DisplayName = "Renamed";

        await temp.ConfigManager.SaveServersAsync(renamed);

        Assert.Equal(expected, Assert.Single(await temp.ConfigManager.LoadServersAsync()).VaultEntryName);
    }

    // ---- .rdp Replace -------------------------------------------------------------------

    [Fact]
    public async Task RdpReplace_KeepsTheLinkageButClearsTheConfirmation()
    {
        using TempConfig temp = new();
        LocalizationManager localizer = LoadLocalizer();
        RdpImportService service = new(temp.ConfigManager, localizer);
        await temp.ConfigManager.SaveServersAsync(
        [
            new ServerProfileDto
            {
                Id = "keep-me",
                DisplayName = "Server01",
                ConnectionType = "RDP",
                RemoteServer = "10.0.0.5",
                VaultEntryName = "Server01 (renamed)"
            }
        ]);
        string importsDirectory = Path.Combine(temp.Root, "imports");
        Directory.CreateDirectory(importsDirectory);
        string path = Path.Combine(importsDirectory, "Server01.rdp");
        await File.WriteAllTextAsync(path, "full address:s:evil.example.com");
        RdpImportPreview preview = await service.PreviewAsync([path], CancellationToken.None);

        _ = await service.ApplyAsync(
            preview,
            new RdpImportSelection
            {
                Entries =
                [
                    new RdpImportSelectionEntry
                    {
                        SourceFilePath = path,
                        IsSelected = true,
                        ConflictResolution = RdpConflictResolution.Replace
                    }
                ]
            },
            CancellationToken.None);

        ServerProfileDto replaced = Assert.Single(await temp.ConfigManager.LoadServersAsync());
        Assert.Equal("evil.example.com", replaced.RemoteServer);
        Assert.Equal("Server01 (renamed)", replaced.VaultEntryName);
        Assert.False(replaced.CredentialLookupConfirmed);
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static LocalizationManager LoadLocalizer()
    {
        LocalizationManager localizer = new();
        localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en").GetAwaiter().GetResult();
        return localizer;
    }

    private static ServerProfileDto Profile(string id, bool confirmed, string? vaultEntry = "")
        => new()
        {
            Id = id,
            DisplayName = "Name-" + id,
            ConnectionType = "SSH",
            RemoteServer = id + ".example.com",
            VaultEntryName = vaultEntry == "" ? "vault-" + id : vaultEntry,
            CredentialLookupConfirmed = confirmed,
        };

    private static ServerProfileDto ImportedLikeProfile() => new()
    {
        Id = "imported-1",
        DisplayName = "prod-db",
        ConnectionType = "SSH",
        RemoteServer = "elsewhere.example.com",
        VaultEntryName = "vault-entry",
        CredentialLookupConfirmed = false,
    };

    private sealed class TempConfig : IDisposable
    {
        public TempConfig()
        {
            Root = Path.Combine(Path.GetTempPath(), "heimdall-lookup-confirm-tests", Guid.NewGuid().ToString("N"));
            ConfigManager = new ConfigManager(Root);
        }

        public string Root { get; }

        public ConfigManager ConfigManager { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A server list over a real configuration, with a dialog that answers every confirmation the same way.</summary>
    private sealed class ListFixture : IAsyncDisposable
    {
        private readonly string _root;

        private ListFixture(string root, ServerListViewModel viewModel, ConfigManager configManager, LocalizationManager localizer, ConfirmDialog dialog)
        {
            _root = root;
            ViewModel = viewModel;
            ConfigManager = configManager;
            Localizer = localizer;
            Dialog = dialog;
        }

        public ServerListViewModel ViewModel { get; }

        public ConfigManager ConfigManager { get; }

        public LocalizationManager Localizer { get; }

        public ConfirmDialog Dialog { get; }

        public int ConfirmCount => Dialog.ConfirmCount;

        public string LastConfirmMessage => Dialog.LastMessage;

        public static async Task<ListFixture> CreateAsync(bool accept)
        {
            string root = Path.Combine(Path.GetTempPath(), "heimdall-lookup-confirm-list", Guid.NewGuid().ToString("N"));
            ConfigManager configManager = new(root);
            await configManager.InitializeAsync();
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            IDialogService dialogService = DispatchProxy.Create<IDialogService, ConfirmDialog>();
            ConfirmDialog dialog = (ConfirmDialog)(object)dialogService;
            dialog.Accept = accept;

            ServerListViewModel viewModel = new(
                configManager,
                localizer,
                new FakeUiDispatcher(),
                new ConnectionStateMachine(),
                new ConnectionService(configManager, localizer, new NullTunnelService(), []),
                dialogService,
                new NullRdpImportService(),
                new PuttySessionImporter(new FakePuttySessionRegistrySource([]), configManager),
                new KnownHostsImporter(configManager, new HostKeyStore()));

            return new ListFixture(root, viewModel, configManager, localizer, dialog);
        }

        public async Task SeedAsync(params ServerProfileDto[] servers)
        {
            await ConfigManager.SaveServersAsync([.. servers]);
            ViewModel.LoadServers(await ConfigManager.LoadServersAsync(), await ConfigManager.LoadSettingsAsync());
        }

        public ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private class ConfirmDialog : DispatchProxy
    {
        public bool Accept { get; set; }

        /// <summary>Runs after the user answers and before the dialog returns.</summary>
        public Func<Task>? Hook { get; set; }

        public int ConfirmCount { get; private set; }

        public string LastMessage { get; private set; } = string.Empty;

        public int LastArgumentCount { get; private set; }

        public string? LastConfirmLabel { get; private set; }

        public string? LastDeclineLabel { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowConfirmAsync))
            {
                ConfirmCount++;
                LastMessage = (string)args![1]!;
                LastArgumentCount = args.Length;
                LastConfirmLabel = args.Length == 5 ? (string?)args[3] : null;
                LastDeclineLabel = args.Length == 5 ? (string?)args[4] : null;
                return Task.Run(async () =>
                {
                    if (Hook is not null)
                    {
                        await Hook();
                    }

                    return Accept;
                });
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class NullTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
            => Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class NullRdpImportService : IRdpImportService
    {
        public Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct) =>
            Task.FromResult(new RdpImportPreview { Entries = [], FilesNotFound = [], FilesUnreadable = [] });

        public Task<RdpImportResult> ApplyAsync(RdpImportPreview preview, RdpImportSelection selection, CancellationToken ct) =>
            Task.FromResult(new RdpImportResult());
    }
}
