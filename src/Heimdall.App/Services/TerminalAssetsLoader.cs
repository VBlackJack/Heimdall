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
using System.Threading;

namespace Heimdall.App.Services;

/// <summary>
/// Provides process-wide cached access to the static terminal HTML, JavaScript,
/// and stylesheet assets used by the embedded terminal.
/// </summary>
internal static class TerminalAssetsLoader
{
    private static readonly Lazy<string> _terminalHtml = CreateLazyAsset("terminal.html");
    private static readonly Lazy<string> _xtermCss = CreateLazyAsset(Path.Combine("Terminal", "xterm.min.css"));
    private static readonly Lazy<string> _xtermJs = CreateLazyAsset(Path.Combine("Terminal", "xterm.min.js"));
    private static readonly Lazy<string> _addonFitJs = CreateLazyAsset(Path.Combine("Terminal", "addon-fit.min.js"));
    private static readonly Lazy<string> _addonWebglJs = CreateLazyAsset(Path.Combine("Terminal", "addon-webgl.min.js"));

    /// <summary>
    /// Gets the cached terminal HTML shell used for <c>NavigateToString</c>.
    /// </summary>
    public static string TerminalHtml => _terminalHtml.Value;

    /// <summary>
    /// Gets the cached xterm.js stylesheet content.
    /// </summary>
    public static string XtermCss => _xtermCss.Value;

    /// <summary>
    /// Gets the cached xterm.js runtime script content.
    /// </summary>
    public static string XtermJs => _xtermJs.Value;

    /// <summary>
    /// Gets the cached xterm.js fit addon script content.
    /// </summary>
    public static string AddonFitJs => _addonFitJs.Value;

    /// <summary>
    /// Gets the cached xterm.js WebGL addon script content.
    /// </summary>
    public static string AddonWebglJs => _addonWebglJs.Value;

    internal static Lazy<string> CreateLazyAsset(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ValidateRelativeAssetPath(relativePath);

        return new Lazy<string>(
            () => LoadAsset(relativePath),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static void ValidateRelativeAssetPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                "Terminal asset path must be relative.",
                nameof(relativePath));
        }

        if (relativePath.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Terminal asset path cannot contain parent-directory traversal.",
                nameof(relativePath));
        }
    }

    private static string LoadAsset(string relativePath)
    {
        string fullPath = Path.Combine(AppContext.BaseDirectory, "Assets", relativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Terminal asset '{relativePath}' not found at '{fullPath}' - installation corrupt",
                fullPath);
        }

        return File.ReadAllText(fullPath);
    }
}
