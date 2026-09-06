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

namespace Heimdall.Core.Updates;

/// <summary>
/// Where updates come from, and how an installed copy is recognised. Pinned at
/// compile time, deliberately.
/// </summary>
/// <remarks>
/// The repository used to be a pair of strings in the user's <c>settings.json</c>,
/// unvalidated and shown in no interface. Anyone able to write that file could point
/// the updater at a repository of their own; the SHA-256 check passed, because it is
/// computed against that repository's own checksum file, and on a Program Files
/// install the relauncher started the payload under an elevation prompt carrying this
/// application's name. A user-level file write became administrator code execution.
/// SHA-256 is an integrity control against a corrupt transfer, not an authenticity
/// control; the only authenticity control available today is the source itself.
/// </remarks>
public static class UpdateSource
{
    /// <summary>The GitHub account that publishes releases.</summary>
    public const string RepositoryOwner = "VBlackJack";

    /// <summary>The GitHub repository whose latest release is the update.</summary>
    public const string RepositoryName = "Heimdall";

    /// <summary>
    /// The Inno Setup <c>AppId</c> of the installer, as it appears in the uninstall
    /// registration. One definition, shared with the guard over
    /// <c>installer/innosetup.iss</c>, so the installer and the probe cannot drift.
    /// </summary>
    public const string InnoSetupAppId = "{B7A4D3E1-8F2C-4A91-9D5E-6C3B8A1F0E72}";

    /// <summary>
    /// The uninstall key Inno Setup writes for that AppId, relative to the hive it
    /// chose at install time (per-user or per-machine).
    /// </summary>
    public const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoSetupAppId + "_is1";

    /// <summary>The value under that key naming the directory the installer wrote to.</summary>
    public const string InstallLocationValueName = "InstallLocation";
}
