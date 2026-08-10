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

using Heimdall.Rdp.ActiveX;

namespace Heimdall.Rdp.Tests;

public sealed class RdpGatewayAttestationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_BlankGateway_DoesNotTouchSettings(string? gatewayHost)
    {
        var settings = new FakeSettings { ThrowOnAccess = true };

        RdpGatewayAttestation.Apply(gatewayHost, settings);

        Assert.Equal(0, settings.AccessCount);
    }

    [Fact]
    public void Apply_InvalidGateway_ThrowsValidationFailure()
    {
        RdpGatewayAttestationException exception = Assert.Throws<RdpGatewayAttestationException>(
            () => RdpGatewayAttestation.Apply("invalid gateway!", new FakeSettings()));

        Assert.Equal(RdpGatewayAttestationStep.GatewayValidation, exception.Step);
        Assert.Empty(exception.DivergentProperties);
    }

    [Fact]
    public void Apply_NullSettings_ThrowsAvailabilityFailure()
    {
        RdpGatewayAttestationException exception = Assert.Throws<RdpGatewayAttestationException>(
            () => RdpGatewayAttestation.Apply("gateway.example.com", null));

        Assert.Equal(RdpGatewayAttestationStep.SettingsAvailability, exception.Step);
        Assert.Empty(exception.DivergentProperties);
    }

    [Fact]
    public void Apply_WriteThrows_ThrowsWriteFailure()
    {
        FakeSettings settings = new() { ThrowOnWrite = true };

        RdpGatewayAttestationException exception = Assert.Throws<RdpGatewayAttestationException>(
            () => RdpGatewayAttestation.Apply("gateway.example.com", settings));

        Assert.Equal(RdpGatewayAttestationStep.SettingsWrite, exception.Step);
        Assert.Empty(exception.DivergentProperties);
    }

    [Fact]
    public void Apply_ReadBackThrows_ThrowsReadBackFailure()
    {
        FakeSettings settings = new() { ThrowOnRead = true };

        RdpGatewayAttestationException exception = Assert.Throws<RdpGatewayAttestationException>(
            () => RdpGatewayAttestation.Apply("gateway.example.com", settings));

        Assert.Equal(RdpGatewayAttestationStep.SettingsReadBack, exception.Step);
        Assert.Empty(exception.DivergentProperties);
    }

    [Fact]
    public void Apply_HostnameMismatch_ThrowsComparisonFailure()
    {
        FakeSettings settings = new() { ReadGatewayHostname = "other.example.com" };

        RdpGatewayAttestationException exception = Assert.Throws<RdpGatewayAttestationException>(
            () => RdpGatewayAttestation.Apply("gateway.example.com", settings));

        Assert.Equal(RdpGatewayAttestationStep.SettingsComparison, exception.Step);
        Assert.Equal(
            new[] { RdpGatewayAttestationProperty.GatewayHostname },
            exception.DivergentProperties);
    }

    [Theory]
    [InlineData(
        nameof(IRdpGatewayTransportSettings.GatewayUsageMethod),
        RdpGatewayAttestationProperty.GatewayUsageMethod)]
    [InlineData(
        nameof(IRdpGatewayTransportSettings.GatewayProfileUsageMethod),
        RdpGatewayAttestationProperty.GatewayProfileUsageMethod)]
    [InlineData(
        nameof(IRdpGatewayTransportSettings.GatewayCredsSource),
        RdpGatewayAttestationProperty.GatewayCredsSource)]
    public void Apply_NumericMismatch_ThrowsComparisonFailure(
        string propertyName,
        RdpGatewayAttestationProperty expectedProperty)
    {
        FakeSettings settings = new()
        {
            MismatchedNumericProperties = new HashSet<string>(StringComparer.Ordinal) { propertyName }
        };

        RdpGatewayAttestationException exception = Assert.Throws<RdpGatewayAttestationException>(
            () => RdpGatewayAttestation.Apply("gateway.example.com", settings));

        Assert.Equal(RdpGatewayAttestationStep.SettingsComparison, exception.Step);
        Assert.Equal(new[] { expectedProperty }, exception.DivergentProperties);
    }

    [Fact]
    public void Apply_MultipleMismatches_ReportsEveryPropertyInDeclarationOrder()
    {
        FakeSettings settings = new()
        {
            ReadGatewayHostname = "other.example.com",
            MismatchedNumericProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(IRdpGatewayTransportSettings.GatewayCredsSource),
                nameof(IRdpGatewayTransportSettings.GatewayProfileUsageMethod),
                nameof(IRdpGatewayTransportSettings.GatewayUsageMethod)
            }
        };

        RdpGatewayAttestationException exception = Assert.Throws<RdpGatewayAttestationException>(
            () => RdpGatewayAttestation.Apply("gateway.example.com", settings));

        RdpGatewayAttestationProperty[] expectedProperties =
        [
            RdpGatewayAttestationProperty.GatewayHostname,
            RdpGatewayAttestationProperty.GatewayUsageMethod,
            RdpGatewayAttestationProperty.GatewayProfileUsageMethod,
            RdpGatewayAttestationProperty.GatewayCredsSource
        ];
        Assert.Equal(expectedProperties, exception.DivergentProperties);
        Assert.Contains(
            "(divergent: GatewayHostname, GatewayUsageMethod, GatewayProfileUsageMethod, GatewayCredsSource).",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_AllReadBacksMatch_WritesExpectedValues()
    {
        var settings = new FakeSettings();

        RdpGatewayAttestation.Apply("gateway.example.com", settings);

        Assert.Equal("gateway.example.com", settings.WrittenGatewayHostname);
        Assert.Equal(1, Convert.ToInt32(settings.WrittenGatewayUsageMethod));
        Assert.Equal(1, Convert.ToInt32(settings.WrittenGatewayProfileUsageMethod));
        Assert.Equal(0, Convert.ToInt32(settings.WrittenGatewayCredsSource));
    }

    private sealed class FakeSettings : IRdpGatewayTransportSettings
    {
        private string _gatewayHostname = string.Empty;
        private object _gatewayUsageMethod = 0;
        private object _gatewayProfileUsageMethod = 0;
        private object _gatewayCredsSource = 0;

        public bool ThrowOnAccess { get; init; }

        public bool ThrowOnWrite { get; init; }

        public bool ThrowOnRead { get; init; }

        public string? ReadGatewayHostname { get; init; }

        public IReadOnlySet<string> MismatchedNumericProperties { get; init; } =
            new HashSet<string>(StringComparer.Ordinal);

        public int AccessCount { get; private set; }

        public string WrittenGatewayHostname => _gatewayHostname;

        public object WrittenGatewayUsageMethod => _gatewayUsageMethod;

        public object WrittenGatewayProfileUsageMethod => _gatewayProfileUsageMethod;

        public object WrittenGatewayCredsSource => _gatewayCredsSource;

        public string GatewayHostname
        {
            get
            {
                OnRead();
                return ReadGatewayHostname ?? _gatewayHostname;
            }
            set
            {
                OnWrite();
                _gatewayHostname = value;
            }
        }

        public object GatewayUsageMethod
        {
            get
            {
                OnRead();
                return MismatchedNumericProperties.Contains(nameof(GatewayUsageMethod))
                    ? 2L
                    : Convert.ToInt64(_gatewayUsageMethod);
            }
            set
            {
                OnWrite();
                _gatewayUsageMethod = value;
            }
        }

        public object GatewayProfileUsageMethod
        {
            get
            {
                OnRead();
                return MismatchedNumericProperties.Contains(nameof(GatewayProfileUsageMethod))
                    ? 2L
                    : Convert.ToInt64(_gatewayProfileUsageMethod);
            }
            set
            {
                OnWrite();
                _gatewayProfileUsageMethod = value;
            }
        }

        public object GatewayCredsSource
        {
            get
            {
                OnRead();
                return MismatchedNumericProperties.Contains(nameof(GatewayCredsSource))
                    ? 1L
                    : Convert.ToInt64(_gatewayCredsSource);
            }
            set
            {
                OnWrite();
                _gatewayCredsSource = value;
            }
        }

        private void OnWrite()
        {
            AccessCount++;
            if (ThrowOnAccess || ThrowOnWrite)
            {
                throw new InvalidOperationException("Write failed.");
            }
        }

        private void OnRead()
        {
            AccessCount++;
            if (ThrowOnAccess || ThrowOnRead)
            {
                throw new InvalidOperationException("Read failed.");
            }
        }
    }
}
