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

namespace Heimdall.Ssh.Plink;

/// <summary>
/// Defines the shared naming contract for temporary Plink password files.
/// </summary>
public static class PlinkPasswordFileNaming
{
    /// <summary>
    /// Canonical prefix used by every Plink password-file writer.
    /// </summary>
    public const string Prefix = "heimdall_ssh_pw_";

    /// <summary>
    /// Search pattern for password files created with the canonical prefix.
    /// </summary>
    public const string SearchPattern = Prefix + "*";

    /// <summary>
    /// Prefix used by tunnel password files before the naming contract was unified.
    /// Retained only so startup cleanup can remove pre-existing crash orphans.
    /// </summary>
    public const string LegacyTunnelPrefix = "heimdall_pw_";

    /// <summary>
    /// Search pattern for pre-existing tunnel password-file orphans.
    /// </summary>
    public const string LegacyTunnelSearchPattern = LegacyTunnelPrefix + "*";
}
