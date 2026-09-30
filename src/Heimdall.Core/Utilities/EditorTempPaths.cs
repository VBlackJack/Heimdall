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

namespace Heimdall.Core.Utilities;

/// <summary>Where the remote file editor stages the files it opens.</summary>
/// <remarks>
/// One definition, read by the code that creates these directories and by the code that
/// sweeps them. Two independent copies of one path is how a sweeper ends up tidying a
/// directory nobody writes to while the real one grows forever.
/// </remarks>
public static class EditorTempPaths
{
    private const string ApplicationFolderName = "Heimdall";

    private const string EditFolderName = "edit";

    /// <summary>The editor's temporary root, one directory per open file beneath it.</summary>
    public static string Root =>
        Path.Combine(Path.GetTempPath(), ApplicationFolderName, EditFolderName);

    /// <summary>
    /// Creates one working directory under <see cref="Root"/>, restricted on Windows to the
    /// current user, Administrators and SYSTEM.
    /// </summary>
    /// <remarks>
    /// One factory for both editors. The external editor restricted its directory and the inline
    /// editor did not, so a root-owned file read through sudo landed in a directory inheriting the
    /// DACL of the temporary folder, readable by every account that can read it, and stayed there
    /// until the sweeper's retention ran out.
    /// <para>
    /// Fail closed: a directory that cannot be restricted is removed and the call refuses, rather
    /// than returning a directory that would expose whatever is staged in it.
    /// </para>
    /// </remarks>
    /// <exception cref="EditorWorkingDirectoryUnprotectedException">
    /// The directory could not be restricted; nothing was left behind.
    /// </exception>
    public static string CreateWorkingDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWorkingDirectory(Security.AclEnforcer.SetDirectoryAcl);
        }

        return CreateWorkingDirectory(restrict: null);
    }

    /// <summary>
    /// Creates one working directory under <see cref="Root"/> and restricts it with
    /// <paramref name="restrict"/>, when one is given.
    /// </summary>
    internal static string CreateWorkingDirectory(Action<string>? restrict)
    {
        string directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        if (restrict is not null)
        {
            try
            {
                restrict(directory);
            }
            catch (Exception ex)
            {
                Logging.FileLogger.Error(
                    $"Failed to restrict the editor working directory ACL; refusing to stage the file ({ex.GetType().Name}): {ex.Message}");
                RemoveUnprotectedDirectory(directory);
                throw new EditorWorkingDirectoryUnprotectedException(ex);
            }
        }

        return directory;
    }

    /// <summary>
    /// Removes a working directory that could not be restricted. It is empty, since nothing is
    /// staged before the restriction; a failure here is logged and left to the startup sweeper.
    /// </summary>
    private static void RemoveUnprotectedDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.FileLogger.Warn(
                $"Could not remove the unprotected editor working directory ({ex.GetType().Name}); the startup sweeper will.");
        }
    }
}
