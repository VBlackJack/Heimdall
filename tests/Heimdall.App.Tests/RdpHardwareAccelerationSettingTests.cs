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
using System.Text.Json;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Rdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Covers the RDP hardware-acceleration setting introduced for issue #161.
/// </summary>
/// <remarks>
/// The setting is off by default on purpose: three concurrent 1920x1080 sessions were measured
/// at 1145.9 MB of private commit with hardware mode on against 763.3 MB with it off, for 840
/// extra kernel handles. These tests pin both the routing and the default, because a routing
/// that silently falls back to the other branch, or a default that drifts back to on, would
/// give the memory back without anyone noticing.
/// </remarks>
public sealed class RdpHardwareAccelerationSettingTests
{
    [Fact]
    public void BuildRedirections_WhenUsingGlobalDefaults_TakesTheGlobalValue()
    {
        var server = new ServerProfileDto
        {
            RdpUseGlobalDefaults = true,
            RdpHardwareAcceleration = false,
        };
        var settings = new AppSettings { RdpDefaultHardwareAcceleration = true };

        RdpRedirectionOptions redirections = RdpProfileResolver.BuildRedirections(server, settings);

        // Reading the server value in the global branch would answer false here.
        Assert.True(redirections.HardwareAcceleration);
    }

    [Fact]
    public void BuildRedirections_WhenOverridingPerServer_TakesTheServerValue()
    {
        var server = new ServerProfileDto
        {
            RdpUseGlobalDefaults = false,
            RdpHardwareAcceleration = true,
        };
        var settings = new AppSettings { RdpDefaultHardwareAcceleration = false };

        RdpRedirectionOptions redirections = RdpProfileResolver.BuildRedirections(server, settings);

        // Reading the global value in the per-server branch would answer false here.
        Assert.True(redirections.HardwareAcceleration);
    }

    [Fact]
    public void HardwareAcceleration_IsOffByDefaultEverywhere()
    {
        Assert.False(new AppSettings().RdpDefaultHardwareAcceleration);
        Assert.False(new ServerProfileDto().RdpHardwareAcceleration);
        Assert.False(new RdpRedirectionOptions().HardwareAcceleration);
    }

    [Fact]
    public void ShippedDefaultSettings_DisableHardwareAcceleration()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config", "settings.default.json");
        Assert.True(File.Exists(path), $"settings.default.json not found at {path}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        // A setting that exists in code but not in the shipped defaults leaves upgraded
        // installations on whatever the deserializer happens to produce.
        Assert.True(
            document.RootElement.TryGetProperty("rdpDefaultHardwareAcceleration", out JsonElement value),
            "rdpDefaultHardwareAcceleration is missing from the shipped default settings");
        Assert.False(value.GetBoolean());
    }

    [Fact]
    public void ServerDialogViewModel_RoundTripsHardwareAccelerationThroughTheDto()
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "measurement target",
            RemoteServer = "192.0.2.10",
            ConnectionType = "RDP",
            RdpHardwareAcceleration = true,
        };

        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        Assert.True(viewModel.RdpHardwareAcceleration);

        ServerProfileDto round = viewModel.ToDto();

        // Dropping either the FromDto or the ToDto assignment loses the operator's choice
        // silently, which is exactly how a saved setting stops being saved.
        Assert.True(round.RdpHardwareAcceleration);
    }
}
