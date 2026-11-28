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

using System.Globalization;
using Heimdall.App.Localization;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Services;

/// <summary>
/// Shared UI workflow for interactively deploying an OpenSSH public key line to a configured
/// SSH server's <c>~/.ssh/authorized_keys</c>, with fully localized error messages.
/// </summary>
internal static class SshPublicKeyDeployUiHelper
{
    public static string FormatDeployError(SshPublicKeyDeployResult result, LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(localizer);

        return result.ErrorCode switch
        {
            SshPublicKeyDeployErrorCode.InvalidPublicKey => localizer["ToolSshKeyDeployErrInvalidKey"],
            SshPublicKeyDeployErrorCode.ConfirmationMissing => localizer["ToolSshKeyDeployErrNoConfirmation"],
            SshPublicKeyDeployErrorCode.RemoteCommandFailed when result.ExitStatus is { } exitCode
                && (string.IsNullOrWhiteSpace(result.ErrorMessage)
                    || result.ErrorMessage.StartsWith("Remote command exited with status ", StringComparison.Ordinal))
                => string.Format(
                    CultureInfo.CurrentCulture,
                    localizer["ToolSshKeyDeployErrRemoteExit"],
                    exitCode),
            _ => result.ErrorMessage ?? string.Empty
        };
    }

    public static Task DeployInteractivelyAsync(
        IServiceProvider? services,
        string publicKeyLine,
        LocalizationManager localizer,
        Action<string> reportStatus,
        CancellationToken cancellationToken = default) =>
        DeployInteractivelyAsync(
            services,
            publicKeyLine,
            localizer,
            (msg, _) => reportStatus(msg),
            cancellationToken);

    public static async Task DeployInteractivelyAsync(
        IServiceProvider? services,
        string publicKeyLine,
        LocalizationManager localizer,
        Action<string, bool> reportStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(reportStatus);

        if (!SshPublicKeyDeployer.TryValidatePublicKeyLine(publicKeyLine, out var normalizedKey))
        {
            reportStatus(
                string.Format(
                    CultureInfo.CurrentCulture,
                    localizer["ToolSshKeyGenDeployFailed"],
                    localizer["ToolSshKeyDeployErrInvalidKey"]),
                true);
            return;
        }

        var configManager = services?.GetService<IConfigManager>();
        var dialogService = services?.GetService<IDialogService>();
        var hostKeyStore = services?.GetService<HostKeyStore>();
        var hostKeyVerifier = services?.GetService<IHostKeyVerifier>();

        if (configManager is null || dialogService is null || hostKeyStore is null || hostKeyVerifier is null)
        {
            reportStatus(localizer["ToolSshKeyGenDeployNoServers"], true);
            return;
        }

        var servers = await configManager.LoadServersAsync().ConfigureAwait(true);
        var settings = await configManager.LoadSettingsAsync().ConfigureAwait(true);
        var tunnelService = services?.GetService<ITunnelService>();
        var sshServers = servers
            .Where(s => string.Equals(s.ConnectionType, "SSH", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(s.RemoteServer)
                && (SshGatewayRoute.GatewayIdFor(s, settings.GroupDefaults) is null || tunnelService is not null))
            .ToList();

        if (sshServers.Count == 0)
        {
            reportStatus(localizer["ToolSshKeyGenDeployNoServers"], true);
            return;
        }

        var choices = sshServers
            .Select(s =>
            {
                int port = s.SshPort > 0 ? s.SshPort : DefaultPorts.Ssh;
                return $"{s.DisplayName} ({s.RemoteServer}:{port})";
            })
            .ToList();

        var selected = await dialogService.ShowChoiceAsync(
            localizer["ToolSshKeyGenDeployTitle"],
            localizer["ToolSshKeyGenDeploySelectServer"],
            choices,
            defaultIndex: 0,
            cancelLabel: localizer["BtnCancel"]);

        if (selected is not int index || index < 0 || index >= sshServers.Count)
        {
            return;
        }

        await DeployToServerAsync(
            services,
            sshServers[index],
            settings,
            normalizedKey,
            localizer,
            reportStatus,
            cancellationToken).ConfigureAwait(true);
    }

    public static Task DeployToServerAsync(
        IServiceProvider? services,
        ServerProfileDto target,
        AppSettings settings,
        string publicKeyLine,
        LocalizationManager localizer,
        Action<string> reportStatus,
        CancellationToken cancellationToken = default) =>
        DeployToServerAsync(
            services,
            target,
            settings,
            publicKeyLine,
            localizer,
            (msg, _) => reportStatus(msg),
            cancellationToken);

    public static async Task DeployToServerAsync(
        IServiceProvider? services,
        ServerProfileDto target,
        AppSettings settings,
        string publicKeyLine,
        LocalizationManager localizer,
        Action<string, bool> reportStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(reportStatus);

        if (!SshPublicKeyDeployer.TryValidatePublicKeyLine(publicKeyLine, out var normalizedKey))
        {
            reportStatus(
                string.Format(
                    CultureInfo.CurrentCulture,
                    localizer["ToolSshKeyGenDeployFailed"],
                    localizer["ToolSshKeyDeployErrInvalidKey"]),
                true);
            return;
        }

        var dialogService = services?.GetService<IDialogService>();
        var hostKeyStore = services?.GetService<HostKeyStore>();
        var hostKeyVerifier = services?.GetService<IHostKeyVerifier>();
        if (dialogService is null || hostKeyStore is null || hostKeyVerifier is null)
        {
            reportStatus(localizer["ToolSshKeyGenDeployNoServers"], true);
            return;
        }

        int targetPort = target.SshPort > 0 ? target.SshPort : DefaultPorts.Ssh;
        var password = ConnectionHelpers.DecryptPassword(target.SshPasswordEncrypted);
        var keyPassphrase = ConnectionHelpers.DecryptPassword(target.SshKeyPassphraseEncrypted);

        if (string.IsNullOrEmpty(password))
        {
            password = await TryResolveExternalSshPasswordAsync(
                services,
                target,
                settings,
                targetPort,
                cancellationToken).ConfigureAwait(true);
        }

        // If the profile has no stored password or key file and no agent holds a key, prompt for a password
        // so password-only servers can accept their first public key deployment cleanly.
        if (string.IsNullOrEmpty(password)
            && string.IsNullOrWhiteSpace(target.SshKeyPath)
            && string.IsNullOrWhiteSpace(target.SshAgentKeyFingerprint))
        {
            bool hasAgentKeys = false;
            try
            {
                hasAgentKeys = SshAgentRegistry.CreateDefault(settings.SshAgentPreference)
                    .Observe().OfferableIdentityCount > 0;
            }
            catch (Exception)
            {
                hasAgentKeys = false;
            }

            if (!hasAgentKeys)
            {
                var endpoint = string.IsNullOrWhiteSpace(target.SshUsername)
                    ? target.RemoteServer
                    : $"{target.SshUsername}@{target.RemoteServer}";
                password = await dialogService.ShowPasswordInputAsync(
                    localizer["DialogSshPasswordPromptTitle"],
                    localizer.Format("DialogSshPasswordPromptMessage", endpoint),
                    cancellationToken).ConfigureAwait(true);
                if (password is null)
                {
                    return;
                }
            }
        }

        reportStatus(
            string.Format(
                CultureInfo.CurrentCulture,
                localizer["ToolSshKeyGenDeployInProgress"],
                target.DisplayName),
            false);

        string connectHost = target.RemoteServer;
        int connectPort = targetPort;
        string? logicalHost = null;
        int? logicalPort = null;

        var tunnelService = services?.GetService<ITunnelService>();
        bool needsTunnel = SshGatewayRoute.GatewayIdFor(target, settings.GroupDefaults) is not null;
        TunnelLease? tunnelLease = null;

        try
        {
            if (needsTunnel)
            {
                if (tunnelService is null)
                {
                    reportStatus(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            localizer["ToolSshKeyGenDeployFailed"],
                            localizer[SshLocalizationKeys.ErrorTunnelFailed]),
                        true);
                    return;
                }

                var tunnelOutcome = await tunnelService.SetupTunnelIfNeededAsync(
                    target,
                    targetPort,
                    settings,
                    cancellationToken).ConfigureAwait(true);
                if (!tunnelOutcome.Success)
                {
                    reportStatus(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            localizer["ToolSshKeyGenDeployFailed"],
                            tunnelOutcome.ErrorMessage ?? localizer[SshLocalizationKeys.ErrorTunnelFailed]),
                        true);
                    return;
                }

                if (tunnelOutcome.UsesTunnel && tunnelOutcome.Port > 0)
                {
                    tunnelLease = new TunnelLease(
                        tunnelService,
                        usesTunnel: true,
                        localPort: tunnelOutcome.Port,
                        leaseId: tunnelOutcome.TunnelLeaseId);
                    connectHost = tunnelOutcome.Host;
                    connectPort = tunnelOutcome.Port;
                    logicalHost = target.RemoteServer;
                    logicalPort = targetPort;
                }
            }

            var connParams = new SshConnectionParams
            {
                Host = connectHost,
                Port = connectPort,
                LogicalHost = logicalHost,
                LogicalPort = logicalPort,
                Username = target.SshUsername ?? string.Empty,
                KeyPath = string.IsNullOrWhiteSpace(target.SshKeyPath) ? null : target.SshKeyPath,
                CertificatePath = string.IsNullOrWhiteSpace(target.SshCertificatePath) ? null : target.SshCertificatePath,
                Password = password,
                KeyPassphrase = keyPassphrase,
                SshAgentPreference = settings.SshAgentPreference,
                CryptoPolicy = SshCryptoPolicyResolver.Resolve(target.SshCryptoPolicy, settings.SshCryptoPolicy),
                AgentKeyFingerprint = target.SshAgentKeyFingerprint,
                OnBeforeAgentSign = SshAgentSignConfirmHelper.BuildBeforeSignNotification(localizer, reportStatus: null),
                ConfirmAgentSign = SshAgentSignConfirmHelper.BuildConfirmCallback(
                    settings,
                    target.RemoteServer,
                    targetPort,
                    localizer,
                    cancellationToken),
                UseLegacyPasswordAsKeyPassphrase = target.UsesLegacySshCredentialMapping,
                LegacyCredentialName = target.DisplayName,
                AuthenticationTimeout = TimeSpan.FromMinutes(2),
                KeyboardInteractiveResponder = request => dialogService.ShowPasswordInputAsync(
                    localizer[SshLocalizationKeys.InteractivePromptTitle],
                    string.Format(
                        CultureInfo.CurrentCulture,
                        localizer[SshLocalizationKeys.InteractivePromptMessage],
                        target.RemoteServer,
                        target.SshUsername ?? string.Empty,
                        ServerPromptText.Sanitize(request)),
                    cancellationToken).GetAwaiter().GetResult()
            };

            var result = await SshPublicKeyDeployer.DeployAsync(
                    connParams,
                    normalizedKey,
                    hostKeyStore,
                    hostKeyVerifier,
                    cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {
                var key = result.AlreadyInstalled
                    ? "ToolSshKeyGenDeployAlreadyInstalled"
                    : "ToolSshKeyGenDeploySuccess";
                reportStatus(string.Format(CultureInfo.CurrentCulture, localizer[key], target.DisplayName), false);
                return;
            }

            var errorText = FormatDeployError(result, localizer);
            FileLogger.Warn($"SSH public key deployment failed ({result.ErrorCode}): {errorText}");
            reportStatus(
                string.Format(
                    CultureInfo.CurrentCulture,
                    localizer["ToolSshKeyGenDeployFailed"],
                    errorText),
                true);
        }
        finally
        {
            tunnelLease?.Dispose();
        }
    }

    internal static async Task<string?> TryResolveExternalSshPasswordAsync(
        IServiceProvider? services,
        ServerProfileDto target,
        AppSettings settings,
        int targetPort,
        CancellationToken cancellationToken)
    {
        if (!settings.UseExternalCredentialProvider)
        {
            return null;
        }

        if (!target.CredentialLookupConfirmed && !SharedSessionIds.IsSharedProfile(target))
        {
            return null;
        }

        var factory = services?.GetService<ICredentialProviderFactory>();
        if (factory is null)
        {
            return null;
        }

        try
        {
            var provider = factory.Create(settings);
            if (!provider.IsAvailable)
            {
                return null;
            }

            string vaultTitle = ViewModels.ServerListViewModel.CredentialLookupTitle(target);
            var credential = await provider.GetCredentialAsync(
                    target.RemoteServer,
                    targetPort,
                    target.SshUsername ?? string.Empty,
                    vaultTitle,
                    cancellationToken)
                .ConfigureAwait(true);

            return string.IsNullOrEmpty(credential?.Password) ? null : credential.Password;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"External credential lookup for SSH helper failed on {target.DisplayName}: {ex.Message}");
            return null;
        }
    }
}
