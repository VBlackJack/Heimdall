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
using System.Xml.Linq;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogGatewayEligibilityTests
{
    [Fact]
    public void Dialog_GatewayFieldHidden_ForIneligibleProtocol()
    {
        var viewModel = new ServerDialogViewModel
        {
            ConnectionType = "TELNET",
            DirectConnection = false,
            SelectedGatewayId = "gateway-01"
        };

        Assert.False(viewModel.SupportsGateway);
        Assert.False(viewModel.CanSelectGateway);
        Assert.False(viewModel.UsesGateway);

        XDocument document = LoadServerDialogXaml();
        XElement style = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Style"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Key"
                    && attribute.Value == "NetworkSectionStyle"));
        Assert.Contains(
            style.Elements(),
            element => IsVisibilitySetter(element, "Collapsed"));
        XElement trigger = Assert.Single(
            style.Descendants(),
            element => element.Name.LocalName == "DataTrigger");
        Assert.Equal("{Binding SupportsGateway}", trigger.Attribute("Binding")?.Value);
        Assert.Equal("True", trigger.Attribute("Value")?.Value);
        Assert.Contains(
            trigger.Elements(),
            element => IsVisibilitySetter(element, "Visible"));
    }

    [Theory]
    [InlineData("TELNET")]
    [InlineData("VNC")]
    [InlineData("FTP")]
    [InlineData("CITRIX")]
    [InlineData("LOCAL")]
    [InlineData("TOOL:PING")]
    public void ToDto_DoesNotPersistGatewayState_ForIneligibleProtocol(string connectionType)
    {
        var viewModel = new ServerDialogViewModel
        {
            ConnectionType = connectionType,
            DirectConnection = true,
            SelectedGatewayId = "gateway-01"
        };

        ServerProfileDto dto = viewModel.ToDto();

        Assert.Null(dto.SshGatewayId);
        Assert.False(dto.UseDirectConnection);
    }

    // Lot 2 of BL-0094. Zero gateway used to be an empty dropdown, enabled, with no text
    // anywhere and no way to fill it. HasNoGateway is what the empty state hangs on, and it
    // must stay silent for the protocols that never tunnel.
    [Theory]
    [InlineData("SSH", true)]
    [InlineData("RDP", true)]
    [InlineData("SFTP", true)]
    [InlineData("WINRM", true)]
    [InlineData("TELNET", false)]
    [InlineData("VNC", false)]
    [InlineData("FTP", false)]
    public void HasNoGateway_OnlyWhenTheProtocolTunnels(string connectionType, bool expected)
    {
        ServerDialogViewModel viewModel = new() { ConnectionType = connectionType };

        Assert.Empty(viewModel.AvailableGateways);
        Assert.Equal(expected, viewModel.HasNoGateway);
    }

    [Fact]
    public void HasNoGateway_FalseOnceAGatewayIsAvailable()
    {
        ServerDialogViewModel viewModel = new() { ConnectionType = "SSH" };
        viewModel.AvailableGateways.Add(new GatewayOption("gw-1", "Bastion"));

        Assert.False(viewModel.HasNoGateway);
    }

    // The button must be enabled exactly when the dropdown beside it is, otherwise it
    // becomes the next surface that offers something it cannot do.
    [Theory]
    [InlineData("SSH", false, true)]
    [InlineData("SSH", true, false)]
    [InlineData("TELNET", false, false)]
    public void CanCreateGateway_FollowsCanSelectGateway(
        string connectionType,
        bool directConnection,
        bool expected)
    {
        ServerDialogViewModel viewModel = new()
        {
            ConnectionType = connectionType,
            DirectConnection = directConnection,
            CreateGatewayRequested = () => Task.FromResult<GatewayOption?>(null)
        };

        Assert.Equal(viewModel.CanSelectGateway, viewModel.CanCreateGateway);
        Assert.Equal(expected, viewModel.CanCreateGateway);
    }

    [Fact]
    public void CanCreateGateway_FalseWhenTheShellSuppliedNoCreationPath()
    {
        ServerDialogViewModel viewModel = new() { ConnectionType = "SSH" };

        Assert.True(viewModel.CanSelectGateway);
        Assert.False(viewModel.CanCreateGateway);
    }

    [Fact]
    public async Task CreateGatewayCommand_AddsTheGatewayAndSelectsIt()
    {
        GatewayOption created = new("gw-new", "Bastion (bastion.example.test)");
        ServerDialogViewModel viewModel = new()
        {
            ConnectionType = "SSH",
            CreateGatewayRequested = () => Task.FromResult<GatewayOption?>(created)
        };

        await viewModel.CreateGatewayCommand.ExecuteAsync(null);

        Assert.Same(created, Assert.Single(viewModel.AvailableGateways));
        Assert.Equal("gw-new", viewModel.SelectedGatewayId);
        Assert.True(viewModel.UsesGateway);
        Assert.False(viewModel.HasNoGateway);
    }

    [Fact]
    public async Task CreateGatewayCommand_CancelledDialog_ChangesNothing()
    {
        ServerDialogViewModel viewModel = new()
        {
            ConnectionType = "SSH",
            CreateGatewayRequested = () => Task.FromResult<GatewayOption?>(null)
        };

        await viewModel.CreateGatewayCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.AvailableGateways);
        Assert.Equal("", viewModel.SelectedGatewayId);
    }

    // Lot 3 of BL-0094. One message for two disjoint causes, and it named the wrong one in
    // both directions: the checkbox has nothing to do with the protocol, and gateways were
    // never SSH-only. This is what a screen reader reads out, so it is the whole feature.
    [Fact]
    public async Task GatewayComboHelpText_NamesTheCauseThatActuallyDisabledTheDropdown()
    {
        LocalizationManager localizer = await CreateEnglishLocalizerAsync();
        ServerDialogViewModel enabled = new() { Localizer = localizer, ConnectionType = "SSH", DirectConnection = false };
        ServerDialogViewModel direct = new() { Localizer = localizer, ConnectionType = "SSH", DirectConnection = true };
        ServerDialogViewModel unsupported = new() { Localizer = localizer, ConnectionType = "TELNET", DirectConnection = false };

        Assert.Equal("", enabled.GatewayComboHelpText);
        Assert.Equal(
            localizer["ServerDialogGatewayDisabledDirectHint"],
            direct.GatewayComboHelpText);
        Assert.Equal(
            localizer["ServerDialogGatewayDisabledProtocolHint"],
            unsupported.GatewayComboHelpText);
        Assert.NotEqual(direct.GatewayComboHelpText, unsupported.GatewayComboHelpText);
    }

    // The wording is the deliverable here, so it is asserted rather than left to a key
    // lookup that would stay green over a message saying the wrong thing again.
    [Fact]
    public async Task GatewayDisabledHints_SayWhatIsTrue()
    {
        LocalizationManager localizer = await CreateEnglishLocalizerAsync();
        string protocolHint = localizer["ServerDialogGatewayDisabledProtocolHint"];
        string directHint = localizer["ServerDialogGatewayDisabledDirectHint"];

        foreach (string supported in new[] { "SSH", "SFTP", "RDP", "WinRM" })
        {
            Assert.Contains(supported, protocolHint, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("protocol", directHint, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("ServerDialogGatewayDisabledProtocolHint", protocolHint);
        Assert.NotEqual("ServerDialogGatewayDisabledDirectHint", directHint);
    }

    private static async Task<LocalizationManager> CreateEnglishLocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    // The XAML half of the same junction: a command nobody binds is a command nobody runs.
    [Fact]
    public void Dialog_NetworkTab_BindsTheAddButtonAndTheEmptyState()
    {
        XDocument document = LoadServerDialogXaml();

        XElement addButton = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value == "DlgSrv_AddGatewayBtn"));
        Assert.Equal("{Binding CreateGatewayCommand}", addButton.Attribute("Command")?.Value);

        Assert.Contains(
            document.Descendants(),
            element => element.Name.LocalName == "StackPanel"
                && element.Attribute("Visibility")?.Value
                    == "{Binding HasNoGateway, Converter={StaticResource BoolToVisibilityConverter}}");
    }

    private static bool IsVisibilitySetter(XElement element, string value)
    {
        return element.Name.LocalName == "Setter"
               && element.Attribute("Property")?.Value == "Visibility"
               && element.Attribute("Value")?.Value == value;
    }

    private static XDocument LoadServerDialogXaml()
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string path = Path.Combine(
            repoRoot,
            "src",
            "Heimdall.App",
            "Views",
            "Dialogs",
            "ServerDialog.xaml");

        Assert.True(File.Exists(path), $"Server dialog XAML not found: {path}");
        return XDocument.Load(path);
    }
}
