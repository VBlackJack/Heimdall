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

namespace Heimdall.Core.Configuration;

/// <summary>
/// The operating system a profile declares for the far end of its connection.
/// </summary>
/// <remarks>
/// <see cref="Auto"/> is the default and means "not declared": every profile written before
/// this field existed, and every profile an importer builds, reads as Auto and keeps the guess
/// made from the protocol.
/// </remarks>
public enum RemoteOs
{
    /// <summary>Not declared; the platform is guessed from the connection type.</summary>
    Auto = 0,

    /// <summary>The far end runs Windows.</summary>
    Windows = 1,

    /// <summary>The far end runs Linux or another POSIX system.</summary>
    Linux = 2,
}

/// <summary>
/// The single answer to "which platform's commands fit this session".
/// </summary>
/// <remarks>
/// <para>Three surfaces ask it - the library's platform filter, the post-connect step resolver
/// and the post-connect picker - and they must agree, so the rule lives here once.</para>
/// <para>An explicit declaration wins. <see cref="RemoteOs.Auto"/> keeps the rule that predates
/// the declaration: SSH reads as Linux; LOCAL, RDP and WINRM read as Windows; anything else is
/// undetermined. SSH towards a Windows OpenSSH server and a local WSL shell are exactly the cases
/// that rule gets wrong, which is why a profile can now say otherwise.</para>
/// </remarks>
public static class RemoteOsResolver
{
    /// <summary>
    /// The effective platform for a session: <see cref="RemoteOs.Windows"/>,
    /// <see cref="RemoteOs.Linux"/>, or <see cref="RemoteOs.Auto"/> when it cannot be told.
    /// </summary>
    /// <param name="declared">What the profile declares.</param>
    /// <param name="connectionType">The profile's connection type, any case.</param>
    public static RemoteOs Resolve(RemoteOs declared, string? connectionType)
    {
        if (declared is RemoteOs.Windows or RemoteOs.Linux)
        {
            return declared;
        }

        return connectionType?.Trim().ToUpperInvariant() switch
        {
            "SSH" => RemoteOs.Linux,
            "LOCAL" or "RDP" or "WINRM" => RemoteOs.Windows,
            _ => RemoteOs.Auto,
        };
    }
}
