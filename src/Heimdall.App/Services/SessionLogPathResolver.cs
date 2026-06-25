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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>
/// Resolves the absolute root directory for session transcripts from
/// <see cref="AppSettings.SessionLogDirectory"/>.
/// </summary>
/// <remarks>
/// An absolute configured path is used verbatim. A relative one is rooted under the same
/// app-writable base directory <c>FileLogger</c> uses for its diagnostic log, so transcripts
/// sit beside <c>logs/heimdall_*.log</c> and stay writable when the app is installed. This
/// deliberately derives the base from FileLogger's own writable location instead of taking a
/// second, independent reference to the install directory.
/// </remarks>
public static class SessionLogPathResolver
{
    /// <summary>
    /// Computes the absolute session-log root directory.
    /// </summary>
    /// <param name="settings">Application settings carrying <see cref="AppSettings.SessionLogDirectory"/>.</param>
    /// <param name="appWritableBaseDirectory">
    /// The app-writable base directory under which relative paths are rooted (the directory that
    /// contains FileLogger's <c>logs</c> folder).
    /// </param>
    /// <returns>An absolute directory path.</returns>
    public static string Resolve(AppSettings settings, string appWritableBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(appWritableBaseDirectory);

        string configured = string.IsNullOrWhiteSpace(settings.SessionLogDirectory)
            ? new AppSettings().SessionLogDirectory
            : settings.SessionLogDirectory;

        if (Path.IsPathRooted(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.GetFullPath(Path.Combine(appWritableBaseDirectory, configured));
    }
}
