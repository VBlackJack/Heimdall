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

namespace Heimdall.Rdp.Tests;

public sealed class RdpGatewayAttestationExceptionTests
{
    [Fact]
    public void ForComparison_SetsComparisonStep()
    {
        RdpGatewayAttestationException exception = RdpGatewayAttestationException.ForComparison(
            "gateway.example.com",
            [RdpGatewayAttestationProperty.GatewayHostname]);

        Assert.Equal(RdpGatewayAttestationStep.SettingsComparison, exception.Step);
    }

    [Fact]
    public void ForComparison_SortsPropertiesInDeclarationOrder()
    {
        RdpGatewayAttestationException exception = RdpGatewayAttestationException.ForComparison(
            "gateway.example.com",
            [
                RdpGatewayAttestationProperty.GatewayCredsSource,
                RdpGatewayAttestationProperty.GatewayHostname,
                RdpGatewayAttestationProperty.GatewayProfileUsageMethod
            ]);

        Assert.Equal(
            [
                RdpGatewayAttestationProperty.GatewayHostname,
                RdpGatewayAttestationProperty.GatewayProfileUsageMethod,
                RdpGatewayAttestationProperty.GatewayCredsSource
            ],
            exception.DivergentProperties);
    }

    [Fact]
    public void ForComparison_DeduplicatesProperties()
    {
        RdpGatewayAttestationException exception = RdpGatewayAttestationException.ForComparison(
            "gateway.example.com",
            [
                RdpGatewayAttestationProperty.GatewayUsageMethod,
                RdpGatewayAttestationProperty.GatewayUsageMethod
            ]);

        Assert.Equal(
            [RdpGatewayAttestationProperty.GatewayUsageMethod],
            exception.DivergentProperties);
    }

    [Fact]
    public void ForComparison_EmptyProperties_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => RdpGatewayAttestationException.ForComparison(
                "gateway.example.com",
                Array.Empty<RdpGatewayAttestationProperty>()));
    }

    [Fact]
    public void ForComparison_NullProperties_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => RdpGatewayAttestationException.ForComparison(
                "gateway.example.com",
                null!));
    }

    [Fact]
    public void ForComparison_PreservesInnerException()
    {
        InvalidOperationException innerException = new("read-back failed");

        RdpGatewayAttestationException exception = RdpGatewayAttestationException.ForComparison(
            "gateway.example.com",
            [RdpGatewayAttestationProperty.GatewayHostname],
            innerException);

        Assert.Same(innerException, exception.InnerException);
    }

    [Fact]
    public void ForComparison_MessageListsPropertiesInDeclarationOrder()
    {
        RdpGatewayAttestationException exception = RdpGatewayAttestationException.ForComparison(
            "gateway.example.com",
            [
                RdpGatewayAttestationProperty.GatewayCredsSource,
                RdpGatewayAttestationProperty.GatewayHostname,
                RdpGatewayAttestationProperty.GatewayProfileUsageMethod
            ]);

        int hostnameIndex = exception.Message.IndexOf(
            nameof(RdpGatewayAttestationProperty.GatewayHostname),
            StringComparison.Ordinal);
        int profileIndex = exception.Message.IndexOf(
            nameof(RdpGatewayAttestationProperty.GatewayProfileUsageMethod),
            StringComparison.Ordinal);
        int credentialsIndex = exception.Message.IndexOf(
            nameof(RdpGatewayAttestationProperty.GatewayCredsSource),
            StringComparison.Ordinal);

        Assert.True(hostnameIndex >= 0);
        Assert.True(profileIndex > hostnameIndex);
        Assert.True(credentialsIndex > profileIndex);
    }

    [Fact]
    public void Constructor_NonComparisonStep_HasNoDivergenceDetails()
    {
        RdpGatewayAttestationException exception = new(
            "gateway.example.com",
            RdpGatewayAttestationStep.SettingsReadBack);

        Assert.Empty(exception.DivergentProperties);
        Assert.DoesNotContain("(divergent:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ComparisonStepWithoutProperties_HasNoDivergenceDetails()
    {
        RdpGatewayAttestationException exception = new(
            "gateway.example.com",
            RdpGatewayAttestationStep.SettingsComparison);

        Assert.Empty(exception.DivergentProperties);
        Assert.DoesNotContain("(divergent:", exception.Message, StringComparison.Ordinal);
    }
}
