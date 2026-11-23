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

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Heimdall.Core.Recipes;

/// <summary>
/// What program resolution needs to know about the machine, so that tests never depend on the real one.
/// </summary>
public interface IRecipeMachine
{
    /// <summary>The Windows folder, or an empty string when unknown.</summary>
    string WindowsDirectory { get; }

    /// <summary>The Program Files folder, or an empty string when unknown.</summary>
    string ProgramFilesDirectory { get; }

    /// <summary>The Program Files (x86) folder, or an empty string when there is none.</summary>
    string ProgramFilesX86Directory { get; }

    /// <summary>The entries of the PATH variable, in order, exactly as written.</summary>
    IReadOnlyList<string> PathEntries { get; }

    /// <summary>True when the path names an existing file (not a folder).</summary>
    bool FileExists(string path);

    /// <summary>
    /// The final path of an existing file with every junction and link followed, or null when it
    /// cannot be resolved. The file is never executed.
    /// </summary>
    string? ResolveFinalPath(string path);

    /// <summary>
    /// The original file name recorded in the version resource of a file, or null when there is none
    /// or it cannot be read. Reading it never executes the file.
    /// </summary>
    string? ReadOriginalFileName(string path);

    /// <summary>
    /// The permission entries that allow something on a file or a folder, read without opening it
    /// for writing; null when they cannot be read. Entries that deny are left out.
    /// </summary>
    IReadOnlyList<RecipeAccessRule>? ReadAllowRules(string path);
}

/// <summary>The real machine.</summary>
public sealed class RecipeMachine : IRecipeMachine
{
    private const char PathSeparator = ';';

    /// <summary>The environment variable that lists the search folders.</summary>
    private const string PathVariable = "PATH";

    /// <summary>The shared instance.</summary>
    public static RecipeMachine Instance { get; } = new();

    /// <inheritdoc />
    public string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <inheritdoc />
    public string ProgramFilesDirectory => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <inheritdoc />
    public string ProgramFilesX86Directory => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    /// <inheritdoc />
    public IReadOnlyList<string> PathEntries
        => (Environment.GetEnvironmentVariable(PathVariable) ?? string.Empty).Split(PathSeparator);

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public string? ResolveFinalPath(string path) => RecipeProgramNative.ResolveFinalPath(path);

    /// <inheritdoc />
    public string? ReadOriginalFileName(string path)
    {
        try
        {
            string? name = FileVersionInfo.GetVersionInfo(path).OriginalFilename;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RecipeAccessRule>? ReadAllowRules(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            // Only the permission list is requested, and the item is opened for reading its security.
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access);

            List<RecipeAccessRule> rules = [];
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                    continue;

                bool inheritOnly = (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0;
                rules.Add(new RecipeAccessRule(rule.IdentityReference.Value, (int)rule.FileSystemRights, inheritOnly));
            }

            return rules;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
