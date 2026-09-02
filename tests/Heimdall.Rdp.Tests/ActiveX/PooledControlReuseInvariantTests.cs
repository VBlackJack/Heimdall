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

using Heimdall.Core.Configuration;
using Heimdall.Rdp.ActiveX;
using Heimdall.Rdp.Display;

namespace Heimdall.Rdp.Tests.ActiveX;

/// <summary>
/// One invariant, asserted on every setting a pooled control carries: a profile that asks for
/// nothing gets the default written onto the control, not whatever the previous profile left
/// there.
/// </summary>
/// <remarks>
/// <para>Reuse buys a measured 66 kernel handles per session, and it costs this: two profiles
/// share one COM object. <see cref="RdpSessionState.Reset"/> restores the Heimdall side of that
/// object, and nothing restored the control's own side, so every setting written under an "only
/// when the profile carries one" guard was inherited in silence - the RD Gateway, the logon
/// identity, USB redirection and the monitor selection all had that shape.</para>
/// <para>The composite test below is the one that matters: it drives a single fake control
/// through profile A, a reset, and then a profile B that names nothing, and reads back what B's
/// session would actually run with. The per-setting tests beside it say which write is missing
/// when it fails.</para>
/// </remarks>
public sealed class PooledControlReuseInvariantTests
{
    private const string ProfileAGateway = "gw.corp.example";
    private const string ProfileAUsername = "svc-admin";
    private const string ProfileADomain = "CORP";

    /// <summary>
    /// The whole defect in one sequence: profile A, release to the pool, then a profile that
    /// carries no gateway, no identity, no USB redirection and no monitor selection.
    /// </summary>
    [Fact]
    public void AProfileThatNamesNothing_InheritsNothingFromThePreviousSession()
    {
        var control = new FakeControl();
        ApplyProfile(
            control,
            gateway: ProfileAGateway,
            username: ProfileAUsername,
            domain: ProfileADomain,
            usbRedirection: true,
            selectedMonitorIndices: [1]);

        // What the pool does between the two sessions: it resets the Heimdall-side state only.
        var session = new RdpSessionState();
        session.Reset();

        ApplyProfile(
            control,
            gateway: session.Redirections.GatewayHostname,
            username: session.Username,
            domain: session.Domain,
            usbRedirection: session.Redirections.Usb,
            selectedMonitorIndices: []);

        Assert.Equal(string.Empty, control.GatewayHostname);
        Assert.Equal(0, Convert.ToInt32(control.GatewayUsageMethod));
        Assert.Equal(string.Empty, control.UserName);
        Assert.Equal(string.Empty, control.Domain);
        Assert.False(control.RedirectDevices);
        Assert.Equal(new[] { "1", string.Empty }, control.SelectedMonitorsWrites);
    }

    /// <summary>
    /// The positive control for the test above: a profile that does name those things still gets
    /// them, so the invariant cannot be satisfied by writing defaults over everything.
    /// </summary>
    [Fact]
    public void AProfileThatNamesEverything_GetsWhatItNamed()
    {
        var control = new FakeControl();

        ApplyProfile(
            control,
            gateway: ProfileAGateway,
            username: ProfileAUsername,
            domain: ProfileADomain,
            usbRedirection: true,
            selectedMonitorIndices: [0, 2]);

        Assert.Equal(ProfileAGateway, control.GatewayHostname);
        Assert.Equal(1, Convert.ToInt32(control.GatewayUsageMethod));
        Assert.Equal(ProfileAUsername, control.UserName);
        Assert.Equal(ProfileADomain, control.Domain);
        Assert.True(control.RedirectDevices);
        Assert.Equal(new[] { "0,2" }, control.SelectedMonitorsWrites);
    }

    [Fact]
    public void ABlankGateway_WritesTheDirectRouteOverTheOneLeftBehind()
    {
        var control = new FakeControl();
        RdpGatewayAttestation.Apply(ProfileAGateway, control);

        RdpGatewayAttestation.Apply(null, control);

        Assert.Equal(string.Empty, control.GatewayHostname);
        Assert.Equal(0, Convert.ToInt32(control.GatewayUsageMethod));
        Assert.Equal(0, Convert.ToInt32(control.GatewayProfileUsageMethod));
    }

    /// <summary>
    /// A control that keeps the gateway after being told to connect directly would tunnel the
    /// next session through it, so the failure has to reach the caller instead of being logged.
    /// </summary>
    [Fact]
    public void AGatewayThatSurvivesTheDirectWrite_FailsAttestation()
    {
        var control = new FakeControl { RefusesToClearGateway = true };
        RdpGatewayAttestation.Apply(ProfileAGateway, control);

        RdpGatewayAttestationException exception = Assert.Throws<RdpGatewayAttestationException>(
            () => RdpGatewayAttestation.Apply(string.Empty, control));

        Assert.Equal(RdpGatewayAttestationStep.SettingsComparison, exception.Step);
        Assert.Contains(RdpGatewayAttestationProperty.GatewayHostname, exception.DivergentProperties);
        Assert.Contains(ProfileAGateway, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A control with no transport settings at all has nowhere for a gateway to have been
    /// written, so the direct case stays silent rather than failing every direct connection.
    /// </summary>
    [Fact]
    public void ABlankGatewayWithNoTransportSettings_IsNotAFailure()
    {
        RdpGatewayAttestation.Apply(null, settings: null);
    }

    [Fact]
    public void ABlankIdentity_OverwritesTheOneLeftBehind()
    {
        var control = new FakeControl();
        RdpActiveXHost.ApplyIdentitySettings(control, ProfileAUsername, ProfileADomain);

        RdpActiveXHost.ApplyIdentitySettings(control, string.Empty, null);

        Assert.Equal(string.Empty, control.UserName);
        Assert.Equal(string.Empty, control.Domain);
    }

    [Fact]
    public void ANamedIdentity_ReachesTheControl()
    {
        var control = new FakeControl();

        RdpActiveXHost.ApplyIdentitySettings(control, ProfileAUsername, ProfileADomain);

        Assert.Equal(ProfileAUsername, control.UserName);
        Assert.Equal(ProfileADomain, control.Domain);
    }

    [Fact]
    public void UsbRedirectionTurnedOff_IsWrittenRatherThanSkipped()
    {
        var control = new FakeControl();
        RdpActiveXHost.ApplyDeviceRedirection(control, usbRedirectionEnabled: true);

        RdpActiveXHost.ApplyDeviceRedirection(control, usbRedirectionEnabled: false);

        Assert.Equal(new[] { true, false }, control.RedirectDevicesWrites);
    }

    [Fact]
    public void AnEmptyMonitorSelection_IsWrittenRatherThanSkipped()
    {
        var control = new FakeControl();
        RdpActiveXHost.ApplySelectedMonitors(control, [1]);

        RdpActiveXHost.ApplySelectedMonitors(control, []);

        Assert.Equal(new[] { "1", string.Empty }, control.SelectedMonitorsWrites);
    }

    [Fact]
    public void ANamedMonitorSelection_IsWrittenAsACommaList()
    {
        var control = new FakeControl();

        RdpActiveXHost.ApplySelectedMonitors(control, [0, 1]);

        Assert.Equal(new[] { "0,1" }, control.SelectedMonitorsWrites);
    }

    /// <summary>
    /// The fullscreen retrigger resolves smart sizing off and the control has to be told: a value
    /// that is only remembered leaves the desktop scaled while the log says it is not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AResolvedDisplayContext_PushesSmartSizingToTheControl(bool smartSizingEnabled)
    {
        var session = new RdpSessionState();
        var sink = new RecordingDisplaySink();

        RdpActiveXHost.AdoptResolvedDisplayContext(
            session,
            CreateEffectiveContext(smartSizingEnabled),
            hostDpiScale: 1.5,
            sink);

        Assert.Equal(new[] { smartSizingEnabled }, sink.SmartSizingWrites);
    }

    [Fact]
    public void AResolvedDisplayContext_LandsOnTheSessionToo()
    {
        var session = new RdpSessionState();

        RdpActiveXHost.AdoptResolvedDisplayContext(
            session,
            CreateEffectiveContext(smartSizingEnabled: false),
            hostDpiScale: 1.5,
            new RecordingDisplaySink());

        Assert.Equal(3840, session.Width);
        Assert.Equal(2160, session.Height);
        Assert.Equal(1.5, session.DpiScaleX);
        Assert.Equal(1.5, session.DpiScaleY);
        Assert.True(session.Redirections.MultiMonitor);
    }

    private static EffectiveDisplayContext CreateEffectiveContext(bool smartSizingEnabled)
        => new()
        {
            ConfiguredMode = RdpResolutionMode.Auto,
            EffectiveMode = RdpResolutionMode.Fixed,
            Width = 3840,
            Height = 2160,
            DesktopScaleFactor = 100,
            DeviceScaleFactor = 100,
            SmartSizingEnabled = smartSizingEnabled,
            MultiMonitorEnabled = true,
            Reason = "test"
        };

    /// <summary>
    /// Runs the production write path for one profile against a single control, in the order
    /// <c>Connect</c> runs it.
    /// </summary>
    private static void ApplyProfile(
        FakeControl control,
        string? gateway,
        string? username,
        string? domain,
        bool usbRedirection,
        IReadOnlyList<int> selectedMonitorIndices)
    {
        RdpActiveXHost.ApplyIdentitySettings(control, username, domain);
        RdpActiveXHost.ApplyDeviceRedirection(control, usbRedirection);
        RdpActiveXHost.ApplySelectedMonitors(control, selectedMonitorIndices);
        RdpGatewayAttestation.Apply(gateway, control);
    }

    /// <summary>
    /// One object standing for the control, because that is what the defect is about: these
    /// settings live on one COM object that outlives the session that wrote them.
    /// </summary>
    private sealed class FakeControl
        : IRdpGatewayTransportSettings,
          IRdpIdentitySettings,
          IRdpDeviceRedirectionSettings,
          IRdpClientShellWriter
    {
        private const string SelectedMonitorsProperty = "selectedmonitors";

        private string _gatewayHostname = string.Empty;
        private bool _redirectDevices;

        /// <summary>
        /// Makes the control keep its gateway whatever it is told, which is the only way the
        /// direct route can fail to take effect.
        /// </summary>
        public bool RefusesToClearGateway { get; init; }

        public string GatewayHostname
        {
            get => _gatewayHostname;
            set
            {
                if (RefusesToClearGateway && string.IsNullOrEmpty(value))
                {
                    return;
                }

                _gatewayHostname = value;
            }
        }

        public object GatewayUsageMethod { get; set; } = 0;

        public object GatewayProfileUsageMethod { get; set; } = 0;

        public object GatewayCredsSource { get; set; } = 0;

        public string UserName { get; set; } = string.Empty;

        public string Domain { get; set; } = string.Empty;

        public bool RedirectDevices
        {
            get => _redirectDevices;
            set
            {
                _redirectDevices = value;
                RedirectDevicesWrites.Add(value);
            }
        }

        public List<bool> RedirectDevicesWrites { get; } = [];

        public List<string> SelectedMonitorsWrites { get; } = [];

        public bool TrySetRdpProperty(string propertyName, object value)
        {
            if (string.Equals(propertyName, SelectedMonitorsProperty, StringComparison.Ordinal))
            {
                SelectedMonitorsWrites.Add((string)value);
            }

            return true;
        }
    }

    private sealed class RecordingDisplaySink : IRdpDisplayContextSink
    {
        public List<bool> SmartSizingWrites { get; } = [];

        public void SetSmartSizing(bool enabled) => SmartSizingWrites.Add(enabled);
    }
}
