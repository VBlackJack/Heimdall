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
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Shared static utilities for connection handlers and tunnel orchestration.
/// </summary>
internal static class ConnectionHelpers
{
    /// <summary>
    /// Converts a gateway DTO to SSH connection parameters, decrypting the password if present.
    /// </summary>
    internal static SshConnectionParams CreateGatewayConnectionParams(
        SshGatewayDto gateway,
        SshAgentPreference sshAgentPreference = SshAgentPreference.AutoOpenSshFirst)
    {
        string? password = null;
        if (!string.IsNullOrEmpty(gateway.SshPasswordEncrypted))
        {
            password = DecryptPassword(gateway.SshPasswordEncrypted);
        }

        string? keyPassphrase = null;
        if (!string.IsNullOrEmpty(gateway.SshKeyPassphraseEncrypted))
        {
            keyPassphrase = DecryptPassword(gateway.SshKeyPassphraseEncrypted);
        }

        return new SshConnectionParams
        {
            Host = gateway.Host,
            Port = gateway.Port,
            Username = gateway.User,
            KeyPath = string.IsNullOrWhiteSpace(gateway.KeyPath) ? null : gateway.KeyPath,
            Password = password,
            KeyPassphrase = keyPassphrase,
            SshAgentPreference = sshAgentPreference,
            UseLegacyPasswordAsKeyPassphrase = gateway.UsesLegacySshCredentialMapping,
            LegacyCredentialName = gateway.Name
        };
    }

    /// <summary>
    /// Resolves the path to plink.exe: uses the user-configured path if valid,
    /// otherwise falls back to the embedded tool copy.
    /// </summary>
    internal static string? ResolvePlinkPath(string? settingsPath)
    {
        if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath))
        {
            return settingsPath;
        }

        var embeddedPath = Path.Combine(
            AppContext.BaseDirectory,
            AppConstants.EmbeddedToolsSubdir,
            "plink.exe");
        if (File.Exists(embeddedPath))
        {
            Core.Logging.FileLogger.Info($"Using embedded plink: {embeddedPath}");
            return embeddedPath;
        }

        return null;
    }

    /// <summary>
    /// Resolves the path to putty.exe: uses the user-configured path if valid,
    /// falls back to the directory containing plink.exe, then the embedded tool copy.
    /// </summary>
    internal static string? ResolvePuttyPath(string? settingsPath, string? plinkPath)
    {
        if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath))
        {
            return settingsPath;
        }

        if (!string.IsNullOrWhiteSpace(plinkPath))
        {
            var plinkDir = Path.GetDirectoryName(plinkPath);
            if (!string.IsNullOrEmpty(plinkDir))
            {
                var candidate = Path.Combine(plinkDir, "putty.exe");
                if (File.Exists(candidate))
                {
                    Core.Logging.FileLogger.Info($"Using PuTTY found next to Plink: {candidate}");
                    return candidate;
                }
            }
        }

        var embeddedPath = Path.Combine(
            AppContext.BaseDirectory,
            AppConstants.EmbeddedToolsSubdir,
            "putty.exe");
        if (File.Exists(embeddedPath))
        {
            Core.Logging.FileLogger.Info($"Using embedded PuTTY: {embeddedPath}");
            return embeddedPath;
        }

        return null;
    }

    /// <summary>
    /// Decrypts a credential string. Supports both HMAC-protected and
    /// legacy DPAPI-only formats via <see cref="CredentialProtector"/>.
    /// </summary>
    /// <summary>
    /// Whether the embedded SSH client would be unable to authenticate for want of a
    /// login name.
    /// </summary>
    /// <remarks>
    /// <para>SSH.NET rejects an empty user name from every authentication method, so a
    /// blank username fails whatever credentials the profile carries and surfaces a raw
    /// ArgumentException rather than anything a user can act on.</para>
    /// <para>Deliberately broader than
    /// <see cref="Handlers.SshHandler.RequiresUsernameBeforeWritingPassword"/>, which
    /// exists to keep a password file off disk for a launcher that will never read it
    /// and therefore tolerates a key-only profile. It does not cover the external
    /// launcher: PuTTY asks for a login name of its own.</para>
    /// <para>Shared by the SSH and SFTP handlers so the two cannot drift apart.</para>
    /// </remarks>
    internal static bool RequiresUsernameToConnect(ServerProfileDto server)
        => string.IsNullOrWhiteSpace(server.SshUsername);

    internal static string? DecryptPassword(string? encryptedValue)
    {
        return CredentialProtector.Unprotect(encryptedValue);
    }

    /// <summary>
    /// Searches the PATH environment variable for an executable.
    /// Returns the first matching full path or null when not found.
    /// </summary>
    internal static string? FindInPath(string executableName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var fullPath = Path.Combine(dir, executableName);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }
}
