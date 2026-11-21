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

using Microsoft.Web.WebView2.Core;

namespace Heimdall.App.Services;

/// <summary>
/// The WebView2 settings and event rules every embedded surface shares, applied in one place so a
/// new host cannot forget one of them.
/// </summary>
/// <remarks>
/// <para>It sets only what no host wants otherwise: developer tools off, general autofill and
/// password autosave off, host objects refused, every external-protocol launch cancelled, and
/// every popup claimed so the browser never opens a bare window of its own. What a host owns
/// (context menus, accelerators, zoom, script dialogs, what a popup turns into) stays with the
/// host.</para>
/// <para>A setting or event newer than the installed runtime is skipped and logged rather than
/// allowed to stop the surface from starting.</para>
/// </remarks>
public static class WebView2Lockdown
{
    /// <summary>Applies the shared rules to <paramref name="core"/>.</summary>
    /// <param name="core">The initialised browser core.</param>
    /// <param name="onNewWindow">
    /// What the host does with a popup, called after the request is marked handled; null drops it.
    /// </param>
    /// <returns>A handle that unsubscribes the events; the host disposes it with itself.</returns>
    public static IDisposable Apply(
        CoreWebView2 core,
        Action<CoreWebView2NewWindowRequestedEventArgs>? onNewWindow)
    {
        ArgumentNullException.ThrowIfNull(core);

        TrySet("developer tools", () => core.Settings.AreDevToolsEnabled = false);
        TrySet("general autofill", () => core.Settings.IsGeneralAutofillEnabled = false);
        TrySet("password autosave", () => core.Settings.IsPasswordAutosaveEnabled = false);
        TrySet("host objects", () => core.Settings.AreHostObjectsAllowed = false);

        var unsubscribe = new List<Action>();

        void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            onNewWindow?.Invoke(e);
        }

        void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
        {
            e.Cancel = true;
            Core.Logging.FileLogger.Warn("WebView2 blocked an external protocol launch.");
        }

        core.NewWindowRequested += OnNewWindowRequested;
        unsubscribe.Add(() => core.NewWindowRequested -= OnNewWindowRequested);

        try
        {
            core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
            unsubscribe.Add(() => core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme);
        }
        catch (Exception ex) when (IsRuntimeTooOld(ex))
        {
            Core.Logging.FileLogger.Warn(
                $"WebView2 runtime too old to block external protocol launches: {ex.Message}");
        }

        return new Subscription(unsubscribe);
    }

    /// <summary>Whether the exception means the installed runtime lacks the feature.</summary>
    internal static bool IsRuntimeTooOld(Exception ex) =>
        ex is NotImplementedException or InvalidCastException;

    private static void TrySet(string what, Action set)
    {
        try
        {
            set();
        }
        catch (Exception ex) when (IsRuntimeTooOld(ex))
        {
            Core.Logging.FileLogger.Warn($"WebView2 runtime too old to set {what}: {ex.Message}");
        }
    }

    /// <summary>Runs the unsubscribe actions once, whatever the number of disposals.</summary>
    internal sealed class Subscription : IDisposable
    {
        private List<Action>? _actions;

        internal Subscription(List<Action> actions) => _actions = actions;

        public void Dispose()
        {
            List<Action>? actions = Interlocked.Exchange(ref _actions, null);
            if (actions is null)
            {
                return;
            }

            foreach (Action action in actions)
            {
                try
                {
                    action();
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException
                    or System.Runtime.InteropServices.COMException)
                {
                    // The browser may already be gone; nothing is left to unsubscribe from.
                }
            }
        }
    }
}
