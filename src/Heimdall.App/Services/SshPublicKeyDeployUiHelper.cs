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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
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

    public static async Task DeployInteractivelyAsync(
        IServiceProvider? services,
        string publicKeyLine,
        LocalizationManager localizer,
        Action<string> reportStatus)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(reportStatus);

        if (!SshPublicKeyDeployer.TryValidatePublicKeyLine(publicKeyLine, out var normalizedKey))
        {
            reportStatus(string.Format(
                CultureInfo.CurrentCulture,
                localizer["ToolSshKeyGenDeployFailed"],
                localizer["ToolSshKeyDeployErrInvalidKey"]));
            return;
        }

        var configManager = services?.GetService<IConfigManager>();
        var dialogService = services?.GetService<IDialogService>();
        var hostKeyStore = services?.GetService<HostKeyStore>();
        var hostKeyVerifier = services?.GetService<IHostKeyVerifier>();

        if (configManager is null || dialogService is null || hostKeyStore is null || hostKeyVerifier is null)
        {
            reportStatus(localizer["ToolSshKeyGenDeployNoServers"]);
            return;
        }

        var servers = await configManager.LoadServersAsync().ConfigureAwait(true);
        var settings = await configManager.LoadSettingsAsync().ConfigureAwait(true);
        var directSshServers = servers
            .Where(s => string.Equals(s.ConnectionType, "SSH", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(s.RemoteServer)
                && (s.UseDirectConnection || string.IsNullOrWhiteSpace(s.SshGatewayId)))
            .ToList();

        if (directSshServers.Count == 0)
        {
            reportStatus(localizer["ToolSshKeyGenDeployNoServers"]);
            return;
        }

        var choices = directSshServers
            .Select(s => $"{s.DisplayName} ({s.RemoteServer}:{s.SshPort})")
            .ToList();

        var selected = await dialogService.ShowChoiceAsync(
            localizer["ToolSshKeyGenDeployTitle"],
            localizer["ToolSshKeyGenDeploySelectServer"],
            choices,
            defaultIndex: 0,
            cancelLabel: localizer["BtnCancel"]);

        if (selected is not int index || index < 0 || index >= directSshServers.Count)
        {
            return;
        }

        var target = directSshServers[index];
        var password = ConnectionHelpers.DecryptPassword(target.SshPasswordEncrypted);
        var keyPassphrase = ConnectionHelpers.DecryptPassword(target.SshKeyPassphraseEncrypted);

        reportStatus(string.Format(
            CultureInfo.CurrentCulture,
            localizer["ToolSshKeyGenDeployInProgress"],
            target.DisplayName));

        var connParams = new SshConnectionParams
        {
            Host = target.RemoteServer,
            Port = target.SshPort,
            Username = target.SshUsername ?? string.Empty,
            KeyPath = string.IsNullOrWhiteSpace(target.SshKeyPath) ? null : target.SshKeyPath,
            Password = password,
            KeyPassphrase = keyPassphrase,
            SshAgentPreference = settings.SshAgentPreference,
            AgentKeyFingerprint = target.SshAgentKeyFingerprint,
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
                    ServerPromptText.Sanitize(request))).GetAwaiter().GetResult()
        };

        var result = await SshPublicKeyDeployer.DeployAsync(
                connParams,
                normalizedKey,
                hostKeyStore,
                hostKeyVerifier)
            .ConfigureAwait(true);

        if (result.Success)
        {
            var key = result.AlreadyInstalled
                ? "ToolSshKeyGenDeployAlreadyInstalled"
                : "ToolSshKeyGenDeploySuccess";
            reportStatus(string.Format(CultureInfo.CurrentCulture, localizer[key], target.DisplayName));
            return;
        }

        var errorText = FormatDeployError(result, localizer);
        FileLogger.Warn($"SSH public key deployment failed ({result.ErrorCode}): {errorText}");
        reportStatus(string.Format(
            CultureInfo.CurrentCulture,
            localizer["ToolSshKeyGenDeployFailed"],
            errorText));
    }
}
