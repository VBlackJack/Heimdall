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

using System.Globalization;
using System.IO;
using System.Windows.Media;
using Heimdall.App.Converters;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.SessionHealth;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Tests;

public sealed class ServerItemViewModelLocalizationTests
{
    [Fact]
    public async Task AuthSummary_WithFrenchLocalizer_UsesLocalizedLabels()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("fr");

        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateSshServer(),
            localizer: localizer);

        Assert.Equal("Nom d'utilisateur + Clé SSH + Mot de passe", viewModel.AuthSummary);
    }

    [Fact]
    public void AuthSummary_WithoutLocalizer_UsesEnglishFallbacks()
    {
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(CreateSshServer());

        Assert.Equal("Username + SSH key + Password", viewModel.AuthSummary);
    }

    [Fact]
    public async Task RefreshLocalizedState_AfterLocaleChange_RecomputesAuthSummary()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            new ServerProfileDto
            {
                Id = "rdp-1",
                DisplayName = "Production",
                ConnectionType = "RDP",
                RemoteServer = "prod.example.test"
            },
            localizer: localizer);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal("No saved credentials", viewModel.AuthSummary);

        await localizer.SwitchLocaleAsync("fr");
        viewModel.RefreshLocalizedState();

        Assert.Equal("Aucun identifiant enregistré", viewModel.AuthSummary);
        Assert.Contains(nameof(ServerItemViewModel.AuthSummary), changed);
    }

    [Theory]
    [InlineData(false, false, false, "No saved credentials")]
    [InlineData(true, false, false, "Username")]
    [InlineData(false, true, false, "SSH key")]
    [InlineData(false, false, true, "Password")]
    [InlineData(true, true, false, "Username + SSH key")]
    [InlineData(true, false, true, "Username + Password")]
    [InlineData(false, true, true, "SSH key + Password")]
    [InlineData(true, true, true, "Username + SSH key + Password")]
    public void AuthSummary_Ssh_ReportsEveryConfiguredCredentialCombination(
        bool hasUsername,
        bool hasKey,
        bool hasPassword,
        string expected)
    {
        ServerProfileDto dto = CreateCredentialServer("SSH", hasUsername, hasPassword);
        dto.SshKeyPath = hasKey ? @"C:\Keys\configured.ppk" : null;
        dto.SshAgentForwarding = true;

        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(dto);

        Assert.Equal(expected, viewModel.AuthSummary);
    }

    [Theory]
    [InlineData(false, false, false, "No saved credentials")]
    [InlineData(true, false, false, "Username")]
    [InlineData(false, true, false, "SSH key")]
    [InlineData(false, false, true, "Password")]
    [InlineData(true, true, false, "Username + SSH key")]
    [InlineData(true, false, true, "Username + Password")]
    [InlineData(false, true, true, "SSH key + Password")]
    [InlineData(true, true, true, "Username + SSH key + Password")]
    public void AuthSummary_Sftp_ReportsEveryConfiguredCredentialCombination(
        bool hasUsername,
        bool hasKey,
        bool hasPassword,
        string expected)
    {
        ServerProfileDto dto = CreateCredentialServer("SFTP", hasUsername, hasPassword);
        dto.SshKeyPath = hasKey ? @"C:\Keys\configured.ppk" : null;
        dto.SshAgentForwarding = true;

        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(dto);

        Assert.Equal(expected, viewModel.AuthSummary);
    }

    [Theory]
    [InlineData(false, false, "No saved credentials")]
    [InlineData(true, false, "Username")]
    [InlineData(false, true, "Password")]
    [InlineData(true, true, "Username + Password")]
    public void AuthSummary_Rdp_ReportsEveryConfiguredCredentialCombination(
        bool hasUsername,
        bool hasPassword,
        string expected)
    {
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateCredentialServer("RDP", hasUsername, hasPassword));

        Assert.Equal(expected, viewModel.AuthSummary);
    }

    [Theory]
    [InlineData(false, false, "No saved credentials")]
    [InlineData(true, false, "Username")]
    [InlineData(false, true, "Password")]
    [InlineData(true, true, "Username + Password")]
    public void AuthSummary_WinRmCredential_ReportsEveryConfiguredCredentialCombination(
        bool hasUsername,
        bool hasPassword,
        string expected)
    {
        ServerProfileDto dto = CreateCredentialServer("WINRM", hasUsername, hasPassword);
        dto.WinRmIdentityMode = WinRmIdentityMode.Credential;

        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(dto);

        Assert.Equal(expected, viewModel.AuthSummary);
    }

    [Fact]
    public void AuthSummary_WinRmCurrentUser_ReportsConfiguredIdentityMode()
    {
        ServerProfileDto dto = CreateCredentialServer("WINRM", hasUsername: true, hasPassword: true);
        dto.WinRmIdentityMode = WinRmIdentityMode.CurrentUser;

        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(dto);

        Assert.Equal("Current user", viewModel.AuthSummary);
    }

    [Theory]
    [InlineData(false, false, "No saved credentials")]
    [InlineData(true, false, "Username")]
    [InlineData(false, true, "Password")]
    [InlineData(true, true, "Username + Password")]
    public void AuthSummary_Ftp_ReportsEveryConfiguredCredentialCombination(
        bool hasUsername,
        bool hasPassword,
        string expected)
    {
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateCredentialServer("FTP", hasUsername, hasPassword));

        Assert.Equal(expected, viewModel.AuthSummary);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AuthSummary_Telnet_RemainsEmptyForEveryStoredCredentialCombination(
        bool hasUsername,
        bool hasPassword)
    {
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateCredentialServer("TELNET", hasUsername, hasPassword));

        Assert.Equal(string.Empty, viewModel.AuthSummary);
    }

    [Theory]
    [InlineData(false, "No saved credentials")]
    [InlineData(true, "Password")]
    public void AuthSummary_Vnc_ReportsEveryConfiguredCredentialCombination(
        bool hasPassword,
        string expected)
    {
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateCredentialServer("VNC", hasUsername: false, hasPassword));

        Assert.Equal(expected, viewModel.AuthSummary);
    }

    [Theory]
    [InlineData("CITRIX")]
    [InlineData("LOCAL")]
    [InlineData("TOOL:PING")]
    [InlineData("UNKNOWN")]
    public void AuthSummary_TypeWithoutConfiguredCredentialBadge_RemainsEmpty(string connectionType)
    {
        ServerProfileDto dto = CreateCredentialServer(connectionType, hasUsername: true, hasPassword: true);
        dto.RdpUsername = "rdp-user";
        dto.RdpPasswordEncrypted = "rdp-password";
        dto.SshUsername = "ssh-user";
        dto.SshKeyPath = @"C:\Keys\configured.ppk";
        dto.SshPasswordEncrypted = "ssh-password";
        dto.WinRmUsername = "winrm-user";
        dto.WinRmPasswordEncrypted = "winrm-password";
        dto.FtpUsername = "ftp-user";
        dto.FtpPasswordEncrypted = "ftp-password";
        dto.TelnetUsername = "telnet-user";
        dto.TelnetPasswordEncrypted = "telnet-password";
        dto.VncPassword = "vnc-password";

        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(dto);

        Assert.Equal(string.Empty, viewModel.AuthSummary);
    }

    [Fact]
    public async Task AccessibleName_ContainsDisplayNameProtocolAndEffectiveState()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateSshServer(),
            connectionState: "Disconnected",
            localizer: localizer);

        viewModel.HealthState = new HealthState(
            HealthStatus.Up,
            DateTime.MinValue,
            12,
            null);

        Assert.Contains("Production", viewModel.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("protocol SSH", viewModel.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("Reachable", viewModel.AccessibleName, StringComparison.Ordinal);

        viewModel.ConnectionState = "Connected";

        Assert.Contains("Connected", viewModel.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("Reachable", viewModel.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessibleName_WithFrenchLocalizer_UsesLocalizedCompositionAndHealth()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateSshServer(),
            localizer: localizer);

        viewModel.HealthState = new HealthState(
            HealthStatus.Down,
            DateTime.MinValue,
            null,
            "timeout");

        Assert.Contains("Production", viewModel.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("protocole SSH", viewModel.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("état Injoignable", viewModel.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("Délai de connexion dépassé", viewModel.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessibleName_RaisesPropertyChangedForAllInputs()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateSshServer(),
            localizer: localizer);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        AssertAccessibleNameRaised(() => viewModel.DisplayName = "Renamed");
        AssertAccessibleNameRaised(() => viewModel.ConnectionType = "SFTP");
        AssertAccessibleNameRaised(() => viewModel.HealthState = new HealthState(
            HealthStatus.Up,
            DateTime.MinValue,
            8,
            null));
        AssertAccessibleNameRaised(() => viewModel.ConnectionState = "Connected");

        void AssertAccessibleNameRaised(Action mutation)
        {
            changed.Clear();
            mutation();
            Assert.Contains(nameof(ServerItemViewModel.AccessibleName), changed);
        }
    }

    [Fact]
    public async Task StatusTooltip_DescribesWhateverTheDotIsColouredFor()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateSshServer(),
            connectionState: "Disconnected",
            localizer: localizer);

        viewModel.HealthState = new HealthState(
            HealthStatus.Up,
            DateTime.MinValue,
            12,
            null);

        // Idle row: the dot falls back to reachability, so the tooltip does too.
        Assert.False(viewModel.StatusShowsConnectionState);
        Assert.Equal(viewModel.HealthTooltipText, viewModel.StatusTooltipText);
        Assert.Contains("Reachable", viewModel.StatusTooltipText, StringComparison.Ordinal);

        // Live row: the dot is coloured from the session state. The tooltip used to keep saying
        // "Reachable" here, next to a dot that no longer meant that.
        viewModel.ConnectionState = "Connected";

        Assert.True(viewModel.StatusShowsConnectionState);
        Assert.Equal(viewModel.ConnectionStateTooltip, viewModel.StatusTooltipText);
        Assert.DoesNotContain("Reachable", viewModel.StatusTooltipText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusTooltip_StillReportsHealthWhileTheHostIsUnreachableAndIdle()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateSshServer(),
            connectionState: "Disconnected",
            localizer: localizer);

        viewModel.HealthState = new HealthState(
            HealthStatus.Down,
            DateTime.MinValue,
            null,
            "timeout");

        // Guards the guard: a tooltip hard-wired to the state text would pass the test above by
        // never mentioning health at all.
        Assert.Equal(viewModel.HealthTooltipText, viewModel.StatusTooltipText);
        Assert.NotEqual(viewModel.ConnectionStateTooltip, viewModel.StatusTooltipText);
    }

    /// <summary>
    /// The dot, its tooltip and the spoken name have to take the same branch.
    /// </summary>
    /// <remarks>
    /// They render it differently on purpose - the tooltip carries the longer explanation, the
    /// spoken name the short one - so this pins the decision rather than the text. Every state that
    /// reaches a row is covered, including the transitional ones, because those are exactly where
    /// the tooltip and the name used to disagree.
    /// </remarks>
    [Theory]
    [InlineData("Disconnected", false)]
    [InlineData("Connected", true)]
    [InlineData("Error", true)]
    [InlineData("Disconnecting", true)]
    [InlineData("LaunchedExternalClient", true)]
    [InlineData("RemoteSessionHandedOff", true)]
    [InlineData("", false)]
    public async Task StatusTooltip_TakesTheSameBranchAsTheSpokenName(string state, bool followsState)
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateSshServer(),
            connectionState: state,
            localizer: localizer);

        viewModel.HealthState = new HealthState(
            HealthStatus.Up,
            DateTime.MinValue,
            12,
            null);

        Assert.Equal(followsState, viewModel.StatusShowsConnectionState);

        if (followsState)
        {
            Assert.Equal(viewModel.ConnectionStateTooltip, viewModel.StatusTooltipText);
            Assert.DoesNotContain(
                viewModel.HealthTooltipText,
                viewModel.AccessibleName,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(viewModel.HealthTooltipText, viewModel.StatusTooltipText);
            Assert.Contains(
                viewModel.HealthTooltipText,
                viewModel.AccessibleName,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StatusTooltip_RaisesPropertyChangedForEveryInputThatMovesIt()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateSshServer(),
            localizer: localizer);
        List<string?> changed = [];
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        AssertStatusTooltipRaised(() => viewModel.ConnectionState = "Connected");
        AssertStatusTooltipRaised(() => viewModel.HealthState = new HealthState(
            HealthStatus.Up,
            DateTime.MinValue,
            8,
            null));

        void AssertStatusTooltipRaised(Action mutation)
        {
            changed.Clear();
            mutation();
            Assert.Contains(nameof(ServerItemViewModel.StatusTooltipText), changed);
        }
    }

    [Fact]
    public void ServerTreeNode_BindsTheStatusDotTooltipToTheEffectiveStatus()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Heimdall.App",
            "MainWindow.xaml"));

        Assert.Contains(
            "ToolTip=\"{Binding StatusTooltipText}\"",
            xaml,
            StringComparison.Ordinal);

        // The dot must not go back to describing health while being coloured from the state.
        Assert.DoesNotContain(
            "ToolTip=\"{Binding HealthTooltipText}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ServerTreeNode_BindsAccessibleNameToAutomationName()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Heimdall.App",
            "MainWindow.xaml"));

        Assert.Contains(
            "AutomationProperties.Name=\"{Binding AccessibleName}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowLocaleRefresh_RefreshesServerTreeItems()
    {
        string repositoryRoot = FindRepositoryRoot();
        string code = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Heimdall.App",
            "MainWindow.xaml.cs"));

        Assert.Contains(
            "vm.ServerList.RefreshLocalizedState();",
            code,
            StringComparison.Ordinal);
    }

    private static ServerProfileDto CreateSshServer() => new()
    {
        Id = "ssh-1",
        DisplayName = "Production",
        ConnectionType = "SSH",
        RemoteServer = "prod.example.test",
        SshUsername = "operator",
        SshKeyPath = @"C:\Keys\production.ppk",
        SshPasswordEncrypted = "encrypted",
        SshAgentForwarding = true
    };

    private static ServerProfileDto CreateCredentialServer(
        string connectionType,
        bool hasUsername,
        bool hasPassword)
    {
        var dto = new ServerProfileDto
        {
            Id = $"auth-{connectionType.ToLowerInvariant()}",
            DisplayName = connectionType,
            ConnectionType = connectionType,
            RemoteServer = "host.example.test"
        };

        switch (connectionType)
        {
            case "SSH":
            case "SFTP":
                dto.SshUsername = hasUsername ? "operator" : null;
                dto.SshPasswordEncrypted = hasPassword ? "encrypted" : null;
                break;

            case "RDP":
                dto.RdpUsername = hasUsername ? "operator" : null;
                dto.RdpPasswordEncrypted = hasPassword ? "encrypted" : null;
                break;

            case "WINRM":
                dto.WinRmUsername = hasUsername ? "operator" : null;
                dto.WinRmPasswordEncrypted = hasPassword ? "encrypted" : null;
                break;

            case "FTP":
                dto.FtpUsername = hasUsername ? "operator" : null;
                dto.FtpPasswordEncrypted = hasPassword ? "encrypted" : null;
                break;

            case "TELNET":
                dto.TelnetUsername = hasUsername ? "operator" : null;
                dto.TelnetPasswordEncrypted = hasPassword ? "encrypted" : null;
                break;

            case "VNC":
                dto.VncPassword = hasPassword ? "encrypted" : null;
                break;
        }

        return dto;
    }

    // --- Sidebar dot / accessible name coherence ------------------------------------------------
    // The dot and the spoken name must report the same thing. They used to diverge: the dot falls
    // back to the health palette only on its default branch, while the accessible name keyed off
    // IsActiveSession - a strictly narrower set - so an Error or any transitional state coloured
    // the dot from the state and read out the reachability verdict instead.

    [Theory]
    [MemberData(nameof(AllConnectionStates))]
    public void OverridesHealth_MatchesTheBrushPriorityOfTheSidebarDot(ConnectionState state)
    {
        // Unknown health is what makes the two branches observable: it is the ONLY input for which
        // the converter answers TextDisabledBrush, and it answers it only from the health branch.
        // Every state branch resolves Success/Info/Warning/Error, and the type fallback resolves
        // Border/Info/Success/Warning - so the key alone tells which branch ran.
        string? requestedKey = null;
        ServerStatusToColorConverter converter = new(key =>
        {
            requestedKey = key;
            return null;
        });

        converter.Convert(
            ["SSH", state.ToString(), HealthState.Initial],
            typeof(Brush),
            null!,
            CultureInfo.InvariantCulture);

        bool dotUsedHealth = requestedKey == "TextDisabledBrush";

        Assert.Equal(!dotUsedHealth, ConnectionStateSets.StateOverridesHealth(state.ToString()));
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Initializing")]
    [InlineData("EstablishingTunnel")]
    [InlineData("LaunchingSsh")]
    [InlineData("Disconnecting")]
    public async Task AccessibleName_StateThatColoursTheDot_ReportsTheStateNotTheHealth(string state)
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateSshServer(),
            localizer: localizer);
        viewModel.HealthState = new HealthState(HealthStatus.Up, DateTime.UtcNow, 12, null);
        viewModel.ConnectionState = state;

        // None of these is an "active session", which is precisely why the old rule got them wrong.
        Assert.False(viewModel.IsActiveSession);
        Assert.NotEqual(viewModel.HealthTooltipText, viewModel.ConnectionStateDisplayName);
        Assert.Contains(viewModel.ConnectionStateDisplayName, viewModel.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain(viewModel.HealthTooltipText, viewModel.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessibleName_DisconnectedWhereTheDotReadsHealth_ReportsTheHealth()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateSshServer(),
            localizer: localizer);
        viewModel.HealthState = new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "refused");
        viewModel.ConnectionState = "Disconnected";

        Assert.NotEqual(viewModel.HealthTooltipText, viewModel.ConnectionStateDisplayName);
        Assert.Contains(viewModel.HealthTooltipText, viewModel.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain(viewModel.ConnectionStateDisplayName, viewModel.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessibleName_HealthChangeWhileStateOverrides_DoesNotChangeTheSpokenStatus()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ServerItemViewModel viewModel = ServerItemViewModel.FromDto(
            CreateSshServer(),
            localizer: localizer);
        viewModel.ConnectionState = "Error";
        viewModel.HealthState = new HealthState(HealthStatus.Up, DateTime.UtcNow, 12, null);
        string before = viewModel.AccessibleName;

        viewModel.HealthState = new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "timeout");

        // The dot stays red for Error whatever the probe says; the spoken name must not drift off it.
        Assert.Equal(before, viewModel.AccessibleName);
    }

    public static TheoryData<ConnectionState> AllConnectionStates()
    {
        TheoryData<ConnectionState> data = [];
        foreach (ConnectionState state in Enum.GetValues<ConnectionState>())
        {
            data.Add(state);
        }

        return data;
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
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
