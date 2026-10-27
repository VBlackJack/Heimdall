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

using System.IO;
using Heimdall.Core.Updates;
using Microsoft.Web.WebView2.Core;

namespace Heimdall.App.Services;

/// <summary>
/// Centralizes WebView2 runtime detection and environment creation.
/// Supports three resolution strategies in priority order:
/// 1. Bundled Fixed Version Runtime in <c>runtimes/webview2/</c>, SelfContained edition only
/// 2. System-wide Evergreen Runtime (Microsoft Edge or standalone installer)
/// 3. Unavailable - caller should show fallback UI
/// </summary>
public static class WebView2Helper
{
    private const string RuntimeExecutableName = "msedgewebview2.exe";

    private static readonly Lazy<string?> BundledRuntimeFolder = new(
        () => BundledRuntimeFolderFor(new VariantDetector().Detect(), AppContext.BaseDirectory, File.Exists));

    private static bool? _isAvailable;

    /// <summary>
    /// Returns true if any WebView2 runtime (bundled or system) is available.
    /// Result is cached after first call.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            _isAvailable ??= CheckAvailability();
            return _isAvailable.Value;
        }
    }

    /// <summary>
    /// Returns true if using the bundled Fixed Version Runtime.
    /// </summary>
    public static bool IsBundled => BundledRuntimeFolder.Value is not null;

    /// <summary>
    /// The folder to hand WebView2 as its browser executable folder, or null for the
    /// system Evergreen Runtime.
    /// </summary>
    /// <remarks>
    /// Only the SelfContained edition uses the bundled runtime. The folder's presence alone
    /// used to decide, and it survives a switch of edition: a Standard install over a
    /// SelfContained one kept <c>runtimes\webview2</c>, so the Standard build kept running
    /// on a Fixed Version Runtime nothing would ever update, instead of the Evergreen one it
    /// is built for.
    /// </remarks>
    internal static string? BundledRuntimeFolderFor(
        BuildVariant edition,
        string baseDirectory,
        Func<string, bool> fileExists)
    {
        if (edition != BuildVariant.SelfContained)
        {
            return null;
        }

        string folder = Path.Combine(baseDirectory, "runtimes", "webview2");
        return fileExists(Path.Combine(folder, RuntimeExecutableName)) ? folder : null;
    }

    /// <summary>
    /// Creates a <see cref="CoreWebView2Environment"/> using the best available runtime.
    /// Uses the bundled Fixed Version Runtime if present, otherwise falls back to system Evergreen.
    /// </summary>
    /// <param name="userDataSubfolder">
    /// Subfolder name under <c>%TEMP%/Heimdall/WebView2/</c> for profile isolation.
    /// </param>
    public static async Task<CoreWebView2Environment> CreateEnvironmentAsync(
        string userDataSubfolder = "Default")
    {
        var userDataFolder = Path.Combine(
            Path.GetTempPath(), "Heimdall", "WebView2", userDataSubfolder);

        string? browserExecutableFolder = BundledRuntimeFolder.Value;
        var options = new CoreWebView2EnvironmentOptions(
            additionalBrowserArguments: EmbeddedBrowserArguments(null));

        return await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: browserExecutableFolder,
            userDataFolder: userDataFolder,
            options: options)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The switch every embedded view runs with: the pointer stays visible while the user types.
    /// </summary>
    internal const string HideCursorWhileTypingOff = "--disable-features=HideCursorWhileTyping";

    /// <summary>
    /// The browser arguments of one embedded view: the app-wide switch, then the route's own.
    /// </summary>
    /// <remarks>
    /// Chromium hides the pointer while the user types when Windows asks for it
    /// (<c>HideCursorWhileTyping</c>, on by default), and shows it again only when a mouse event
    /// reaches the same window that hid it. A view closed while its pointer was hidden therefore
    /// left the window without one, and no code on this side could bring it back: the counter that
    /// hides the pointer belongs to the browser's own thread. That is what happened between runtime
    /// 152 and 154, and the Fixed Version Runtime bundled with the Self-Contained edition never
    /// updates out of it. The feature buys a pointer that follows the typing and costs a window the
    /// user cannot point at, so every view turns it off.
    /// </remarks>
    internal static string EmbeddedBrowserArguments(string? routeArguments)
        => string.IsNullOrWhiteSpace(routeArguments)
            ? HideCursorWhileTypingOff
            : $"{HideCursorWhileTypingOff} {routeArguments}";

    /// <summary>Sub-folder of the data directory holding the web consoles' browser profile.</summary>
    internal const string WebConsoleProfileFolder = "WebView2-Web";

    private static readonly object WebConsoleEnvironmentLock = new();

    /// <summary>One environment per route: the direct one under the empty key, one per gateway.</summary>
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> WebConsoleEnvironments =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The one browser environment every web console tab shares, so a login made in one tab
    /// holds in the others and survives a restart.
    /// </summary>
    /// <remarks>
    /// Kept under the data directory rather than <c>%TEMP%</c>, where the other embedded views
    /// keep theirs: those hold no state worth keeping, while this one holds the cookies of every
    /// console, and a disk cleanup emptying the temporary folder would sign the user out of all
    /// of them. A failed creation is not cached, so the next tab tries again.
    ///
    /// A console behind an SSH gateway gets an environment of its own, per gateway, whose browser
    /// goes through the SOCKS proxy that gateway's tunnel opens: the console's real name reaches
    /// the browser, so its certificate, its pin, its redirects and a name only the admin network
    /// resolves all work. A proxy is a browser-process setting, and one profile folder can only
    /// serve one browser process at a time, hence one folder per gateway: a sign-in made directly
    /// is not shared with the same console reached through a gateway.
    /// </remarks>
    public static Task<CoreWebView2Environment> GetWebConsoleEnvironmentAsync(
        string? gatewayId = null,
        int? proxyPort = null)
    {
        string key = WebConsoleRouteKey(gatewayId, proxyPort);
        lock (WebConsoleEnvironmentLock)
        {
            if (WebConsoleEnvironments.TryGetValue(key, out Task<CoreWebView2Environment>? existing)
                && existing is { IsFaulted: false, IsCanceled: false })
            {
                return existing;
            }

            string userDataFolder = Path.Combine(
                Core.Configuration.ApplicationDataPathResolver.Resolve(),
                WebConsoleProfileFolderFor(gatewayId, proxyPort));
            string? browserExecutableFolder = BundledRuntimeFolder.Value;
            string? arguments = WebConsoleBrowserArguments(proxyPort);
            CoreWebView2EnvironmentOptions options = new(
                additionalBrowserArguments: EmbeddedBrowserArguments(arguments));
            Task<CoreWebView2Environment> created = CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: browserExecutableFolder,
                userDataFolder: userDataFolder,
                options: options);
            WebConsoleEnvironments[key] = created;
            return created;
        }
    }

    /// <summary>The cache key of a route: empty for direct, the gateway and its port otherwise.</summary>
    internal static string WebConsoleRouteKey(string? gatewayId, int? proxyPort)
        => string.IsNullOrWhiteSpace(gatewayId) || proxyPort is not > 0
            ? string.Empty
            : $"{gatewayId}:{proxyPort}";

    /// <summary>
    /// The profile folder of a route. The gateway identifier is a GUID in practice; anything that
    /// is not a letter, a digit or a dash is dropped so a hand-edited one cannot leave the folder.
    /// </summary>
    internal static string WebConsoleProfileFolderFor(string? gatewayId, int? proxyPort)
    {
        if (string.IsNullOrWhiteSpace(gatewayId) || proxyPort is not > 0)
        {
            return WebConsoleProfileFolder;
        }

        string safe = new(gatewayId.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        return safe.Length == 0 ? WebConsoleProfileFolder + "-gw" : $"{WebConsoleProfileFolder}-gw-{safe}";
    }

    /// <summary>
    /// The browser arguments of a route. SOCKS5 through Chromium hands host names to the proxy,
    /// so a console name the gateway's network resolves needs no DNS on this machine.
    /// </summary>
    internal static string? WebConsoleBrowserArguments(int? proxyPort)
        => proxyPort is > 0
            ? $"--proxy-server=socks5://127.0.0.1:{proxyPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : null;

    private static bool CheckAvailability()
    {
        // Check bundled Fixed Version Runtime first
        if (IsBundled)
        {
            Core.Logging.FileLogger.Info(
                $"WebView2 Fixed Version Runtime found at: {BundledRuntimeFolder.Value}");
            return true;
        }

        // Check system Evergreen Runtime
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (!string.IsNullOrEmpty(version))
            {
                Core.Logging.FileLogger.Info($"WebView2 Evergreen Runtime found: {version}");
                return true;
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // Expected when no runtime is installed
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"WebView2 detection error: {ex.Message}");
        }

        Core.Logging.FileLogger.Warn(
            "WebView2 Runtime not found. Embedded terminal and VNC sessions will be unavailable. "
            + "Place a Fixed Version Runtime in runtimes/webview2/ or install the Evergreen Runtime.");
        return false;
    }
}
