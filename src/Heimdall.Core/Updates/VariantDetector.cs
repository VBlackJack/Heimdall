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
using Microsoft.Win32;

namespace Heimdall.Core.Updates;

/// <summary>
/// Determines the running build variant from the edition the build stamped into the
/// executable, falling back to the bundled WebView2 runtime marker for a build that
/// carries no stamp, and whether the running copy is one the installer put there.
/// The base directory, the stamp and both probes are injectable so the detector can
/// be exercised without touching the real filesystem, registry or entry assembly.
/// </summary>
public sealed class VariantDetector : IVariantDetector
{
    /// <summary>
    /// The <see cref="AssemblyMetadataAttribute"/> key Build.ps1 sets through the
    /// <c>HeimdallEdition</c> MSBuild property, one value per published edition.
    /// </summary>
    public const string EditionMetadataKey = "HeimdallEdition";

    private const string MarkerRelativePath = @"runtimes\webview2\msedgewebview2.exe";

    private readonly string _baseDirectory;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<IReadOnlyList<string>> _readInstallLocations;
    private readonly Func<BuildVariant?> _readStampedEdition;

    /// <param name="baseDirectory">Where the running copy lives; the application base directory by default.</param>
    /// <param name="fileExists">The file probe; the real filesystem by default.</param>
    /// <param name="readInstallLocations">
    /// Yields every install location the installer registered, in any hive; the
    /// Windows registry by default. A copy that appears in none of them was not
    /// installed by the installer.
    /// </param>
    /// <param name="readStampedEdition">
    /// Yields the edition stamped into the running executable, or null when it carries
    /// none; the entry assembly's metadata by default.
    /// </param>
    public VariantDetector(
        string? baseDirectory = null,
        Func<string, bool>? fileExists = null,
        Func<IReadOnlyList<string>>? readInstallLocations = null,
        Func<BuildVariant?>? readStampedEdition = null)
    {
        _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
        _fileExists = fileExists ?? File.Exists;
        _readInstallLocations = readInstallLocations ?? ReadRegisteredInstallLocations;
        _readStampedEdition = readStampedEdition ?? ReadEntryAssemblyEdition;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The stamp wins over the marker. The marker folder outlives the edition that put it
    /// there: a Standard installer run over a SelfContained install, or a Standard archive
    /// extracted over a SelfContained one, leaves <c>runtimes\webview2</c> behind, and the
    /// marker alone then reported SelfContained, made the Standard build run on that
    /// abandoned runtime, and fetched the SelfContained installer on the next update. The
    /// marker only decides for a build that carries no stamp, such as a local
    /// <c>dotnet build</c>.
    /// </remarks>
    public BuildVariant Detect()
    {
        BuildVariant? stamped = _readStampedEdition();
        if (stamped is { } edition)
        {
            return edition;
        }

        string markerPath = Path.Combine(_baseDirectory, MarkerRelativePath);
        return _fileExists(markerPath) ? BuildVariant.SelfContained : BuildVariant.Standard;
    }

    /// <summary>
    /// Parses an edition stamp. Unknown or empty values read as no stamp, so the marker
    /// decides rather than a guess.
    /// </summary>
    internal static BuildVariant? ParseEdition(string? value)
        => Enum.TryParse(value, ignoreCase: true, out BuildVariant edition) && Enum.IsDefined(edition)
            ? edition
            : null;

    private static BuildVariant? ReadEntryAssemblyEdition()
    {
        Assembly? entry = Assembly.GetEntryAssembly();
        if (entry is null)
        {
            return null;
        }

        foreach (AssemblyMetadataAttribute metadata in entry.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(metadata.Key, EditionMetadataKey, StringComparison.Ordinal))
            {
                return ParseEdition(metadata.Value);
            }
        }

        return null;
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
