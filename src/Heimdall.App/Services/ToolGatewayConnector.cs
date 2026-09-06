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

using System.Windows;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;

namespace Heimdall.App.Services;

/// <summary>
/// Shared helper for connecting to an SSH gateway from network tools.
/// Runs authentication preflight, attaches TOFU host key verification,
/// and provides clear error messages on failure.
/// </summary>
internal static class ToolGatewayConnector
{
    /// <summary>
    /// Creates and connects an SSH client to the specified gateway.
    /// Validates auth prerequisites, verifies host keys via TOFU,
    /// and returns a ready-to-use client for remote command execution.
    /// </summary>
    /// <param name="gateway">Gateway to connect to.</param>
    /// <returns>A connected <see cref="SshClient"/>. Caller must dispose.</returns>
    /// <exception cref="InvalidOperationException">Preflight check failed.</exception>
    /// <exception cref="SshException">SSH connection or authentication failed.</exception>
    public static SshClient Connect(SshGatewayDto gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        string? password = null;
        if (!string.IsNullOrEmpty(gateway.SshPasswordEncrypted))
        {
            password = CredentialProtector.Unprotect(gateway.SshPasswordEncrypted);
        }

        string? keyPassphrase = null;
        if (!string.IsNullOrEmpty(gateway.SshKeyPassphraseEncrypted))
        {
            keyPassphrase = CredentialProtector.Unprotect(gateway.SshKeyPassphraseEncrypted);
        }

        var connParams = new SshConnectionParams
        {
            Host = gateway.Host,
            Port = gateway.Port,
            Username = gateway.User,
            KeyPath = gateway.KeyPath,
            Password = password,
            KeyPassphrase = keyPassphrase,
            UseLegacyPasswordAsKeyPassphrase = gateway.UsesLegacySshCredentialMapping,
            LegacyCredentialName = gateway.Name,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };

        var (trustService, localizer) = ResolveHostKeyDependencies();

        // Validate authentication prerequisites before attempting connection
        var preflight = AuthPreflightChecker.Check(connParams, isTunnelMode: true);
        if (!preflight.Success)
        {
            throw new InvalidOperationException(
                TunnelFailureMessageResolver.ResolvePreflightMessage(preflight, localizer));
        }

        var client = SshConnectionFactory.CreateSshClient(connParams);
        var fingerprint = trustService.GetEffectiveEntry(gateway.Host, gateway.Port)?.Fingerprint
            ?? throw new InvalidOperationException(
                localizer.Format("ErrorGatewayHostKeyNotTrusted", gateway.Host, gateway.Port));

        // TOFU host key verification
        SshConnectionFactory.AttachPinnedHostKeyVerification(
            client,
            gateway.Host,
            gateway.Port,
            new PinnedFingerprintVerifier(gateway.Host, gateway.Port, fingerprint));

        client.Connect();

        Core.Logging.FileLogger.Info(
            $"Tool gateway connected: {gateway.Name} ({gateway.Host}:{gateway.Port})");

        return client;
    }

    private static (IHostKeyTrustService TrustService, LocalizationManager Localizer) ResolveHostKeyDependencies()
    {
        if ((Application.Current as App)?.Services is IServiceProvider services)
        {
            if (services.GetService<IHostKeyTrustService>() is { } trustService &&
                services.GetService<LocalizationManager>() is { } localizer)
            {
                return (trustService, localizer);
            }
        }

        throw new InvalidOperationException(
            "ToolGatewayConnector could not resolve host key services from DI; refusing to connect without pinned host key verification.");
    }
}
