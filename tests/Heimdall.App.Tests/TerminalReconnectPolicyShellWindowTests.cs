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

namespace Heimdall.App.Tests;

/// <summary>
/// A shell session that ends right after it was attached, before the user typed, is not
/// retried: every attach resets the attempt count, so a device that closes at once would
/// otherwise be reconnected forever at the shortest delay.
/// </summary>
public sealed class TerminalReconnectPolicyShellWindowTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(15)]
    public void DisconnectWithinConnectWindow_DoesNotAutoReconnect(int secondsSinceAttach)
    {
        Assert.True(TerminalReconnectPolicy.SuppressesConnectTimeShellDisconnect(
            hasTerminalInput: false,
            TimeSpan.FromSeconds(secondsSinceAttach),
            Window));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(3600)]
    public void DisconnectAfterConnectWindow_StillAutoReconnects(int secondsSinceAttach)
    {
        Assert.False(TerminalReconnectPolicy.SuppressesConnectTimeShellDisconnect(
            hasTerminalInput: false,
            TimeSpan.FromSeconds(secondsSinceAttach),
            Window));
    }

    [Fact]
    public void DisconnectAfterUserInput_IsUnaffectedByTheWindow()
    {
        Assert.False(TerminalReconnectPolicy.SuppressesConnectTimeShellDisconnect(
            hasTerminalInput: true,
            TimeSpan.FromSeconds(1),
            Window));
    }

    [Fact]
    public void DisconnectWindow_FollowsTheConfiguredValue()
    {
        TimeSpan configured = TerminalReconnectPolicy.ResolveConnectTimeExitWindow(5);

        Assert.True(TerminalReconnectPolicy.SuppressesConnectTimeShellDisconnect(false, TimeSpan.FromSeconds(5), configured));
        Assert.False(TerminalReconnectPolicy.SuppressesConnectTimeShellDisconnect(false, TimeSpan.FromSeconds(6), configured));
    }

    [Fact]
    public void DefaultWindow_IsTheSharedConnectTimeWindow()
    {
        Assert.True(TerminalReconnectPolicy.SuppressesConnectTimeShellDisconnect(
            false,
            TerminalReconnectPolicy.ConnectTimeExitWindow));
        Assert.False(TerminalReconnectPolicy.SuppressesConnectTimeShellDisconnect(
            false,
            TerminalReconnectPolicy.ConnectTimeExitWindow + TimeSpan.FromSeconds(1)));
    }
}
