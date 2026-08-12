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
using System.Windows.Media;
using Heimdall.App.Converters;
using Heimdall.Core.SessionHealth;

namespace Heimdall.App.Tests;

public sealed class ServerStatusToColorConverterTests
{
    private static readonly Brush TaggedBrush = new SolidColorBrush(Colors.Magenta);

    private static ServerStatusToColorConverter MakeConverter(out List<string> resolvedKeys)
    {
        var captured = new List<string>();
        resolvedKeys = captured;
        return new ServerStatusToColorConverter(key =>
        {
            captured.Add(key);
            return TaggedBrush;
        });
    }

    [Fact]
    public void ServerStatus_LaunchedExternalClient_UsesWarningBrushKey()
    {
        string? requestedKey = null;
        var warningBrush = Brushes.Orange;
        var converter = new ServerStatusToColorConverter(key =>
        {
            requestedKey = key;
            return warningBrush;
        });

        var result = converter.Convert(
            ["RDP", "LaunchedExternalClient"],
            typeof(Brush),
            parameter: null!,
            CultureInfo.InvariantCulture);

        Assert.Equal("WarningBrush", requestedKey);
        Assert.Same(warningBrush, result);
    }

    [Fact]
    public void ServerStatus_RemoteSessionHandedOff_UsesInfoBrushKey()
    {
        string? requestedKey = null;
        var infoBrush = Brushes.Cyan;
        var converter = new ServerStatusToColorConverter(key =>
        {
            requestedKey = key;
            return infoBrush;
        });

        var result = converter.Convert(
            ["WINRM", "RemoteSessionHandedOff"],
            typeof(Brush),
            parameter: null!,
            CultureInfo.InvariantCulture);

        Assert.Equal("InfoBrush", requestedKey);
        Assert.Same(infoBrush, result);
    }

    [Fact]
    public void TwoValues_DisconnectedSsh_FallsBackToConnectionTypePalette()
    {
        var converter = MakeConverter(out var keys);

        var result = converter.Convert(["SSH", "Disconnected"], typeof(Brush), null!, CultureInfo.InvariantCulture);

        Assert.Same(TaggedBrush, result);
        Assert.Equal("SuccessBrush", keys.Single());
    }

    [Fact]
    public void ThreeValuesWithInitialHealth_DisconnectedRdp_UsesTextDisabledBrush()
    {
        var converter = MakeConverter(out var keys);

        var result = converter.Convert(
            ["RDP", "Disconnected", HealthState.Initial],
            typeof(Brush), null!, CultureInfo.InvariantCulture);

        Assert.Same(TaggedBrush, result);
        Assert.Equal("TextDisabledBrush", keys.Single());
    }

    [Fact]
    public void FourValuesWithHealthUp_DisconnectedSsh_UsesSuccessBrush()
    {
        var converter = MakeConverter(out var keys);
        var up = new HealthState(HealthStatus.Up, DateTime.UtcNow, 12, null);

        var result = converter.Convert(
            ["SSH", "Disconnected", up, 7],
            typeof(Brush), null!, CultureInfo.InvariantCulture);

        Assert.Same(TaggedBrush, result);
        Assert.Equal("SuccessBrush", keys.Single());
    }

    [Fact]
    public void FourValuesWithHealthDown_DisconnectedRdp_UsesErrorBrush()
    {
        var converter = MakeConverter(out var keys);
        var down = new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "timeout");

        var result = converter.Convert(
            ["RDP", "Disconnected", down, 0],
            typeof(Brush), null!, CultureInfo.InvariantCulture);

        Assert.Same(TaggedBrush, result);
        Assert.Equal("ErrorBrush", keys.Single());
    }

    [Fact]
    public void ActiveState_AlwaysWinsOverHealth()
    {
        var converter = MakeConverter(out var keys);
        var down = new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "timeout");

        var result = converter.Convert(
            ["SSH", "Connected", down, 0],
            typeof(Brush), null!, CultureInfo.InvariantCulture);

        Assert.Same(TaggedBrush, result);
        Assert.Equal("SuccessBrush", keys.Single());
    }

    [Fact]
    public void LaunchingWinRm_WithHealthyServer_UsesWarningBrush()
    {
        ServerStatusToColorConverter converter = MakeConverter(out List<string> keys);
        HealthState up = new(HealthStatus.Up, DateTime.UtcNow, 12, null);

        object result = converter.Convert(
            ["WINRM", "LaunchingWinRm", up],
            typeof(Brush),
            null!,
            CultureInfo.InvariantCulture);

        Assert.Same(TaggedBrush, result);
        Assert.Equal("WarningBrush", keys.Single());
    }

    [Fact]
    public void ProbingHealth_DisconnectedSsh_UsesWarningBrush()
    {
        var converter = MakeConverter(out var keys);
        var probing = new HealthState(HealthStatus.Probing, DateTime.UtcNow, null, null);

        var result = converter.Convert(
            ["SSH", "Disconnected", probing, 0],
            typeof(Brush), null!, CultureInfo.InvariantCulture);

        Assert.Same(TaggedBrush, result);
        Assert.Equal("WarningBrush", keys.Single());
    }

    [Fact]
    public void ConnectionState_LaunchedExternalClient_UsesWarningBrushKey()
    {
        string? requestedKey = null;
        var warningBrush = Brushes.Orange;
        var converter = new ConnectionStateToBrushConverter(key =>
        {
            requestedKey = key;
            return warningBrush;
        });

        var result = converter.Convert(
            "LaunchedExternalClient",
            typeof(Brush),
            parameter: null!,
            CultureInfo.InvariantCulture);

        Assert.Equal("WarningBrush", requestedKey);
        Assert.Same(warningBrush, result);
    }

    [Fact]
    public void ConnectionState_RemoteSessionHandedOff_UsesInfoBrushKey()
    {
        string? requestedKey = null;
        var infoBrush = Brushes.Cyan;
        var converter = new ConnectionStateToBrushConverter(key =>
        {
            requestedKey = key;
            return infoBrush;
        });

        var result = converter.Convert(
            "RemoteSessionHandedOff",
            typeof(Brush),
            parameter: null!,
            CultureInfo.InvariantCulture);

        Assert.Equal("InfoBrush", requestedKey);
        Assert.Same(infoBrush, result);
    }

    // Producer: ConnectionStateToBrushConverter.ResolveBrush amber arm — every
    // Launching* state carries IsProgress metadata and must render the warning
    // (amber) brush, not the gray fallback.
    [Theory]
    [InlineData("LaunchingRdp")]
    [InlineData("LaunchingSsh")]
    [InlineData("LaunchingSftp")]
    [InlineData("LaunchingLocal")]
    [InlineData("LaunchingVnc")]
    [InlineData("LaunchingFtp")]
    [InlineData("LaunchingTelnet")]
    [InlineData("LaunchingCitrix")]
    [InlineData("LaunchingWinRm")]
    public void ConnectionState_AnyLaunchingState_UsesWarningBrushKey(string state)
    {
        string? requestedKey = null;
        var converter = new ConnectionStateToBrushConverter(key =>
        {
            requestedKey = key;
            return Brushes.Orange;
        });

        converter.Convert(state, typeof(Brush), parameter: null!, CultureInfo.InvariantCulture);

        Assert.Equal("WarningBrush", requestedKey);
    }

    [Theory]
    [InlineData("Connected", "SuccessBrush")]
    [InlineData("Error", "ErrorBrush")]
    [InlineData("Disconnected", "TextSecondaryBrush")]
    [InlineData("SomethingUnknown", "TextSecondaryBrush")]
    public void ConnectionState_AnchorStates_UseExpectedBrushKeys(string state, string expectedKey)
    {
        string? requestedKey = null;
        var converter = new ConnectionStateToBrushConverter(key =>
        {
            requestedKey = key;
            return Brushes.Gray;
        });

        converter.Convert(state, typeof(Brush), parameter: null!, CultureInfo.InvariantCulture);

        Assert.Equal(expectedKey, requestedKey);
    }
}
