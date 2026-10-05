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
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Creates a log file that is restricted before any data reaches it.
/// </summary>
internal static class RestrictedLogFile
{
    /// <summary>
    /// Creates <paramref name="path"/> empty and closed, then applies the restrictive ACL.
    /// </summary>
    /// <remarks>
    /// If the ACL cannot be applied the empty file is removed before the failure propagates.
    /// Writers only harden a file they create, so a file left behind here would be picked up by
    /// the next attempt as existing and filled with its inherited, wider permissions.
    /// </remarks>
    /// <param name="path">The file to create. It must not exist yet.</param>
    /// <param name="harden">Applies the ACL; defaults to <see cref="AclEnforcer.SetFileAcl"/> on Windows.</param>
    internal static void Create(string path, Action<string>? harden = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        harden ??= DefaultHarden;
        try
        {
            harden(path);
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Still held or already gone; the original failure is the one to report.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }

            throw;
        }
    }

    private static void DefaultHarden(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            AclEnforcer.SetFileAcl(path);
        }
    }
}
