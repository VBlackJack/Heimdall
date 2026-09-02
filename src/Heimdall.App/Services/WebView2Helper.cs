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
using Microsoft.Web.WebView2.Core;

namespace Heimdall.App.Services;

/// <summary>
/// Centralizes WebView2 runtime detection and environment creation.
/// Supports three resolution strategies in priority order:
/// 1. Bundled Fixed Version Runtime in <c>runtimes/webview2/</c> (fully portable)
/// 2. System-wide Evergreen Runtime (Microsoft Edge or standalone installer)
/// 3. Unavailable - caller should show fallback UI
/// </summary>
public static class WebView2Helper
{
    private static readonly string BundledRuntimePath =
        Path.Combine(AppContext.BaseDirectory, "runtimes", "webview2");

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
    public static bool IsBundled => Directory.Exists(BundledRuntimePath)
        && File.Exists(Path.Combine(BundledRuntimePath, "msedgewebview2.exe"));

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

        string? browserExecutableFolder = IsBundled ? BundledRuntimePath : null;

        return await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: browserExecutableFolder,
            userDataFolder: userDataFolder)
            .ConfigureAwait(false);
    }

    private static bool CheckAvailability()
    {
        // Check bundled Fixed Version Runtime first
        if (IsBundled)
        {
            Core.Logging.FileLogger.Info(
                $"WebView2 Fixed Version Runtime found at: {BundledRuntimePath}");
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
