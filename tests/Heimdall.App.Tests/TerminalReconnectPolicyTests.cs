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

using Heimdall.App.Services;
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class TerminalReconnectPolicyTests
{
    [Fact]
    public void ClassifyProcessExit_ZeroExit_ReturnsCleanDisconnect()
    {
        SshSessionDisconnectInfo disconnect = TerminalReconnectPolicy.ClassifyProcessExit(
            exitCode: 0,
            autoReconnectOnProcessExit: false);

        Assert.True(disconnect.IsClean);
        Assert.False(disconnect.SuppressAutoReconnect);
        Assert.Equal("Process exited with code 0", disconnect.Message);
    }

    [Fact]
    public void ClassifyProcessExit_NonZeroExitWithAutoReconnect_ReturnsUnclassifiedDisconnect()
    {
        SshSessionDisconnectInfo disconnect = TerminalReconnectPolicy.ClassifyProcessExit(
            exitCode: 1,
            autoReconnectOnProcessExit: true);

        Assert.False(disconnect.IsClean);
        Assert.False(disconnect.SuppressAutoReconnect);
        Assert.Equal("Process exited with code 1", disconnect.Message);
    }

    [Fact]
    public void ClassifyProcessExit_NonZeroExitWithoutAutoReconnect_ReturnsSuppressedDisconnect()
    {
        SshSessionDisconnectInfo disconnect = TerminalReconnectPolicy.ClassifyProcessExit(
            exitCode: 1,
            autoReconnectOnProcessExit: false);

        Assert.False(disconnect.IsClean);
        Assert.True(disconnect.SuppressAutoReconnect);
        Assert.Equal("Process exited with code 1", disconnect.Message);
    }

    [Fact]
    public void ClassifyProcessExit_NonZeroExitWithSuppression_ReturnsSuppressedDisconnect()
    {
        SshSessionDisconnectInfo disconnect = TerminalReconnectPolicy.ClassifyProcessExit(
            exitCode: 1,
            autoReconnectOnProcessExit: true,
            suppressAutoReconnect: true);

        Assert.False(disconnect.IsClean);
        Assert.True(disconnect.SuppressAutoReconnect);
        Assert.Equal("Process exited with code 1", disconnect.Message);
    }

    [Theory]
    [InlineData("WINRM")]
    [InlineData("winrm")]
    [InlineData("LOCAL")]
    [InlineData("local")]
    public void ReconnectsOnProcessExit_LocalOrHandedOffProcessProtocols_ReturnsFalse(
        string connectionType)
    {
        bool reconnects = TerminalReconnectPolicy.ReconnectsOnProcessExit(connectionType);

        Assert.False(reconnects);
    }

    [Theory]
    [InlineData("SSH")]
    [InlineData("TELNET")]
    [InlineData("UNKNOWN")]
    [InlineData(null)]
    public void ReconnectsOnProcessExit_NetworkOrUnknownProtocols_ReturnsTrue(
        string? connectionType)
    {
        bool reconnects = TerminalReconnectPolicy.ReconnectsOnProcessExit(connectionType);

        Assert.True(reconnects);
    }

    [Fact]
    public void SuppressesConnectTimeProcessExit_SshPipeNoInputWithinWindow_ReturnsTrue()
    {
        bool suppresses = TerminalReconnectPolicy.SuppressesConnectTimeProcessExit(
            connectionType: "SSH",
            isPipeModeSession: true,
            hasTerminalInput: false,
            processRuntime: TerminalReconnectPolicy.ConnectTimeExitWindow);

        Assert.True(suppresses);
    }

    [Fact]
    public void SuppressesConnectTimeProcessExit_SshPipeWithInput_ReturnsFalse()
    {
        bool suppresses = TerminalReconnectPolicy.SuppressesConnectTimeProcessExit(
            connectionType: "SSH",
            isPipeModeSession: true,
            hasTerminalInput: true,
            processRuntime: TimeSpan.FromSeconds(1));

        Assert.False(suppresses);
    }

    [Fact]
    public void SuppressesConnectTimeProcessExit_SshPipeOutsideWindow_ReturnsFalse()
    {
        bool suppresses = TerminalReconnectPolicy.SuppressesConnectTimeProcessExit(
            connectionType: "SSH",
            isPipeModeSession: true,
            hasTerminalInput: false,
            processRuntime: TerminalReconnectPolicy.ConnectTimeExitWindow + TimeSpan.FromMilliseconds(1));

        Assert.False(suppresses);
    }

    [Fact]
    public void SuppressesConnectTimeProcessExit_CustomWindow_IsUsed()
    {
        bool suppresses = TerminalReconnectPolicy.SuppressesConnectTimeProcessExit(
            connectionType: "SSH",
            isPipeModeSession: true,
            hasTerminalInput: false,
            processRuntime: TimeSpan.FromSeconds(20),
            connectTimeExitWindow: TimeSpan.FromSeconds(30));

        Assert.True(suppresses);
    }

    [Fact]
    public void ResolveConnectTimeExitWindow_Null_ReturnsFallback()
    {
        TimeSpan window = TerminalReconnectPolicy.ResolveConnectTimeExitWindow(null);

        Assert.Equal(TerminalReconnectPolicy.ConnectTimeExitWindow, window);
    }

    [Fact]
    public void ResolveConnectTimeExitWindow_ConfiguredSeconds_ReturnsConfiguredWindow()
    {
        TimeSpan window = TerminalReconnectPolicy.ResolveConnectTimeExitWindow(0);

        Assert.Equal(TimeSpan.Zero, window);
    }

    [Fact]
    public void ComputeAutoReconnectDelaySeconds_NoSettings_UsesLegacyDelays()
    {
        Assert.Equal(2, EmbeddedSshView.ComputeAutoReconnectDelaySeconds(null, 1));
        Assert.Equal(5, EmbeddedSshView.ComputeAutoReconnectDelaySeconds(null, 2));
        Assert.Equal(15, EmbeddedSshView.ComputeAutoReconnectDelaySeconds(null, 3));
    }

    [Fact]
    public void ComputeAutoReconnectDelaySeconds_Settings_UsesConfiguredDelays()
    {
        AppSettings settings = new()
        {
            SshAutoReconnectFirstDelaySeconds = 4,
            SshAutoReconnectSecondDelaySeconds = 8,
            SshAutoReconnectSubsequentDelaySeconds = 16
        };

        Assert.Equal(4, EmbeddedSshView.ComputeAutoReconnectDelaySeconds(settings, 1));
        Assert.Equal(8, EmbeddedSshView.ComputeAutoReconnectDelaySeconds(settings, 2));
        Assert.Equal(16, EmbeddedSshView.ComputeAutoReconnectDelaySeconds(settings, 3));
    }

    [Fact]
    public void AutoReconnectTickScheduler_CurrentCallback_RunsTickExactlyOnce()
    {
        AutoReconnectTickScheduler scheduler = new();
        List<Action> queuedActions = [];
        int tickCount = 0;
        TimerCallback callback = scheduler.CreateTimerCallback(
            queuedActions.Add,
            () => tickCount++);

        callback(null);
        Assert.Single(queuedActions);

        queuedActions[0]();

        Assert.Equal(1, tickCount);
    }

    [Fact]
    public void AutoReconnectTickScheduler_InvalidatedAfterQueue_DropsQueuedTick()
    {
        AutoReconnectTickScheduler scheduler = new();
        List<Action> queuedActions = [];
        int tickCount = 0;
        TimerCallback callback = scheduler.CreateTimerCallback(
            queuedActions.Add,
            () => tickCount++);

        callback(null);
        Assert.Single(queuedActions);

        scheduler.Invalidate();
        queuedActions[0]();

        Assert.Equal(0, tickCount);
    }

    [Fact]
    public void AutoReconnectTickScheduler_NewGeneration_DropsOldTickAndRunsNewTick()
    {
        AutoReconnectTickScheduler scheduler = new();
        List<Action> queuedActions = [];
        int tickCount = 0;
        TimerCallback oldCallback = scheduler.CreateTimerCallback(
            queuedActions.Add,
            () => tickCount++);

        oldCallback(null);
        scheduler.Invalidate();
        TimerCallback currentCallback = scheduler.CreateTimerCallback(
            queuedActions.Add,
            () => tickCount++);
        currentCallback(null);

        Assert.Equal(2, queuedActions.Count);
        queuedActions[0]();
        queuedActions[1]();

        Assert.Equal(1, tickCount);
    }

    [Fact]
    public void AutoReconnectTickScheduler_RepeatedInvalidation_DropsQueuedTick()
    {
        AutoReconnectTickScheduler scheduler = new();
        List<Action> queuedActions = [];
        int tickCount = 0;
        TimerCallback callback = scheduler.CreateTimerCallback(
            queuedActions.Add,
            () => tickCount++);

        callback(null);
        scheduler.Invalidate();
        scheduler.Invalidate();
        queuedActions[0]();

        Assert.Equal(0, tickCount);
    }

    [Fact]
    public void StopAutoReconnectTimer_NullTimer_InvalidatesQueuedTick()
    {
        AutoReconnectTickScheduler scheduler = new();
        List<Action> queuedActions = [];
        int tickCount = 0;
        TimerCallback callback = scheduler.CreateTimerCallback(
            queuedActions.Add,
            () => tickCount++);
        System.Threading.Timer? timer = null;

        callback(null);
        EmbeddedSshView.StopAutoReconnectTimer(scheduler, ref timer);
        queuedActions[0]();

        Assert.Equal(0, tickCount);
        Assert.Null(timer);
    }

    [Fact]
    public void AutoReconnectTickScheduler_OldCallbackQueuesAfterNewStart_DropsLateTick()
    {
        AutoReconnectTickScheduler scheduler = new();
        List<Action> queuedActions = [];
        int tickCount = 0;
        TimerCallback oldCallback = scheduler.CreateTimerCallback(
            queuedActions.Add,
            () => tickCount++);

        scheduler.Invalidate();
        TimerCallback currentCallback = scheduler.CreateTimerCallback(
            queuedActions.Add,
            () => tickCount++);
        oldCallback(null);
        currentCallback(null);

        Assert.Equal(2, queuedActions.Count);
        queuedActions[0]();
        queuedActions[1]();

        Assert.Equal(1, tickCount);
    }

    [Theory]
    [InlineData("TELNET")]
    [InlineData("LOCAL")]
    [InlineData("WINRM")]
    [InlineData(null)]
    public void SuppressesConnectTimeProcessExit_NonSshConnection_ReturnsFalse(
        string? connectionType)
    {
        bool suppresses = TerminalReconnectPolicy.SuppressesConnectTimeProcessExit(
            connectionType,
            isPipeModeSession: true,
            hasTerminalInput: false,
            processRuntime: TimeSpan.FromSeconds(1));

        Assert.False(suppresses);
    }
}
