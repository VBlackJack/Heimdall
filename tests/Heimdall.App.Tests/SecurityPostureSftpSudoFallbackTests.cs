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

using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>The SFTP root fallback line of the security posture card, as a pure decision.</summary>
public sealed class SecurityPostureSftpSudoFallbackTests
{
    private static readonly SecurityPostureInputs Safe = new(
        RdpNla: true,
        RdpStrictServerAuthentication: true,
        TftpSharing: false,
        SessionTranscripts: false,
        PowerShellExecutionPolicy: "Default",
        VaultEnabled: true,
        AutoLockIdleMinutes: 15,
        DisconnectOnLock: true,
        RequireCredentialGuard: true,
        RequireWindowsHelloOnConnect: true,
        UpdateChecks: true,
        KnownHostsSync: false);

    private static SecurityPostureItem Line(SecurityPostureInputs inputs)
        => SecurityPosture.Evaluate(inputs).Single(item => item.Key == SecurityPostureKey.SftpSudoFallback);

    [Fact]
    public void InputsThatSayNothingAboutItGetTheSafeDefaultAndAreNotFlagged()
    {
        SecurityPostureItem line = Line(Safe);

        Assert.Equal(SecurityPostureState.Ask, line.State);
        Assert.False(line.IsRisky);
        Assert.Equal(SftpSudoFallbackMode.Ask, new AppSettings().SftpSudoFallback);
    }

    [Theory]
    [InlineData(SftpSudoFallbackMode.Never, SecurityPostureState.Never, false)]
    [InlineData(SftpSudoFallbackMode.Ask, SecurityPostureState.Ask, false)]
    [InlineData(SftpSudoFallbackMode.Automatic, SecurityPostureState.Automatic, true)]
    public void OnlyAutomaticIsRisky(SftpSudoFallbackMode mode, SecurityPostureState state, bool risky)
    {
        SecurityPostureItem line = Line(Safe with { SftpSudoFallback = mode });

        Assert.Equal(state, line.State);
        Assert.Equal(risky, line.IsRisky);
        Assert.Equal(SecurityPosture.SftpSudoFallbackTarget, line.TargetSettingId);
    }

    [Theory]
    [InlineData(SftpSudoFallbackMode.Automatic, SftpSudoFallbackMode.Never, "Never")]
    [InlineData(SftpSudoFallbackMode.Automatic, SftpSudoFallbackMode.Ask, "Ask")]
    [InlineData(SftpSudoFallbackMode.Ask, SftpSudoFallbackMode.Never, "Never")]
    public void ACapThatLowersTheChoiceIsManagedAndShowsWhatApplies(
        SftpSudoFallbackMode choice,
        SftpSudoFallbackMode cap,
        string detail)
    {
        SecurityPostureItem line = Line(Safe with { SftpSudoFallback = choice, SftpSudoFallbackCap = cap });

        Assert.Equal(SecurityPostureState.Managed, line.State);
        Assert.NotEqual(SecurityPostureState.Policy, line.State);
        Assert.False(line.IsRisky);
        Assert.Equal(detail, line.Detail);
    }

    [Theory]
    [InlineData(SftpSudoFallbackMode.Never, SftpSudoFallbackMode.Never, SecurityPostureState.Never)]
    [InlineData(SftpSudoFallbackMode.Ask, SftpSudoFallbackMode.Ask, SecurityPostureState.Ask)]
    [InlineData(SftpSudoFallbackMode.Ask, SftpSudoFallbackMode.Automatic, SecurityPostureState.Ask)]
    public void ACapThatChangesNothingIsNotManaged(
        SftpSudoFallbackMode choice,
        SftpSudoFallbackMode cap,
        SecurityPostureState state)
        => Assert.Equal(state, Line(Safe with { SftpSudoFallback = choice, SftpSudoFallbackCap = cap }).State);

    [Fact]
    public void AnAutomaticCapDoesNotHideAnAutomaticChoice()
    {
        SecurityPostureItem line = Line(Safe with
        {
            SftpSudoFallback = SftpSudoFallbackMode.Automatic,
            SftpSudoFallbackCap = SftpSudoFallbackMode.Automatic,
        });

        Assert.True(line.IsRisky);
        Assert.Equal(1, SecurityPosture.CountRisky(SecurityPosture.Evaluate(Safe with
        {
            SftpSudoFallback = SftpSudoFallbackMode.Automatic,
        })));
    }
}
