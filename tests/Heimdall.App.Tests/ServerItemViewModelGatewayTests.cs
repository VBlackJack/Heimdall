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

namespace Heimdall.App.Tests;

public sealed class ServerItemViewModelGatewayTests
{
    [Fact]
    public void FromDto_WithoutGatewayId_HidesGatewayBadge()
    {
        ServerProfileDto dto = CreateServer(gatewayId: null);

        ServerItemViewModel vm = ServerItemViewModel.FromDto(dto, gatewayMap: new Dictionary<string, SshGatewayDto>());

        Assert.False(vm.IsGatewayBadgeVisible);
        Assert.False(vm.IsGatewayMissing);
        Assert.Equal("", vm.GatewayName);
        Assert.Equal("", vm.GatewayBadgeText);
        Assert.Equal("", vm.GatewayDetailText);
    }

    [Fact]
    public void FromDto_WithGatewayIdAndNoMap_HidesGatewayBadgeWithoutFlaggingMissing()
    {
        ServerProfileDto dto = CreateServer("gw-1");

        ServerItemViewModel vm = ServerItemViewModel.FromDto(dto, gatewayMap: null);

        Assert.False(vm.IsGatewayBadgeVisible);
        Assert.False(vm.IsGatewayMissing);
        Assert.Equal("", vm.GatewayName);
        Assert.Equal("", vm.GatewayBadgeText);
        Assert.Equal("", vm.GatewayDetailText);
    }

    [Fact]
    public void FromDto_WithResolvedGateway_ShowsViaBadge()
    {
        ServerProfileDto dto = CreateServer("gw-1");
        Dictionary<string, SshGatewayDto> gatewayMap = new(StringComparer.Ordinal)
        {
            ["gw-1"] = new SshGatewayDto { Id = "gw-1", Name = "Bastion" }
        };

        ServerItemViewModel vm = ServerItemViewModel.FromDto(dto, gatewayMap: gatewayMap);

        Assert.True(vm.IsGatewayBadgeVisible);
        Assert.False(vm.IsGatewayMissing);
        Assert.Equal("Bastion", vm.GatewayName);
        Assert.Equal("via Bastion", vm.GatewayBadgeText);
        Assert.Equal("Routes through SSH gateway Bastion.", vm.GatewayBadgeTooltip);
        Assert.Equal("Bastion", vm.GatewayDetailText);
    }

    [Fact]
    public void TelnetProfileWithGatewayId_ShowsNoBadge()
    {
        ServerProfileDto dto = CreateServer("gw-1");
        dto.ConnectionType = "TELNET";
        Dictionary<string, SshGatewayDto> gatewayMap = new(StringComparer.Ordinal)
        {
            ["gw-1"] = new SshGatewayDto { Id = "gw-1", Name = "Bastion" }
        };

        ServerItemViewModel vm = ServerItemViewModel.FromDto(dto, gatewayMap: gatewayMap);

        Assert.False(vm.IsGatewayBadgeVisible);
        Assert.False(vm.IsGatewayMissing);
        Assert.Equal("", vm.GatewayName);
        Assert.Equal("", vm.GatewayBadgeText);
        Assert.Equal("", vm.GatewayDetailText);
    }

    [Fact]
    public void FromDto_WithMissingGateway_ShowsWarningBadge()
    {
        ServerProfileDto dto = CreateServer("gw-missing");

        ServerItemViewModel vm = ServerItemViewModel.FromDto(dto, gatewayMap: new Dictionary<string, SshGatewayDto>());

        Assert.True(vm.IsGatewayBadgeVisible);
        Assert.True(vm.IsGatewayMissing);
        Assert.Equal("", vm.GatewayName);
        Assert.Equal("gateway missing", vm.GatewayBadgeText);
        Assert.Equal("This session references missing SSH gateway id gw-missing.", vm.GatewayBadgeTooltip);
        Assert.Equal("Missing gateway (gw-missing)", vm.GatewayDetailText);
    }

    [Fact]
    public void UpdateFromDto_RecomputesGatewayState()
    {
        ServerItemViewModel vm = ServerItemViewModel.FromDto(
            CreateServer("gw-missing"),
            gatewayMap: new Dictionary<string, SshGatewayDto>());

        Dictionary<string, SshGatewayDto> gatewayMap = new(StringComparer.Ordinal)
        {
            ["gw-1"] = new SshGatewayDto { Id = "gw-1", Name = "Prod Bastion" }
        };

        vm.UpdateFromDto(CreateServer("gw-1"), gatewayMap: gatewayMap);

        Assert.True(vm.IsGatewayBadgeVisible);
        Assert.False(vm.IsGatewayMissing);
        Assert.Equal("Prod Bastion", vm.GatewayName);
        Assert.Equal("via Prod Bastion", vm.GatewayBadgeText);
        Assert.Equal("Prod Bastion", vm.GatewayDetailText);
    }

    [Fact]
    public async Task FromDto_WithFrenchLocalizer_UsesLocalizedMissingGatewayBadge()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        ServerProfileDto dto = CreateServer("gw-absente");

        ServerItemViewModel vm = ServerItemViewModel.FromDto(
            dto,
            gatewayMap: new Dictionary<string, SshGatewayDto>(),
            localizer: localizer);

        Assert.True(vm.IsGatewayBadgeVisible);
        Assert.True(vm.IsGatewayMissing);
        Assert.Equal("passerelle manquante", vm.GatewayBadgeText);
        Assert.Equal("Passerelle manquante (gw-absente)", vm.GatewayDetailText);
    }

    private static ServerProfileDto CreateServer(string? gatewayId)
    {
        return new ServerProfileDto
        {
            Id = "server-1",
            DisplayName = "Prod",
            ConnectionType = "SSH",
            RemoteServer = "prod.example.test",
            SshGatewayId = gatewayId
        };
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
