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

using System.Reflection;
using Heimdall.Core.Configuration;
using Heimdall.Core.Rdp;

namespace Heimdall.Core.Tests;

/// <summary>
/// Every declared setting range, frozen as data, and the loader's behaviour derived from it.
/// </summary>
/// <remarks>
/// <para><b>The table is the oracle.</b> A wide mechanical change - forty-six attributes written
/// from a validator that spelled each bound by hand - is honest only if something outside the
/// change says what the bounds were. This table holds them: the validator's values as they stood
/// before, with the two bounds Julien arbitrated on 2026-09-04 where the loader and the screen had
/// disagreed. A declaration that drifts from a row is a red test, and a new ranged setting is a
/// new row, on purpose.</para>
/// <para><b>The loader is measured from the declarations, not from a second list.</b> For every
/// declared range the value just outside each end is diagnosed and the ends themselves are not,
/// so a validator that stopped reading the attributes, or read them with the comparison the wrong
/// way round, fails here for every setting at once.</para>
/// </remarks>
public sealed class SettingRangesTests
{
    private static readonly (string Name, int Min, int Max, bool ZeroMeansOff)[] ExpectedAppSettingsRanges =
    [
        (nameof(AppSettings.DefaultResolutionWidth), RdpDisplayLimits.MinimumSessionResolution, RdpDisplayLimits.MaximumSessionResolution, false),
        (nameof(AppSettings.DefaultResolutionHeight), RdpDisplayLimits.MinimumSessionResolution, RdpDisplayLimits.MaximumSessionResolution, false),
        (nameof(AppSettings.UpdateCheckIntervalHours), 1, 8760, false),
        (nameof(AppSettings.TunnelEstablishmentDelayMs), 0, 60000, false),
        (nameof(AppSettings.TunnelRetryDelayMs), 0, 60000, false),
        (nameof(AppSettings.ProcessKillTimeoutMs), 0, 60000, false),
        (nameof(AppSettings.ExternalToolTimeoutMs), 5000, 600000, false),
        (nameof(AppSettings.HostKeyProbeTimeoutMs), 1000, 120000, false),
        (nameof(AppSettings.TelnetConnectTimeoutMs), 1000, 120000, false),
        (nameof(AppSettings.CredentialProviderTimeoutMs), 1000, 120000, false),
        (nameof(AppSettings.RdpCredentialAutofillTimeoutMs), 5000, 300000, false),
        (nameof(AppSettings.RdpArtifactCleanupDelayMs), 1000, 60000, false),
        (nameof(AppSettings.RdpResizeEnableDelayMs), 1000, 60000, true),
        (nameof(AppSettings.RdpConnectWatchdogTimeoutMs), 5000, 600000, true),
        (nameof(AppSettings.RdpAutoReconnectMaxAttempts), 1, AppSettings.DefaultRdpAutoReconnectMaxAttempts, false),
        (nameof(AppSettings.RdpKeepAliveIntervalMs), 5000, 300000, false),
        (nameof(AppSettings.RdpHostPoolCapacity), 0, 8, false),
        (nameof(AppSettings.RdpHostPoolIdleExpiryMinutes), 0, 1440, false),
        (nameof(AppSettings.SshKeepAliveIntervalSeconds), 5, 600, false),
        (nameof(AppSettings.PlinkPortCheckIntervalMs), 500, 30000, false),
        (nameof(AppSettings.PlinkKillGracePeriodMs), 500, 30000, false),
        (nameof(AppSettings.SftpUploadDebounceMs), 500, 30000, false),
        (nameof(AppSettings.ServerShutdownTimeoutMs), 500, 30000, false),
        (nameof(AppSettings.SleepPreventionIntervalSeconds), 10, 600, false),
        (nameof(AppSettings.FileLoggerFlushIntervalMs), 500, 30000, false),
        (nameof(AppSettings.DefaultRdpTunnelPort), 1, 65535, false),
        (nameof(AppSettings.DefaultSshTunnelPort), 1, 65535, false),
        (nameof(AppSettings.EphemeralHttpPort), 1, 65535, false),
        (nameof(AppSettings.EphemeralTftpPort), 1, 65535, false),
        (nameof(AppSettings.TerminalFontSize), 8, 72, false),
        // Arbitrated 2026-09-04: 0 turns the timer off at the use site and used to be warned
        // about; 3600 is the ceiling the settings screen always accepted, the loader said 600.
        (nameof(AppSettings.AntiIdleIntervalSeconds), 10, 3600, true),
        (nameof(AppSettings.SshTmoutResetIntervalSeconds), 0, 3600, false),
        (nameof(AppSettings.SshAutoReconnectAttempts), 1, 10, false),
        (nameof(AppSettings.SshAutoReconnectFirstDelaySeconds), 1, 600, false),
        (nameof(AppSettings.SshAutoReconnectSecondDelaySeconds), 1, 600, false),
        (nameof(AppSettings.SshAutoReconnectSubsequentDelaySeconds), 1, 600, false),
        (nameof(AppSettings.SshConnectTimeExitWindowSeconds), 0, 600, false),
        (nameof(AppSettings.RdpDefaultColorDepth), 16, 32, false),
        (nameof(AppSettings.MaxEmbeddedSessions), 1, 20, false),
        (nameof(AppSettings.SidebarWidth), 0, 1000, false),
        (nameof(AppSettings.SessionHealthCheckIntervalSeconds), 15, 3600, false),
        (nameof(AppSettings.SessionHealthProbeTimeoutMs), 250, 30000, false),
        (nameof(AppSettings.SessionHealthMaxConcurrent), 1, 50, false),
        (nameof(AppSettings.WindowsHelloGraceMinutes), 0, 1440, false),
        (nameof(AppSettings.AutoLockIdleMinutes), 0, 1440, false),
        (nameof(AppSettings.VaultHelloMaxDaysBeforeMasterPassword), 0, 3650, false),
    ];

    private static readonly (string Name, int Min, int Max, bool ZeroMeansOff)[] ExpectedServerRanges =
    [
        (nameof(ServerProfileDto.RdpAudioMode), 0, 2, false),
        (nameof(ServerProfileDto.RdpColorDepth), 16, 32, false),
    ];

    [Fact]
    public void AppSettings_DeclaresExactlyTheFrozenRanges()
    {
        AssertDeclaredRanges(typeof(AppSettings), ExpectedAppSettingsRanges);
    }

    [Fact]
    public void ServerProfile_DeclaresExactlyTheFrozenRanges()
    {
        AssertDeclaredRanges(typeof(ServerProfileDto), ExpectedServerRanges);
    }

    // The loader, measured from the declarations. Each row is one declared range; the value one
    // past each end is diagnosed and the ends are not.
    public static IEnumerable<object[]> DeclaredAppSettingsRanges()
        => SettingRanges.For(typeof(AppSettings)).Values.Select(range => new object[] { range.PropertyName });

    [Theory]
    [MemberData(nameof(DeclaredAppSettingsRanges))]
    public void ValidateSettings_DiagnosesJustOutsideEachEnd_AndAcceptsTheEnds(string propertyName)
    {
        SettingRange range = SettingRanges.Of(propertyName);
        PropertyInfo property = typeof(AppSettings).GetProperty(propertyName)!;

        Assert.Contains(propertyName, DiagnosticsWith(property, range.Min - 1));
        Assert.Contains(propertyName, DiagnosticsWith(property, range.Max + 1));
        Assert.DoesNotContain(propertyName, DiagnosticsWith(property, range.Min));
        Assert.DoesNotContain(propertyName, DiagnosticsWith(property, range.Max));

        if (range.DisabledValue is int off)
        {
            Assert.DoesNotContain(propertyName, DiagnosticsWith(property, off));
        }
    }

    [Fact]
    public void ValidateSettings_TheDiagnosticSentence_IsTheOneOptionThreeShipped()
    {
        var settings = new AppSettings { TunnelEstablishmentDelayMs = 240000 };

        string diagnostic = Assert.Single(
            SchemaValidator.ValidateSettings(settings).Errors,
            error => error.StartsWith(nameof(AppSettings.TunnelEstablishmentDelayMs), StringComparison.Ordinal));

        Assert.Equal(
            "TunnelEstablishmentDelayMs: value 240000 is outside the recommended range [0..60000]; the loader keeps it as written.",
            diagnostic);
    }

    // The two arbitrations, as tests that name the decision: what the loader and the screen used
    // to disagree about is now one value each.
    [Fact]
    public void Arbitration_TunnelDelay_45SecondsIsInsideTheRange()
    {
        var settings = new AppSettings { TunnelEstablishmentDelayMs = 45000 };
        Assert.DoesNotContain(
            SchemaValidator.ValidateSettings(settings).Errors,
            error => error.Contains(nameof(AppSettings.TunnelEstablishmentDelayMs), StringComparison.Ordinal));
        Assert.True(SettingRanges.Of(nameof(AppSettings.TunnelEstablishmentDelayMs)).Accepts(45000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3600)]
    public void Arbitration_AntiIdle_OffAndOneHourAreAccepted(int value)
    {
        var settings = new AppSettings { AntiIdleIntervalSeconds = value };
        Assert.DoesNotContain(
            SchemaValidator.ValidateSettings(settings).Errors,
            error => error.Contains(nameof(AppSettings.AntiIdleIntervalSeconds), StringComparison.Ordinal));
    }

    // The per-profile override shares the application setting's declaration, so it accepts the
    // same three things: inherit, off, or a value in the declared range.
    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1000, false)]
    [InlineData(60000, false)]
    [InlineData(999, true)]
    [InlineData(60001, true)]
    public void ValidateServer_ResizeDelayOverride_IsBoundedByTheSettingsDeclaration(int? value, bool diagnosed)
    {
        var server = new ServerProfileDto
        {
            Id = "srv",
            DisplayName = "Srv",
            RemoteServer = "srv.example",
            ConnectionType = "RDP",
            RdpResizeEnableDelayMs = value,
        };

        bool actual = SchemaValidator.ValidateServer(server).Errors
            .Any(error => error.StartsWith(nameof(ServerProfileDto.RdpResizeEnableDelayMs), StringComparison.Ordinal));

        Assert.Equal(diagnosed, actual);
    }

    [Fact]
    public void SettingRange_Accepts_TheEndsAndTheOffValue_AndNothingElseOutside()
    {
        SettingRange range = new("X", 10, 20, DisabledValue: 0);

        Assert.True(range.Accepts(10));
        Assert.True(range.Accepts(20));
        Assert.True(range.Accepts(0));
        Assert.False(range.Accepts(9));
        Assert.False(range.Accepts(21));
        Assert.False(range.Accepts(-1));
    }

    [Fact]
    public void SettingRanges_Of_RefusesAPropertyThatDeclaresNoRange()
    {
        Assert.Throws<KeyNotFoundException>(() => SettingRanges.Of(nameof(AppSettings.DefaultLocale)));
    }

    private static void AssertDeclaredRanges(Type type, (string Name, int Min, int Max, bool ZeroMeansOff)[] expected)
    {
        IReadOnlyDictionary<string, SettingRange> declared = SettingRanges.For(type);

        foreach ((string name, int min, int max, bool zeroMeansOff) in expected)
        {
            Assert.True(declared.TryGetValue(name, out SettingRange? range), $"{type.Name}.{name} declares no range.");
            Assert.Equal((min, max, zeroMeansOff ? 0 : (int?)null), (range!.Min, range.Max, range.DisabledValue));
        }

        // Nothing declared that the table does not know: a new ranged setting is a new row here.
        Assert.Equal(
            expected.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal),
            declared.Keys.OrderBy(name => name, StringComparer.Ordinal));
    }

    private static List<string> DiagnosticsWith(PropertyInfo property, int value)
    {
        var settings = new AppSettings();
        property.SetValue(settings, value);
        return SchemaValidator.ValidateSettings(settings).Errors
            .Where(error => error.StartsWith(property.Name + ":", StringComparison.Ordinal))
            .Select(error => property.Name)
            .ToList();
    }
}
