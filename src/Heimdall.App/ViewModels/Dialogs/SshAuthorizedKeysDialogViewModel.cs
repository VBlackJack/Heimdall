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

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// View-model driving the remote <c>~/.ssh/authorized_keys</c> manager modal dialog.
/// Lists, adds (with optional OpenSSH restrictions), and safely removes authorized keys over
/// direct or gateway-routed SSH connections.
/// </summary>
public sealed partial class SshAuthorizedKeysDialogViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly ServerProfileDto _target;
    private readonly AppSettings _settings;
    private readonly LocalizationManager _localizer;
    private CancellationTokenSource? _cts;
    private string? _cachedPromptedPassword;

    public SshAuthorizedKeysDialogViewModel(
        IServiceProvider services,
        ServerProfileDto target,
        AppSettings settings,
        LocalizationManager localizer)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));

        int port = _target.SshPort > 0 ? _target.SshPort : DefaultPorts.Ssh;
        TargetSummary = $"{_target.DisplayName} ({_target.RemoteServer}:{port})";
    }

    public string TargetSummary { get; }

    public string PublicKeyFileFilter => _localizer["TreeCtxDeploySshKeyPubFilter"];

    public ObservableCollection<AuthorizedKeyEntry> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveSelected))]
    private AuthorizedKeyEntry? _selectedEntry;

    [ObservableProperty]
    private string _newPublicKeyLine = string.Empty;

    [ObservableProperty]
    private string _newKeyOptions = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(CanRemoveSelected))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    public bool CanInteract => !IsBusy;

    public bool CanRemoveSelected => !IsBusy && SelectedEntry is not null;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public void CancelActiveOperation()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        await RunWithConnectionAsync(async (connParams, hostKeyStore, hostKeyVerifier, ct) =>
        {
            var result = await SshAuthorizedKeysService.ListAsync(connParams, hostKeyStore, hostKeyVerifier, ct)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                if (result.ErrorCode == SshAuthorizedKeysErrorCode.ConnectionFailed)
                {
                    _cachedPromptedPassword = null;
                }

                ReportStatus(FormatError(result.ErrorCode, result.ErrorMessage, null), isError: true);
                return;
            }

            Entries.Clear();
            foreach (var entry in result.Entries)
            {
                Entries.Add(entry);
            }

            ReportStatus(
                _localizer.FormatCount(
                    Entries.Count,
                    "SshAuthKeysStatusLoadedOne",
                    "SshAuthKeysStatusLoaded",
                    Entries.Count),
                isError: false);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddKeyAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!SshPublicKeyDeployer.TryValidatePublicKeyLine(NewPublicKeyLine, out _))
        {
            ReportStatus(_localizer["ToolSshKeyDeployErrInvalidKey"], isError: true);
            return;
        }

        if (!SshAuthorizedKeysService.TryValidateOptions(NewKeyOptions, out _))
        {
            ReportStatus(_localizer["SshAuthKeysErrInvalidOptions"], isError: true);
            return;
        }

        await RunWithConnectionAsync(async (connParams, hostKeyStore, hostKeyVerifier, ct) =>
        {
            var addResult = await SshAuthorizedKeysService.AddKeyAsync(
                    connParams,
                    NewPublicKeyLine,
                    NewKeyOptions,
                    hostKeyStore,
                    hostKeyVerifier,
                    ct)
                .ConfigureAwait(true);

            if (!addResult.Success)
            {
                if (addResult.ErrorCode == SshAuthorizedKeysErrorCode.ConnectionFailed)
                {
                    _cachedPromptedPassword = null;
                }

                ReportStatus(FormatError(addResult.ErrorCode, addResult.ErrorMessage, addResult.ExitStatus), isError: true);
                return;
            }

            NewPublicKeyLine = string.Empty;
            NewKeyOptions = string.Empty;

            var listResult = await SshAuthorizedKeysService.ListAsync(connParams, hostKeyStore, hostKeyVerifier, ct)
                .ConfigureAwait(true);
            if (listResult.Success)
            {
                Entries.Clear();
                foreach (var entry in listResult.Entries)
                {
                    Entries.Add(entry);
                }
            }

            ReportStatus(
                _localizer[addResult.AlreadyPresent ? "SshAuthKeysStatusAlreadyExists" : "SshAuthKeysStatusAdded"],
                isError: false);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        if (IsBusy || SelectedEntry is not { } targetEntry)
        {
            return;
        }

        var dialogService = _services.GetService<IDialogService>();
        if (dialogService is null)
        {
            return;
        }

        bool isLastKey = Entries.Count <= 1;
        string confirmMessage = isLastKey
            ? _localizer.Format("SshAuthKeysConfirmRemoveLast", targetEntry.Fingerprint)
            : _localizer.Format("SshAuthKeysConfirmRemove", targetEntry.Fingerprint);

        bool confirmed = await dialogService.ShowConfirmAsync(
                _localizer["SshAuthKeysDialogTitle"],
                confirmMessage,
                "warning",
                _localizer["SshAuthKeysBtnRemove"],
                _localizer["BtnCancel"])
            .ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await RunWithConnectionAsync(async (connParams, hostKeyStore, hostKeyVerifier, ct) =>
        {
            var removeResult = await SshAuthorizedKeysService.RemoveKeyAsync(
                    connParams,
                    targetEntry.KeyType,
                    targetEntry.KeyBase64,
                    allowRemoveLastKey: isLastKey,
                    hostKeyStore,
                    hostKeyVerifier,
                    ct)
                .ConfigureAwait(true);

            if (!removeResult.Success)
            {
                if (removeResult.ErrorCode == SshAuthorizedKeysErrorCode.ConnectionFailed)
                {
                    _cachedPromptedPassword = null;
                }

                ReportStatus(FormatError(removeResult.ErrorCode, removeResult.ErrorMessage, removeResult.ExitStatus), isError: true);
                return;
            }

            var listResult = await SshAuthorizedKeysService.ListAsync(connParams, hostKeyStore, hostKeyVerifier, ct)
                .ConfigureAwait(true);
            if (listResult.Success)
            {
                Entries.Clear();
                foreach (var entry in listResult.Entries)
                {
                    Entries.Add(entry);
                }
            }

            ReportStatus(_localizer["SshAuthKeysStatusRemoved"], isError: false);
        }).ConfigureAwait(true);
    }

    private async Task RunWithConnectionAsync(
        Func<SshConnectionParams, HostKeyStore, IHostKeyVerifier, CancellationToken, Task> action)
    {
        var dialogService = _services.GetService<IDialogService>();
        var hostKeyStore = _services.GetService<HostKeyStore>();
        var hostKeyVerifier = _services.GetService<IHostKeyVerifier>();
        var tunnelService = _services.GetService<ITunnelService>();

        if (dialogService is null || hostKeyStore is null || hostKeyVerifier is null)
        {
            ReportStatus(_localizer["ToolSshKeyGenDeployNoServers"], isError: true);
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        IsBusy = true;
        TunnelLease? tunnelLease = null;
        try
        {
            int targetPort = _target.SshPort > 0 ? _target.SshPort : DefaultPorts.Ssh;
            string? password = _cachedPromptedPassword ?? ConnectionHelpers.DecryptPassword(_target.SshPasswordEncrypted);
            string? passphrase = ConnectionHelpers.DecryptPassword(_target.SshKeyPassphraseEncrypted);

            if (string.IsNullOrEmpty(password))
            {
                password = await SshPublicKeyDeployUiHelper.TryResolveExternalSshPasswordAsync(
                    _services,
                    _target,
                    _settings,
                    targetPort,
                    ct).ConfigureAwait(true);
            }

            if (string.IsNullOrEmpty(password)
                && string.IsNullOrWhiteSpace(_target.SshKeyPath)
                && string.IsNullOrWhiteSpace(_target.SshAgentKeyFingerprint))
            {
                bool hasAgentKeys = false;
                try
                {
                    hasAgentKeys = SshAgentRegistry.CreateDefault(_settings.SshAgentPreference)
                        .Observe().OfferableIdentityCount > 0;
                }
                catch (Exception)
                {
                    hasAgentKeys = false;
                }

                if (!hasAgentKeys)
                {
                    var endpoint = string.IsNullOrWhiteSpace(_target.SshUsername)
                        ? _target.RemoteServer
                        : $"{_target.SshUsername}@{_target.RemoteServer}";
                    password = await dialogService.ShowPasswordInputAsync(
                        _localizer["DialogSshPasswordPromptTitle"],
                        _localizer.Format("DialogSshPasswordPromptMessage", endpoint),
                        ct).ConfigureAwait(true);
                    if (password is null)
                    {
                        return;
                    }

                    _cachedPromptedPassword = password;
                }
            }

            string connectHost = _target.RemoteServer;
            int connectPort = targetPort;
            string? logicalHost = null;
            int? logicalPort = null;

            if (SshGatewayRoute.GatewayIdFor(_target, _settings.GroupDefaults) is not null)
            {
                if (tunnelService is null)
                {
                    ReportStatus(_localizer[SshLocalizationKeys.ErrorTunnelFailed], isError: true);
                    return;
                }

                var tunnelOutcome = await tunnelService.SetupTunnelIfNeededAsync(
                    _target,
                    targetPort,
                    _settings,
                    ct).ConfigureAwait(true);
                if (!tunnelOutcome.Success)
                {
                    ReportStatus(tunnelOutcome.ErrorMessage ?? _localizer[SshLocalizationKeys.ErrorTunnelFailed], isError: true);
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
                    logicalHost = _target.RemoteServer;
                    logicalPort = targetPort;
                }
            }

            var connParams = new SshConnectionParams
            {
                Host = connectHost,
                Port = connectPort,
                LogicalHost = logicalHost,
                LogicalPort = logicalPort,
                Username = _target.SshUsername ?? string.Empty,
                KeyPath = string.IsNullOrWhiteSpace(_target.SshKeyPath) ? null : _target.SshKeyPath,
                CertificatePath = string.IsNullOrWhiteSpace(_target.SshCertificatePath) ? null : _target.SshCertificatePath,
                Password = password,
                KeyPassphrase = passphrase,
                SshAgentPreference = _settings.SshAgentPreference,
                AgentKeyFingerprint = _target.SshAgentKeyFingerprint,
                UseLegacyPasswordAsKeyPassphrase = _target.UsesLegacySshCredentialMapping,
                LegacyCredentialName = _target.DisplayName,
                CryptoPolicy = SshCryptoPolicyResolver.Resolve(_target.SshCryptoPolicy, _settings.SshCryptoPolicy),
                OnBeforeAgentSign = SshAgentSignConfirmHelper.BuildBeforeSignNotification(_localizer, reportStatus: null),
                ConfirmAgentSign = SshAgentSignConfirmHelper.BuildConfirmCallback(
                    _settings,
                    _target.RemoteServer,
                    targetPort,
                    _localizer,
                    ct),
                AuthenticationTimeout = TimeSpan.FromMinutes(2),
                KeyboardInteractiveResponder = request => dialogService.ShowPasswordInputAsync(
                    _localizer[SshLocalizationKeys.InteractivePromptTitle],
                    string.Format(
                        CultureInfo.CurrentCulture,
                        _localizer[SshLocalizationKeys.InteractivePromptMessage],
                        _target.RemoteServer,
                        _target.SshUsername ?? string.Empty,
                        ServerPromptText.Sanitize(request)),
                    ct).GetAwaiter().GetResult()
            };

            await action(connParams, hostKeyStore, hostKeyVerifier, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ReportStatus(_localizer[SshLocalizationKeys.ErrorSshCancelled], isError: true);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshAuthorizedKeys] operation failed: {ex.Message}");
            ReportStatus(ex.Message, isError: true);
        }
        finally
        {
            tunnelLease?.Dispose();
            IsBusy = false;
        }
    }

    private string FormatError(SshAuthorizedKeysErrorCode code, string? rawMessage, int? exitStatus) => code switch
    {
        SshAuthorizedKeysErrorCode.InvalidPublicKey => _localizer["ToolSshKeyDeployErrInvalidKey"],
        SshAuthorizedKeysErrorCode.InvalidOptions => _localizer["SshAuthKeysErrInvalidOptions"],
        SshAuthorizedKeysErrorCode.SymlinkRefused => _localizer["SshKeyRotateErrSymlinkRefused"],
        SshAuthorizedKeysErrorCode.LastKeyProtected => _localizer["SshAuthKeysErrLastKeyProtected"],
        SshAuthorizedKeysErrorCode.RemoteCommandFailed when exitStatus is { } exitCode =>
            string.Format(CultureInfo.CurrentCulture, _localizer["ToolSshKeyDeployErrRemoteExit"], exitCode),
        _ => rawMessage ?? string.Empty
    };

    private void ReportStatus(string message, bool isError)
    {
        IsStatusError = isError;
        StatusMessage = message;
    }
}
