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

using System.Collections;
using System.Linq;
using System.Reflection;
using Heimdall.Core.Configuration;
using Heimdall.Rdp;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.Rdp.Tests.ActiveX;

/// <summary>
/// The reset contract that makes reusing one ActiveX control across sessions safe.
/// </summary>
/// <remarks>
/// Reuse is worth a measured 66 kernel handles per session against roughly 3, but it means
/// two profiles share one control. Anything this reset misses is inherited by the next
/// session without it ever asking, and a credential is among the things that could be
/// inherited.
/// </remarks>
public sealed class RdpSessionStateTests
{
    [Fact]
    public void Reset_RestoresEveryPropertyToItsDefault()
    {
        var state = new RdpSessionState();
        MutateEveryProperty(state);

        state.Reset();

        var pristine = new RdpSessionState();
        foreach (PropertyInfo property in SettableProperties())
        {
            Assert.True(
                ValuesMatch(property.GetValue(state), property.GetValue(pristine)),
                $"{property.Name} was not restored by Reset(): a later session would inherit it.");
        }
    }

    /// <summary>
    /// Keeps the test above honest. A property added to the state but not to the mutation
    /// helper would be compared default-against-default, so the reset test would pass
    /// while never exercising it.
    /// </summary>
    [Fact]
    public void MutationHelper_TouchesEveryProperty()
    {
        var state = new RdpSessionState();
        MutateEveryProperty(state);

        var pristine = new RdpSessionState();
        foreach (PropertyInfo property in SettableProperties())
        {
            Assert.False(
                ValuesMatch(property.GetValue(state), property.GetValue(pristine)),
                $"{property.Name} is not covered by MutateEveryProperty, so the reset test does not exercise it.");
        }
    }

    [Fact]
    public void Reset_ClearsTheCredential()
    {
        var state = new RdpSessionState
        {
            Username = "operator",
            Password = "a-secret-that-must-not-survive",
            Domain = "CORP"
        };

        state.Reset();

        Assert.Null(state.Password);
        Assert.Null(state.Domain);
        Assert.Equal(string.Empty, state.Username);
    }

    /// <summary>
    /// The redirection options are a reference, so resetting the reference is not enough:
    /// a caller holding the old instance must not be able to reach back into the state.
    /// </summary>
    [Fact]
    public void Reset_ReplacesTheRedirectionOptionsInstance()
    {
        var state = new RdpSessionState();
        RdpRedirectionOptions original = state.Redirections;
        original.Drives = true;
        original.AudioMode = 2;

        state.Reset();

        Assert.NotSame(original, state.Redirections);
        Assert.False(state.Redirections.Drives);
        Assert.Equal(0, state.Redirections.AudioMode);
    }

    private static IEnumerable<PropertyInfo> SettableProperties()
    {
        return typeof(RdpSessionState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .OrderBy(property => property.Name, StringComparer.Ordinal);
    }

    private static void MutateEveryProperty(RdpSessionState state)
    {
        state.Host = "rdp.example.invalid";
        state.Port = 4489;
        state.Username = "operator";
        state.Password = "a-secret-that-must-not-survive";
        state.Domain = "CORP";
        state.Width = 2560;
        state.Height = 1440;
        state.ColorDepth = 16;
        state.DesktopScaleFactor = 150;
        state.DeviceScaleFactor = 180;
        state.DpiScaleX = 1.5;
        state.DpiScaleY = 1.75;
        state.ResolutionMode = RdpResolutionMode.Fixed;
        state.IsFullscreen = true;
        state.ResolutionPresets = [(1280, 720), (1920, 1080)];
        state.SelectedMonitorIndices = [0, 1];
        state.Redirections = new RdpRedirectionOptions
        {
            Clipboard = false,
            Drives = true,
            Printers = true,
            ComPorts = true,
            SmartCards = true,
            Usb = true,
            Webcam = true,
            AudioCapture = true,
            AudioMode = 2
        };
        state.MaxAutoReconnectAttempts = 3;
        state.KeepAliveIntervalMs = 15_000;
    }

    private static bool ValuesMatch(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is RdpRedirectionOptions leftRedirections
            && right is RdpRedirectionOptions rightRedirections)
        {
            return typeof(RdpRedirectionOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.CanWrite)
                .All(property => Equals(
                    property.GetValue(leftRedirections),
                    property.GetValue(rightRedirections)));
        }

        if (left is IEnumerable leftItems and not string
            && right is IEnumerable rightItems and not string)
        {
            return leftItems.Cast<object>().SequenceEqual(rightItems.Cast<object>());
        }

        return Equals(left, right);
    }
}
