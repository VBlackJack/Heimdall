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

namespace Heimdall.App.ViewModels.Shell;

/// <summary>
/// The identifiers of the shell's top-level tabs.
/// </summary>
/// <remarks>
/// These are internal identifiers, never user-facing text: the visible tab labels come from the
/// locale files, and renaming a label must not move a tab. Before this type they were bare string
/// literals repeated across the shell view model and the main window, compared with
/// <see cref="StringComparison.Ordinal"/> at every site, so a single typo silently produced a tab
/// that no comparison ever matched and no compiler ever questioned.
/// </remarks>
public static class ShellTab
{
    /// <summary>Connected sessions. The shell opens here.</summary>
    public const string Sessions = "Sessions";

    /// <summary>SSH tunnels.</summary>
    public const string Tunnels = "Tunnels";

    /// <summary>Scheduled tasks.</summary>
    public const string Scheduled = "Scheduled";

    /// <summary>Application settings. The only tab whose exit is guarded.</summary>
    public const string Settings = "Settings";

    /// <summary>Standalone tools.</summary>
    public const string Tools = "Tools";

    /// <summary>Version and licence information.</summary>
    public const string About = "About";

    /// <summary>
    /// Every tab identifier, for exhaustiveness checks.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        Sessions,
        Tunnels,
        Scheduled,
        Settings,
        Tools,
        About,
    ];
}
