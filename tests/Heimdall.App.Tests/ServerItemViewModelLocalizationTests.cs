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
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.SessionHealth;

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
