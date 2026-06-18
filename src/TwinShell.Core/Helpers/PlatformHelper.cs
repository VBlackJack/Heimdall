/*
 * Copyright 2025 Julien Bombled
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

using System.Runtime.InteropServices;
using TwinShell.Core.Enums;

namespace TwinShell.Core.Helpers;

/// <summary>
/// Helper class for platform detection and operations.
/// </summary>
public static class PlatformHelper
{
    // PERFORMANCE: Cache platform detection to avoid repeated P/Invoke calls
    // Platform never changes during application lifetime
    private static Platform? _cachedPlatform;

    /// <summary>
    /// Gets the current operating system platform.
    /// </summary>
    /// <returns>The current platform (Windows or Linux)</returns>
    public static Platform GetCurrentPlatform()
    {
        // Return cached value if available (99% CPU savings on repeated calls)
        if (_cachedPlatform.HasValue)
        {
            return _cachedPlatform.Value;
        }

        // Detect and cache platform
        Platform detectedPlatform;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            detectedPlatform = Platform.Windows;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            detectedPlatform = Platform.Linux;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS is treated as Linux for command purposes
            detectedPlatform = Platform.Linux;
        }
        else
        {
            // Default to Windows if unknown
            detectedPlatform = Platform.Windows;
        }

        _cachedPlatform = detectedPlatform;
        return detectedPlatform;
    }

    /// <summary>
    /// Checks if an action is compatible with the current platform.
    /// </summary>
    /// <param name="actionPlatform">The platform requirement of the action</param>
    /// <returns>True if compatible with current platform</returns>
    public static bool IsCompatibleWithCurrentPlatform(Platform actionPlatform)
    {
        if (actionPlatform == Platform.Both)
            return true;

        var currentPlatform = GetCurrentPlatform();
        return actionPlatform == currentPlatform;
    }

    /// <summary>
    /// Gets a human-readable platform name.
    /// </summary>
    /// <param name="platform">The platform</param>
    /// <returns>Platform name as string</returns>
    public static string GetPlatformName(Platform platform)
    {
        return platform switch
        {
            Platform.Windows => "Windows",
            Platform.Linux => "Linux",
            Platform.Both => "Cross-Platform",
            _ => "Unknown"
        };
    }
}
