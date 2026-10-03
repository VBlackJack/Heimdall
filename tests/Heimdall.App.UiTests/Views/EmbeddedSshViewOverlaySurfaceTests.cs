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
using System.Windows;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// The SSH terminal surface steps aside whenever one of its veils is up.
/// </summary>
/// <remarks>
/// The terminal is a WebView2, a native window that covers every WPF element drawn over it. On
/// v2026.100201 a clean exit and a server-side kill both set the end-of-session message visible,
/// and the screen showed the bare terminal: View output, Edit profile and Copy error could not be
/// reached. These run the real view and read the surface's visibility at each step.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
public sealed class EmbeddedSshViewOverlaySurfaceTests
{
    [StaFact]
    public void TheSurfaceIsHiddenUnderEachVeil_AndBackForViewOutput()
    {
        WpfTestHost.Invoke(() =>
        {
            using EmbeddedSshView view = new();
            FrameworkElement surface = Element(view, "TerminalWebView");

            // The view opens under its connecting veil.
            Assert.Equal(Visibility.Visible, Element(view, "ConnectingOverlay").Visibility);
            Assert.Equal(Visibility.Hidden, surface.Visibility);

            Invoke(view, "HideConnectingOverlay");
            Assert.Equal(Visibility.Visible, surface.Visibility);

            Invoke(view, "ShowReconnectOverlay");
            Assert.Equal(Visibility.Visible, Element(view, "ReconnectOverlay").Visibility);
            Assert.Equal(Visibility.Hidden, surface.Visibility);

            Invoke(view, "OnOverlayViewOutputClick", view, new RoutedEventArgs());
            Assert.Equal(Visibility.Collapsed, Element(view, "ReconnectOverlay").Visibility);
            Assert.Equal(Visibility.Visible, surface.Visibility);
        });
    }

    [StaFact]
    public void TheFallbackPanelKeepsTheSurfaceCollapsed_WhateverTheVeils()
    {
        WpfTestHost.Invoke(() =>
        {
            using EmbeddedSshView view = new();
            FrameworkElement surface = Element(view, "TerminalWebView");

            Invoke(view, "ShowWebViewUnavailable", "no runtime");
            Assert.Equal(Visibility.Collapsed, surface.Visibility);

            Invoke(view, "ShowReconnectOverlay");
            Invoke(view, "HideReconnectOverlay");
            Assert.Equal(Visibility.Collapsed, surface.Visibility);
        });
    }

    private static FrameworkElement Element(EmbeddedSshView view, string name)
        => Assert.IsAssignableFrom<FrameworkElement>(view.FindName(name));

    private static void Invoke(EmbeddedSshView view, string methodName, params object[] arguments)
    {
        MethodInfo method = typeof(EmbeddedSshView).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found.");
        method.Invoke(view, arguments);
    }
}
