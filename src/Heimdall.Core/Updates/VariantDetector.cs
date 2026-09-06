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

using Microsoft.Win32;

namespace Heimdall.Core.Updates;

/// <summary>
/// Determines the running build variant by probing for the bundled WebView2
/// runtime marker, and whether the running copy is one the installer put there.
/// The base directory and both probes are injectable so the detector can be
/// exercised without touching the real filesystem or registry.
/// </summary>
public sealed class VariantDetector : IVariantDetector
{
    private const string MarkerRelativePath = @"runtimes\webview2\msedgewebview2.exe";

    private readonly string _baseDirectory;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<IReadOnlyList<string>> _readInstallLocations;

    /// <param name="baseDirectory">Where the running copy lives; the application base directory by default.</param>
    /// <param name="fileExists">The file probe; the real filesystem by default.</param>
    /// <param name="readInstallLocations">
    /// Yields every install location the installer registered, in any hive; the
    /// Windows registry by default. A copy that appears in none of them was not
    /// installed by the installer.
    /// </param>
    public VariantDetector(
        string? baseDirectory = null,
        Func<string, bool>? fileExists = null,
        Func<IReadOnlyList<string>>? readInstallLocations = null)
    {
        _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
        _fileExists = fileExists ?? File.Exists;
        _readInstallLocations = readInstallLocations ?? ReadRegisteredInstallLocations;
    }

    public BuildVariant Detect()
    {
        string markerPath = Path.Combine(_baseDirectory, MarkerRelativePath);
        return _fileExists(markerPath) ? BuildVariant.SelfContained : BuildVariant.Standard;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The WebView2 marker separates the standard build from the self-contained one;
    /// it says nothing about how the copy got there. A portable archive extracted
    /// anywhere, an MSI deployment, and a build run from its output directory all
    /// look like an installed copy to the marker probe, and offering them the Inno
    /// installer installs a second copy elsewhere, relaunches the old one, and
    /// reports "did not apply" on every launch from then on. Only the installer's own
    /// registration can say that the installer owns this directory.
    /// </remarks>
    public bool IsInstalledInPlace()
    {
        string baseDirectory = NormalizeDirectory(_baseDirectory);
        foreach (string location in _readInstallLocations())
        {
            if (string.Equals(NormalizeDirectory(location), baseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the install location Inno Setup recorded, from both the per-user and the
    /// per-machine hive: <c>PrivilegesRequired=lowest</c> writes to whichever the user
    /// chose at install time. Never throws; a registry that cannot be read reports no
    /// registration, which is the fail-closed answer.
    /// </summary>
    private static IReadOnlyList<string> ReadRegisteredInstallLocations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        List<string> locations = [];
        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using RegistryKey? key = hive.OpenSubKey(UpdateSource.UninstallKeyPath, writable: false);
                if (key?.GetValue(UpdateSource.InstallLocationValueName) is string location
                    && !string.IsNullOrWhiteSpace(location))
                {
                    locations.Add(location);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                Logging.FileLogger.Warn($"Update install registration unreadable under {hive.Name}: {ex.Message}");
            }
        }

        return locations;
    }

    private static string NormalizeDirectory(string directory)
    {
        string full;
        try
        {
            full = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return directory;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
