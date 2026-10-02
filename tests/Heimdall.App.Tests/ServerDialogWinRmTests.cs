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
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogWinRmTests
{
    [Fact]
    public void WinRmCredentialsCard_UsesCaseInsensitiveViewModelVisibility()
    {
        XDocument document = LoadServerDialogXaml();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement title = Assert.Single(document.Descendants(), element =>
            string.Equals(
                element.Attribute(x + "Name")?.Value,
                "DlgSrv_BasicWinRmCredentialsTitle",
                StringComparison.Ordinal));
        XElement card = title.Ancestors().First(element => element.Name.LocalName == "Border");

        Assert.Equal(
            "{Binding IsWinRmConnection, Converter={StaticResource BoolToVisibilityConverter}}",
            card.Attribute("Visibility")?.Value);
        Assert.DoesNotContain(
            card.Descendants(),
            element => element.Name.LocalName == "DataTrigger"
                && string.Equals(element.Attribute("Value")?.Value, "WINRM", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectProtocol_WinRm_DefaultsToHttpPort()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel();

        vm.SelectProtocolCommand.Execute("WINRM");

        Assert.Equal("WINRM", vm.ConnectionType);
        Assert.Equal("WinRM", vm.ConnectionTypeDisplayName);
        Assert.Equal(DefaultPorts.WinRmHttp, vm.WinRmPort);
        Assert.Equal(DefaultPorts.WinRmHttp, vm.EndpointPort);
    }

    private static XDocument LoadServerDialogXaml()
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "Heimdall.App",
            "Views",
            "Dialogs",
            "ServerDialog.xaml"));
        return XDocument.Load(path);
    }

    [Fact]
    public void WinRmUseSsl_TogglesDefaultPorts()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel { ConnectionType = "WINRM" };

        vm.WinRmUseSsl = true;

        Assert.Equal(DefaultPorts.WinRmHttps, vm.WinRmPort);
        Assert.Equal(DefaultPorts.WinRmHttps, vm.EndpointPort);

        vm.WinRmUseSsl = false;

        Assert.Equal(DefaultPorts.WinRmHttp, vm.WinRmPort);
        Assert.Equal(DefaultPorts.WinRmHttp, vm.EndpointPort);
    }

    [Fact]
    public void ReselectProtocol_WinRmSsl_RestoresHttpsDefaultPort()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel();
        vm.SelectProtocolCommand.Execute("WINRM");
        vm.WinRmUseSsl = true;
        vm.BackToProtocolSelectorCommand.Execute(null);
        vm.SelectProtocolCommand.Execute("SSH");
        vm.BackToProtocolSelectorCommand.Execute(null);

        vm.SelectProtocolCommand.Execute("WINRM");

        Assert.True(vm.WinRmUseSsl);
        Assert.Equal(DefaultPorts.WinRmHttps, vm.WinRmPort);
        Assert.Equal(DefaultPorts.WinRmHttps, vm.EndpointPort);
    }

    [Fact]
    public void WinRmUseSsl_PreservesCustomPort()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel
        {
            ConnectionType = "WINRM",
            WinRmPort = 12345
        };

        vm.WinRmUseSsl = true;
        Assert.Equal(12345, vm.WinRmPort);

        vm.WinRmUseSsl = false;
        Assert.Equal(12345, vm.WinRmPort);
    }

    [Fact]
    public void WinRmUseSsl_WithGateway_IsDisabledAndCleared()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel { ConnectionType = "WINRM" };
        vm.WinRmUseSsl = true;

        vm.SelectedGatewayId = "gateway-01";

        Assert.True(vm.UsesGateway);
        Assert.False(vm.CanUseWinRmSsl);
        Assert.False(vm.WinRmUseSsl);
        Assert.Equal(DefaultPorts.WinRmHttp, vm.WinRmPort);
        Assert.Equal(DefaultPorts.WinRmHttp, vm.EndpointPort);
    }

    [Fact]
    public void WinRmUseSsl_CannotBeEnabledWhileGatewayIsSelected()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel
        {
            ConnectionType = "WINRM",
            SelectedGatewayId = "gateway-01"
        };

        vm.WinRmUseSsl = true;

        Assert.False(vm.CanUseWinRmSsl);
        Assert.False(vm.WinRmUseSsl);
        Assert.Equal(DefaultPorts.WinRmHttp, vm.WinRmPort);
    }

    [Fact]
    public void WinRmSkipCertificateCheck_ClearsWhenSslIsDisabled()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel { ConnectionType = "WINRM" };
        vm.WinRmUseSsl = true;
        vm.WinRmSkipCertificateCheck = true;

        vm.WinRmUseSsl = false;

        Assert.False(vm.WinRmSkipCertificateCheck);
        Assert.False(vm.CanSkipWinRmCertificate);
    }

    [Fact]
    public void FromDto_WinRmGatewayWithSsl_ClearsUnsupportedSsl()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            ConnectionType = "WINRM",
            WinRmPort = DefaultPorts.WinRmHttps,
            WinRmUseSsl = true,
            WinRmSkipCertificateCheck = true,
            SshGatewayId = "gateway-01"
        });

        Assert.True(vm.UsesGateway);
        Assert.False(vm.CanUseWinRmSsl);
        Assert.False(vm.WinRmUseSsl);
        Assert.False(vm.WinRmSkipCertificateCheck);
        Assert.Equal(DefaultPorts.WinRmHttp, vm.WinRmPort);
    }

    [Fact]
    public void FromDto_WinRmWithoutSsl_ClearsLatentCertificateSkip()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            ConnectionType = "WINRM",
            WinRmUseSsl = false,
            WinRmSkipCertificateCheck = true
        });

        Assert.False(vm.WinRmUseSsl);
        Assert.False(vm.WinRmSkipCertificateCheck);
    }

    [Fact]
    public void ToDto_WinRmWithoutSsl_DoesNotPersistLatentCertificateSkip()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel
        {
            ConnectionType = "WINRM",
            WinRmUseSsl = false,
            WinRmSkipCertificateCheck = true
        };

        ServerProfileDto dto = vm.ToDto();

        Assert.False(dto.WinRmUseSsl);
        Assert.False(dto.WinRmSkipCertificateCheck);
    }

    [Fact]
    public async Task WinRmDisplayText_UsesFriendlyProtocolAndSessionNames()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel
        {
            ConnectionType = "WINRM",
            Localizer = await CreateLocalizerAsync("en")
        };

        Assert.Equal("WinRM", vm.ConnectionTypeDisplayName);
        Assert.Equal("WinRM session", vm.SessionKindLabel);
        Assert.Equal("WinRM session", vm.GatewayToServerLabel);
    }

    [Fact]
    public void ToDto_FromDto_PreservesWinRmFields()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel
        {
            DisplayName = "WinRM",
            RemoteServer = "server01.contoso.test",
            ConnectionType = "WINRM",
            WinRmPort = DefaultPorts.WinRmHttps,
            WinRmUseSsl = true,
            WinRmSkipCertificateCheck = true,
            WinRmIdentityMode = WinRmIdentityMode.Credential,
            WinRmUsername = @"CONTOSO\admin",
            ExistingWinRmPasswordEncrypted = "encrypted-password"
        };

        ServerProfileDto dto = vm.ToDto();

        Assert.Equal("WINRM", dto.ConnectionType);
        Assert.Equal(DefaultPorts.WinRmHttps, dto.WinRmPort);
        Assert.True(dto.WinRmUseSsl);
        Assert.True(dto.WinRmSkipCertificateCheck);
        Assert.Equal(WinRmIdentityMode.Credential, dto.WinRmIdentityMode);
        Assert.Equal(@"CONTOSO\admin", dto.WinRmUsername);
        Assert.Equal("encrypted-password", dto.WinRmPasswordEncrypted);

        ServerDialogViewModel roundTripped = ServerDialogViewModel.FromDto(dto);

        Assert.Equal(DefaultPorts.WinRmHttps, roundTripped.WinRmPort);
        Assert.True(roundTripped.WinRmUseSsl);
        Assert.True(roundTripped.WinRmSkipCertificateCheck);
        Assert.True(roundTripped.CanSkipWinRmCertificate);
        Assert.True(roundTripped.IsWinRmCredentialIdentity);
        Assert.Equal(@"CONTOSO\admin", roundTripped.WinRmUsername);
        Assert.Equal("encrypted-password", roundTripped.ExistingWinRmPasswordEncrypted);
    }

    [Fact]
    public void ToDto_FromDto_WinRmWithGateway_PersistsGatewayRouting()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel
        {
            DisplayName = "WinRM via gateway",
            RemoteServer = "server01.contoso.test",
            ConnectionType = "WINRM",
            DirectConnection = false,
            SelectedGatewayId = "gateway-01"
        };

        Assert.True(vm.CanSelectGateway);
        Assert.True(vm.UsesGateway);

        ServerProfileDto dto = vm.ToDto();
        Assert.Equal("gateway-01", dto.SshGatewayId);
        Assert.False(dto.UseDirectConnection);
        Assert.Equal(DefaultPorts.WinRmTunnel, dto.LocalPort);

        ServerDialogViewModel roundTripped = ServerDialogViewModel.FromDto(dto);
        Assert.Equal("gateway-01", roundTripped.SelectedGatewayId);
        Assert.False(roundTripped.DirectConnection);
        Assert.True(roundTripped.UsesGateway);
        Assert.Equal(DefaultPorts.WinRmTunnel, roundTripped.LocalPort);
    }

    [Fact]
    public void ToDto_DirectConnection_DropsResidualGatewayId()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel
        {
            ConnectionType = "WINRM",
            DirectConnection = true,
            SelectedGatewayId = "gateway-01"
        };

        ServerProfileDto dto = vm.ToDto();

        Assert.True(dto.UseDirectConnection);
        Assert.Null(dto.SshGatewayId);
    }

    [Fact]
    public void FromDto_WinRmDirectWithResidualGateway_PreservesSsl()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            ConnectionType = "WINRM",
            WinRmPort = DefaultPorts.WinRmHttps,
            WinRmUseSsl = true,
            WinRmSkipCertificateCheck = true,
            SshGatewayId = "gateway-01",
            UseDirectConnection = true
        });

        Assert.True(vm.DirectConnection);
        Assert.False(vm.UsesGateway);
        Assert.True(vm.CanUseWinRmSsl);
        Assert.True(vm.WinRmUseSsl);
        Assert.True(vm.WinRmSkipCertificateCheck);
        Assert.Equal(DefaultPorts.WinRmHttps, vm.WinRmPort);
    }

    [Fact]
    public void ToDto_AfterDirectResidualGatewayRoundTrip_DropsGatewayAndPreservesSsl()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            ConnectionType = "WINRM",
            WinRmPort = DefaultPorts.WinRmHttps,
            WinRmUseSsl = true,
            WinRmSkipCertificateCheck = true,
            SshGatewayId = "gateway-01",
            UseDirectConnection = true
        });

        ServerProfileDto dto = vm.ToDto();

        Assert.True(dto.UseDirectConnection);
        Assert.Null(dto.SshGatewayId);
        Assert.True(dto.WinRmUseSsl);
        Assert.True(dto.WinRmSkipCertificateCheck);
        Assert.Equal(DefaultPorts.WinRmHttps, dto.WinRmPort);
    }

    [Fact]
    public void FromDto_WinRmMissingPort_UsesSslAwareDefault()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            ConnectionType = "WINRM",
            WinRmPort = 0,
            WinRmUseSsl = true
        });

        Assert.Equal(DefaultPorts.WinRmHttps, vm.WinRmPort);
    }

    // The dialog refuses the username the credential bootstrap would refuse at connect time,
    // by the same predicate, so the user learns it at Save rather than from a failed session.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad user")]
    [InlineData("user;Remove-Item")]
    [InlineData("CONTOSO\\user\\extra")]
    public void Validate_CredentialModeWithUsernameTheBootstrapRefuses_NamesTheField(string username)
    {
        ServerDialogViewModel vm = CredentialProfile(username);

        vm.ValidateCommand.Execute(null);

        Assert.NotNull(vm.ValidationError);
        Assert.Equal("WinRmUsername", vm.FirstInvalidField);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("CONTOSO\\operator")]
    [InlineData("operator@contoso.com")]
    public void Validate_CredentialModeWithUsernameTheBootstrapAccepts_PassesTheField(string username)
    {
        ServerDialogViewModel vm = CredentialProfile(username);

        vm.ValidateCommand.Execute(null);

        Assert.Null(vm.ValidationError);
    }

    [Fact]
    public void Validate_CurrentUserMode_IgnoresTheUsernameField()
    {
        ServerDialogViewModel vm = CredentialProfile("bad user");
        vm.WinRmIdentityMode = WinRmIdentityMode.CurrentUser;

        vm.ValidateCommand.Execute(null);

        Assert.Null(vm.ValidationError);
    }

    [Fact]
    public async Task Validate_InvalidUsername_ShowsTheLocalizedMessageAndClearsWhenFixed()
    {
        ServerDialogViewModel vm = CredentialProfile("bad user");
        vm.Localizer = await CreateLocalizerAsync("en");

        vm.ValidateCommand.Execute(null);
        Assert.Equal(vm.Localizer["ValidationInlineWinRmUserInvalid"], vm.ValidationError);
        Assert.NotEqual("ValidationInlineWinRmUserInvalid", vm.ValidationError);

        vm.WinRmUsername = "operator";

        Assert.Null(vm.ValidationError);
    }

    // Switching back to the current Windows identity must not keep a stored password nobody
    // can see or clear any more: the credential card, and its Clear button, are hidden then.
    [Fact]
    public void ToDto_CurrentUserMode_DropsTheStoredWinRmPassword()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            DisplayName = "WinRM",
            RemoteServer = "server01.contoso.test",
            ConnectionType = "WINRM",
            WinRmIdentityMode = WinRmIdentityMode.Credential,
            WinRmUsername = "operator",
            WinRmPasswordEncrypted = "encrypted-password"
        });

        vm.WinRmIdentityMode = WinRmIdentityMode.CurrentUser;
        ServerProfileDto dto = vm.ToDto();

        Assert.Equal(WinRmIdentityMode.CurrentUser, dto.WinRmIdentityMode);
        Assert.Null(dto.WinRmPasswordEncrypted);
    }

    [Fact]
    public void ToDto_BackToCredentialModeBeforeSaving_KeepsTheStoredWinRmPassword()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            DisplayName = "WinRM",
            RemoteServer = "server01.contoso.test",
            ConnectionType = "WINRM",
            WinRmIdentityMode = WinRmIdentityMode.Credential,
            WinRmUsername = "operator",
            WinRmPasswordEncrypted = "encrypted-password"
        });

        vm.WinRmIdentityMode = WinRmIdentityMode.CurrentUser;
        vm.WinRmIdentityMode = WinRmIdentityMode.Credential;
        ServerProfileDto dto = vm.ToDto();

        Assert.Equal("encrypted-password", dto.WinRmPasswordEncrypted);
    }

    [Fact]
    public void Validate_CredentialModeWithoutAnyPassword_NamesThePasswordField()
    {
        ServerDialogViewModel vm = CredentialProfile("operator");
        vm.WinRmPassword = "";

        vm.ValidateCommand.Execute(null);

        Assert.NotNull(vm.WinRmPasswordError);
        Assert.NotNull(vm.ValidationError);
        Assert.Equal("WinRmPassword", vm.FirstInvalidField);

        vm.WinRmPassword = "typed-later";

        Assert.Null(vm.WinRmPasswordError);
        Assert.Null(vm.ValidationError);
    }

    [Fact]
    public void Validate_CredentialModeWithStoredPasswordAndEmptyBox_KeepsTheStoredPassword()
    {
        ServerDialogViewModel vm = CredentialProfile("operator");
        vm.WinRmPassword = "";
        vm.ExistingWinRmPasswordEncrypted = "encrypted-password";

        vm.ValidateCommand.Execute(null);

        Assert.Null(vm.WinRmPasswordError);
        Assert.Null(vm.ValidationError);
    }

    [Fact]
    public void Validate_CurrentUserModeWithoutPassword_RaisesNoPasswordError()
    {
        ServerDialogViewModel vm = CredentialProfile("operator");
        vm.WinRmPassword = "";
        vm.WinRmIdentityMode = WinRmIdentityMode.CurrentUser;

        vm.ValidateCommand.Execute(null);

        Assert.Null(vm.WinRmPasswordError);
    }

    [Fact]
    public void GatewaySelected_WithSsl_ExplainsTheSwitchAndRestoresItWhenTheGatewayIsRemoved()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel { ConnectionType = "WINRM" };
        vm.WinRmUseSsl = true;
        vm.WinRmSkipCertificateCheck = true;
        Assert.False(vm.WinRmSslDisabledByGateway);

        vm.SelectedGatewayId = "gateway-01";

        Assert.False(vm.WinRmUseSsl);
        Assert.True(vm.WinRmSslDisabledByGateway);
        Assert.Equal(DefaultPorts.WinRmHttp, vm.WinRmPort);

        vm.SelectedGatewayId = "";

        Assert.False(vm.WinRmSslDisabledByGateway);
        Assert.True(vm.WinRmUseSsl);
        Assert.True(vm.WinRmSkipCertificateCheck);
        Assert.Equal(DefaultPorts.WinRmHttps, vm.WinRmPort);
    }

    [Fact]
    public async Task GatewaySelected_WithSsl_ShowsTheDedicatedNoticeInsteadOfTheGenericHint()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel { ConnectionType = "WINRM" };
        vm.Localizer = await CreateLocalizerAsync("en");
        vm.WinRmUseSsl = true;
        string genericGatewayHint = vm.Localizer["ServerDialogWinRmUseSslGatewayHint"];

        vm.SelectedGatewayId = "gateway-01";

        Assert.Equal(vm.Localizer["ServerDialogWinRmUseSslGatewayDisabledNotice"], vm.WinRmUseSslHelpText);
        Assert.NotEqual(genericGatewayHint, vm.WinRmUseSslHelpText);
    }

    [Fact]
    public void GatewaySelected_WithoutSsl_RaisesNoNotice()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel { ConnectionType = "WINRM" };

        vm.SelectedGatewayId = "gateway-01";

        Assert.False(vm.WinRmSslDisabledByGateway);
    }

    [Theory]
    [InlineData("10.0.0.5", false, false, true, true)]
    [InlineData("10.0.0.5", true, false, true, false)]
    [InlineData("server01", false, true, true, true)]
    [InlineData("server01", false, false, true, false)]
    [InlineData("server01.contoso.test", false, true, true, false)]
    [InlineData("10.0.0.5", false, false, false, false)]
    [InlineData("", false, true, true, false)]
    public void ShouldShowWinRmTrustedHostsHint_CoversTheOffDomainHttpCases(
        string host,
        bool useSsl,
        bool storedCredential,
        bool applicable,
        bool expected)
    {
        Assert.Equal(
            expected,
            ServerDialogViewModel.ShouldShowWinRmTrustedHostsHint(host, useSsl, storedCredential, applicable));
    }

    [Fact]
    public void SkipCertificateHint_SaysWhatEnablesTheBoxAndWarnsOnceActive()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel { ConnectionType = "WINRM" };
        string withoutSsl = vm.WinRmSkipCertificateHintText;
        Assert.False(vm.IsWinRmCertificateCheckSkipped);

        vm.WinRmUseSsl = true;
        Assert.NotEqual(withoutSsl, vm.WinRmSkipCertificateHintText);
        Assert.False(vm.IsWinRmCertificateCheckSkipped);

        vm.WinRmSkipCertificateCheck = true;
        Assert.True(vm.IsWinRmCertificateCheckSkipped);
    }

    /// <summary>
    /// Ticking or clearing the skip box repaints its warning at once.
    /// </summary>
    /// <remarks>
    /// The hint turns to the warning colour through a DataTrigger on
    /// <see cref="ServerDialogViewModel.IsWinRmCertificateCheckSkipped"/>, which re-reads the value
    /// only when it is notified. The neighbour above asserts the getter, which was right while the
    /// box itself announced nothing: the warning appeared only on a profile reopened with the box
    /// already ticked, or after an unrelated edit, and stayed after the box was cleared.
    /// </remarks>
    [Fact]
    public void SkipCertificateBox_NotifiesTheWarningEachWayItIsToggled()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel { ConnectionType = "WINRM", WinRmUseSsl = true };
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.WinRmSkipCertificateCheck = true;
        Assert.Contains(nameof(ServerDialogViewModel.IsWinRmCertificateCheckSkipped), raised);

        raised.Clear();
        vm.WinRmSkipCertificateCheck = false;
        Assert.Contains(nameof(ServerDialogViewModel.IsWinRmCertificateCheckSkipped), raised);
    }

    private static ServerDialogViewModel CredentialProfile(string username) =>
        new()
        {
            DisplayName = "WinRM",
            RemoteServer = "server01.contoso.test",
            ConnectionType = "WINRM",
            WinRmIdentityMode = WinRmIdentityMode.Credential,
            WinRmUsername = username,
            WinRmPassword = "test-password"
        };

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
