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
using System.Security.Cryptography;
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// Gives each recipe run a fresh, empty folder to run in, and takes it away afterwards. The
/// runner refuses a folder that is not empty, so a run never sees what an earlier one left.
/// </summary>
public interface IRecipeWorkFolders
{
    /// <summary>Creates a new empty folder and returns its absolute path.</summary>
    /// <exception cref="IOException">The folder could not be created.</exception>
    string Create();

    /// <summary>Deletes a folder made by <see cref="Create"/>, best effort; never throws.</summary>
    /// <param name="path">The path <see cref="Create"/> returned.</param>
    void Delete(string path);

    /// <summary>Deletes every run folder a crash left behind; never throws.</summary>
    void CleanStale();
}

/// <summary>
/// The run folders of the application, one subfolder per run under a root in the user's temporary
/// folder. The root is not under the data directory on purpose: a program run from a folder there
/// could reach the settings through a relative path.
/// </summary>
public sealed class RecipeWorkFolders : IRecipeWorkFolders
{
    private const string FolderIdFormat = "N";

    /// <summary>How many hexadecimal characters of the digest of the data directory name the root.</summary>
    private const int RootKeyLength = 16;

    private readonly string _root;

    /// <summary>Creates the service over a run folder root.</summary>
    /// <param name="root">The folder under which each run gets its own folder; <see cref="DefaultRoot(string)"/> in production.</param>
    public RecipeWorkFolders(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    /// <summary>
    /// The production root: a folder in the user's temporary folder named after a digest of the data
    /// directory, so that two instances on different data directories never share a root (one's
    /// stale cleanup would delete the other's live run folder) and the same data directory finds
    /// its root again after a restart.
    /// </summary>
    /// <param name="dataRoot">The application data directory (the one <c>--data-dir</c> overrides).</param>
    public static string DefaultRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        string full = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant()));
        string key = Convert.ToHexStringLower(digest)[..RootKeyLength];
        return Path.Combine(Path.GetTempPath(), AppConstants.RecipeRunsTempDirectoryName, key);
    }

    /// <inheritdoc />
    public string Create()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, Guid.NewGuid().ToString(FolderIdFormat));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <inheritdoc />
    public void Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string full = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(full) ?? string.Empty;

            // Only a direct child of the run root is ever deleted, whatever the caller passes.
            if (string.Equals(parent, _root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            FileLogger.Warn($"[Recipes] A run folder could not be deleted ({ex.GetType().Name})");
        }
    }

    /// <inheritdoc />
    public void CleanStale()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            foreach (string folder in Directory.EnumerateDirectories(_root))
            {
                Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogger.Warn($"[Recipes] The run folders could not be listed ({ex.GetType().Name})");
        }
    }
}
