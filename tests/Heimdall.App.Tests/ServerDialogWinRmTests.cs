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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogWinRmTests
{
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

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
