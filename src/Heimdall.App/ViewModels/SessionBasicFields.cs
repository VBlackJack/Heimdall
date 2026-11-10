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

using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The five basic fields of a session the properties panel edits in place.
/// </summary>
/// <param name="DisplayName">The session name.</param>
/// <param name="Host">The server address; empty for a protocol with none.</param>
/// <param name="Port">The port of the protocol's own endpoint; 0 when the text typed is no number.</param>
/// <param name="Username">The login name of the protocol; empty for a protocol with none.</param>
/// <param name="Folder">The folder path, empty for no folder.</param>
internal sealed record SessionBasicFieldValues(
    string DisplayName,
    string Host,
    int Port,
    string Username,
    string Folder);

/// <summary>The inline errors of the five basic fields; null means the field is valid.</summary>
internal sealed record SessionBasicFieldErrors(
    string? DisplayName,
    string? Host,
    string? Port,
    string? Username)
{
    /// <summary>No error on any field.</summary>
    public static SessionBasicFieldErrors None { get; } = new(null, null, null, null);

    /// <summary>Whether every field passed.</summary>
    public bool IsValid => DisplayName is null && Host is null && Port is null && Username is null;
}

/// <summary>Which of the basic fields a protocol has; the folder and the name belong to every session.</summary>
internal sealed record SessionBasicFieldCapabilities(bool HasHost, bool HasPort, bool HasUsername)
{
    /// <summary>The fields of a connection type; an empty type reads as RDP, as the dialog reads it.</summary>
    public static SessionBasicFieldCapabilities For(string? connectionType)
    {
        string type = string.IsNullOrWhiteSpace(connectionType) ? "RDP" : connectionType.ToUpperInvariant();
        return type switch
        {
            "RDP" or "SSH" or "SFTP" or "FTP" or "WINRM" or "WEB" => new(true, true, true),
            "VNC" or "TELNET" => new(true, true, false),
            _ => new(false, false, false),
        };
    }
}

/// <summary>
/// Writes the basic fields onto a stored profile, the same way the server dialog lays them out.
/// </summary>
internal static class SessionBasicFields
{
    /// <summary>
    /// Assigns, on the stored profile, only the fields whose value the user changed.
    /// </summary>
    /// <param name="stored">The profile as it is in the inventory now, not as the panel read it.</param>
    /// <param name="original">The values the panel read when it opened.</param>
    /// <param name="edited">The values the user typed.</param>
    /// <remarks>
    /// A field the user did not touch is not assigned, so a change made to the stored profile in
    /// the meantime survives, and so does a field's presence flag (a port written through its
    /// setter marks itself as present). Each assignment mirrors <c>ServerDialogViewModel.ToDto</c>:
    /// a blank username or folder is stored as null.
    /// </remarks>
    public static void Apply(
        ServerProfileDto stored,
        SessionBasicFieldValues original,
        SessionBasicFieldValues edited)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);

        SessionBasicFieldCapabilities capabilities = SessionBasicFieldCapabilities.For(stored.ConnectionType);

        if (!string.Equals(original.DisplayName, edited.DisplayName, StringComparison.Ordinal))
        {
            stored.DisplayName = edited.DisplayName;
        }

        if (capabilities.HasHost
            && !string.Equals(original.Host, edited.Host, StringComparison.Ordinal))
        {
            stored.RemoteServer = edited.Host;
        }

        if (capabilities.HasPort && original.Port != edited.Port)
        {
            AssignPort(stored, edited.Port);
        }

        if (capabilities.HasUsername
            && !string.Equals(original.Username, edited.Username, StringComparison.Ordinal))
        {
            AssignUsername(stored, string.IsNullOrWhiteSpace(edited.Username) ? null : edited.Username);
        }

        if (!string.Equals(original.Folder, edited.Folder, StringComparison.Ordinal))
        {
            stored.Group = string.IsNullOrWhiteSpace(edited.Folder) ? null : edited.Folder;
        }
    }

    /// <summary>
    /// The values to judge a save by: what the user changed, and for every field left alone what
    /// the stored profile holds now, so a rule that couples fields (a web address is judged with
    /// its port and path) is never decided on values read when the panel opened.
    /// </summary>
    /// <param name="current">The values of the stored profile now.</param>
    /// <param name="original">The values the panel read when it opened.</param>
    /// <param name="edited">The values the user typed.</param>
    public static SessionBasicFieldValues Merge(
        SessionBasicFieldValues current,
        SessionBasicFieldValues original,
        SessionBasicFieldValues edited)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);

        return new SessionBasicFieldValues(
            string.Equals(original.DisplayName, edited.DisplayName, StringComparison.Ordinal)
                ? current.DisplayName : edited.DisplayName,
            string.Equals(original.Host, edited.Host, StringComparison.Ordinal) ? current.Host : edited.Host,
            original.Port == edited.Port ? current.Port : edited.Port,
            string.Equals(original.Username, edited.Username, StringComparison.Ordinal)
                ? current.Username : edited.Username,
            string.Equals(original.Folder, edited.Folder, StringComparison.Ordinal)
                ? current.Folder : edited.Folder);
    }

    private static void AssignPort(ServerProfileDto stored, int port)
    {
        switch (NormalizedType(stored))
        {
            case "RDP":
                stored.RemotePort = port;
                break;
            case "WINRM":
                stored.WinRmPort = port;
                break;
            case "VNC":
                stored.VncPort = port;
                stored.RemotePort = port;
                break;
            case "FTP":
                stored.FtpPort = port;
                stored.RemotePort = port;
                break;
            case "TELNET":
                stored.TelnetPort = port;
                stored.RemotePort = port;
                break;
            case "WEB":
                stored.WebPort = port;
                stored.RemotePort = port;
                break;
            default:
                stored.SshPort = port;
                stored.RemotePort = port;
                break;
        }
    }

    private static void AssignUsername(ServerProfileDto stored, string? username)
    {
        switch (NormalizedType(stored))
        {
            case "RDP":
                stored.RdpUsername = username;
                break;
            case "WINRM":
                stored.WinRmUsername = username;
                break;
            case "FTP":
                stored.FtpUsername = username;
                break;
            case "WEB":
                stored.WebUsername = username;
                break;
            default:
                stored.SshUsername = username;
                break;
        }
    }

    private static string NormalizedType(ServerProfileDto stored) =>
        string.IsNullOrWhiteSpace(stored.ConnectionType) ? "RDP" : stored.ConnectionType.ToUpperInvariant();
}
