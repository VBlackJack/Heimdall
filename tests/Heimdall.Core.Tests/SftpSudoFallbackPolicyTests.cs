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

using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

/// <summary>
/// The "retry as root" mode: how a settings file reads it, how the organisation cap is read, and
/// how the two combine.
/// </summary>
[Collection("FileLogger")]
public sealed class SftpSudoFallbackPolicyTests
{
    private static SftpSudoFallbackMode Read(string json)
        => JsonSerializer.Deserialize<AppSettings>(json)!.SftpSudoFallback;

    [Fact]
    public void TheNumericOrderIsTheStrictnessOrder()
    {
        Assert.True(SftpSudoFallbackMode.Never < SftpSudoFallbackMode.Ask);
        Assert.True(SftpSudoFallbackMode.Ask < SftpSudoFallbackMode.Automatic);
        Assert.Equal(0, (int)SftpSudoFallbackMode.Never);
        Assert.Equal(1, (int)SftpSudoFallbackMode.Ask);
        Assert.Equal(2, (int)SftpSudoFallbackMode.Automatic);
    }

    [Fact]
    public void ANewSettingsObjectAsks()
        => Assert.Equal(SftpSudoFallbackMode.Ask, new AppSettings().SftpSudoFallback);

    [Theory]
    [InlineData("{}", SftpSudoFallbackMode.Ask)]
    [InlineData("{\"SftpSudoFallback\":\"Never\"}", SftpSudoFallbackMode.Never)]
    [InlineData("{\"SftpSudoFallback\":\"automatic\"}", SftpSudoFallbackMode.Automatic)]
    [InlineData("{\"SftpSudoFallback\":0}", SftpSudoFallbackMode.Never)]
    [InlineData("{\"SftpSudoFallback\":2}", SftpSudoFallbackMode.Automatic)]
    public void AKnownOrMissingValueIsRead(string json, SftpSudoFallbackMode expected)
        => Assert.Equal(expected, Read(json));

    [Theory]
    [InlineData("{\"SftpSudoFallback\":\"Always\"}")]
    [InlineData("{\"SftpSudoFallback\":7}")]
    [InlineData("{\"SftpSudoFallback\":-1}")]
    [InlineData("{\"SftpSudoFallback\":\"\"}")]
    [InlineData("{\"SftpSudoFallback\":true}")]
    public void AnUnknownValueAsksInsteadOfFailingTheWholeFile(string json)
        => Assert.Equal(SftpSudoFallbackMode.Ask, Read(json));

    [Theory]
    [InlineData(SftpSudoFallbackMode.Never)]
    [InlineData(SftpSudoFallbackMode.Ask)]
    [InlineData(SftpSudoFallbackMode.Automatic)]
    public void TheModeRoundTripsByName(SftpSudoFallbackMode mode)
    {
        string json = JsonSerializer.Serialize(new AppSettings { SftpSudoFallback = mode });

        Assert.Contains("\"SftpSudoFallback\":\"" + mode + "\"", json, StringComparison.Ordinal);
        Assert.Equal(mode, Read(json));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(-1, null)]
    [InlineData(3, null)]
    [InlineData(99, null)]
    [InlineData(int.MaxValue, null)]
    [InlineData(0, SftpSudoFallbackMode.Never)]
    [InlineData(1, SftpSudoFallbackMode.Ask)]
    [InlineData(2, SftpSudoFallbackMode.Automatic)]
    public void ThePolicyValueBecomesACapOrNoCap(int? raw, SftpSudoFallbackMode? expected)
        => Assert.Equal(expected, new SftpSudoFallbackPolicy(() => raw).Cap);

    [Fact]
    public void ThePolicyIsReadOnceAndKept()
    {
        int reads = 0;
        int? value = 1;
        SftpSudoFallbackPolicy policy = new(() =>
        {
            reads++;
            return value;
        });

        Assert.Equal(0, reads);
        Assert.Equal(SftpSudoFallbackMode.Ask, policy.Cap);
        value = 0;
        Assert.Equal(SftpSudoFallbackMode.Ask, policy.Cap);
        _ = policy.Effective(SftpSudoFallbackMode.Automatic);

        Assert.Equal(1, reads);
    }

    [Fact]
    public void AMissingValueIsAlsoReadOnce()
    {
        int reads = 0;
        SftpSudoFallbackPolicy policy = new(() =>
        {
            reads++;
            return null;
        });

        Assert.Null(policy.Cap);
        Assert.Null(policy.Cap);

        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(null, SftpSudoFallbackMode.Never, SftpSudoFallbackMode.Never)]
    [InlineData(null, SftpSudoFallbackMode.Ask, SftpSudoFallbackMode.Ask)]
    [InlineData(null, SftpSudoFallbackMode.Automatic, SftpSudoFallbackMode.Automatic)]
    [InlineData(0, SftpSudoFallbackMode.Never, SftpSudoFallbackMode.Never)]
    [InlineData(0, SftpSudoFallbackMode.Ask, SftpSudoFallbackMode.Never)]
    [InlineData(0, SftpSudoFallbackMode.Automatic, SftpSudoFallbackMode.Never)]
    [InlineData(1, SftpSudoFallbackMode.Never, SftpSudoFallbackMode.Never)]
    [InlineData(1, SftpSudoFallbackMode.Ask, SftpSudoFallbackMode.Ask)]
    [InlineData(1, SftpSudoFallbackMode.Automatic, SftpSudoFallbackMode.Ask)]
    [InlineData(2, SftpSudoFallbackMode.Never, SftpSudoFallbackMode.Never)]
    [InlineData(2, SftpSudoFallbackMode.Ask, SftpSudoFallbackMode.Ask)]
    [InlineData(2, SftpSudoFallbackMode.Automatic, SftpSudoFallbackMode.Automatic)]
    [InlineData(42, SftpSudoFallbackMode.Automatic, SftpSudoFallbackMode.Automatic)]
    public void TheEffectiveModeIsTheStricterOfTheChoiceAndTheCap(
        int? raw,
        SftpSudoFallbackMode choice,
        SftpSudoFallbackMode expected)
        => Assert.Equal(expected, new SftpSudoFallbackPolicy(() => raw).Effective(choice));

    [Fact]
    public void TheMachineRegistryReadNeverThrows()
    {
        SftpSudoFallbackPolicy policy = new();

        SftpSudoFallbackMode? cap = policy.Cap;

        Assert.True(cap is null || Enum.IsDefined(cap.Value));
    }

    [Fact]
    public void ThePolicyKeyAndValueNamesAreTheDocumentedOnes()
    {
        Assert.Equal(@"Software\Policies\Heimdall", SftpSudoFallbackPolicy.PolicyKeyPath);
        Assert.Equal("SftpSudoFallbackMax", SftpSudoFallbackPolicy.PolicyValueName);
    }
}
