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
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// View-model driving the 5-step guided SSH key rotation wizard with isolated new-key verification,
/// profile persistence BEFORE old-key removal (eliminating lockout windows), and atomic old-key cleanup.
/// </summary>
public sealed partial class SshKeyRotationDialogViewModel : ObservableObject
{
    public sealed record AlgorithmOption(SshKeyAlgorithm Algorithm, string DisplayName);

    private readonly IServiceProvider _services;
    private readonly ServerProfileDto _target;
    private readonly AppSettings _settings;
    private readonly LocalizationManager _localizer;
    private readonly string? _oldPublicKeyLine;
    private CancellationTokenSource? _cts;

    public SshKeyRotationDialogViewModel(
        IServiceProvider services,
        ServerProfileDto target,
        AppSettings settings,
        LocalizationManager localizer)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));

        Algorithms =
        [
            new AlgorithmOption(SshKeyAlgorithm.Ed25519, _localizer["SshKeyRotateAlgoEd25519Recommended"]),
            new AlgorithmOption(SshKeyAlgorithm.EcdsaP256, "ECDSA P-256"),
            new AlgorithmOption(SshKeyAlgorithm.EcdsaP384, "ECDSA P-384"),
            new AlgorithmOption(SshKeyAlgorithm.Rsa4096, "RSA 4096")
        ];
        _selectedAlgorithm = Algorithms[0];

        int port = _target.SshPort > 0 ? _target.SshPort : DefaultPorts.Ssh;
        TargetSummary = $"{_target.DisplayName} ({_target.RemoteServer}:{port})";
        CurrentKeyDisplay = string.IsNullOrWhiteSpace(_target.SshKeyPath)
            ? _localizer["SshKeyRotateNoCurrentKey"]
            : _target.SshKeyPath;

        _oldPublicKeyLine = TryReadOldPublicKeyLine(_target.SshKeyPath);
        CanRemoveOldKey = _oldPublicKeyLine is not null;
        _removeOldKeyFromServer = CanRemoveOldKey;
        _newPrivateKeyPath = SuggestNewKeyPath(_target);
    }

    public IReadOnlyList<AlgorithmOption> Algorithms { get; }

    public string TargetSummary { get; }

    public string CurrentKeyDisplay { get; }

    public bool CanRemoveOldKey { get; }

    [ObservableProperty]
    private AlgorithmOption _selectedAlgorithm;

    [ObservableProperty]
    private string _newPrivateKeyPath;

    public string NewKeyPassphrase { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _removeOldKeyFromServer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditOptions))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _completed;

    [ObservableProperty]
    private bool _hasStarted;

    [ObservableProperty]
    private string _progressLog = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    public bool CanEditOptions => !IsRunning && !Completed;

    public bool CanStart => !IsRunning && !Completed;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool Succeeded { get; private set; }

    public event Action<bool>? CloseRequested;

    public void CancelActiveRotation()
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
    private void CancelOrClose()
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            return;
        }

        CloseRequested?.Invoke(Succeeded);
    }

    [RelayCommand]
    private async Task StartRotationAsync()
    {
        if (IsRunning || Completed)
        {
            return;
        }

        var configManager = _services.GetService<IConfigManager>();
        var dialogService = _services.GetService<IDialogService>();
        var hostKeyStore = _services.GetService<HostKeyStore>();
        var hostKeyVerifier = _services.GetService<IHostKeyVerifier>();
        var tunnelService = _services.GetService<ITunnelService>();

        if (configManager is null || dialogService is null || hostKeyStore is null || hostKeyVerifier is null)
        {
            ReportError(_localizer["ToolSshKeyGenDeployNoServers"]);
            return;
        }

        string privateKeyPath = NewPrivateKeyPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(privateKeyPath))
        {
            ReportError(_localizer["SshKeyRotateOutputPathLabel"]);
            return;
        }

        if (ImportedPathPolicy.IsNetworkOrDevicePath(privateKeyPath))
        {
            ReportError(_localizer["SshKeyRotateErrNetworkPath"]);
            return;
        }

        string publicKeyPath = privateKeyPath + ".pub";
        string fullNewPrivatePath;
        string? fullCurrentKeyPath;
        try
        {
            fullNewPrivatePath = Path.GetFullPath(privateKeyPath);
            fullCurrentKeyPath = string.IsNullOrWhiteSpace(_target.SshKeyPath)
                ? null
                : Path.GetFullPath(_target.SshKeyPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ReportError(_localizer["SshKeyRotateErrInvalidPath"]);
            return;
        }

        if (File.Exists(fullNewPrivatePath)
            || File.Exists(publicKeyPath)
            || (fullCurrentKeyPath is not null
                && string.Equals(fullNewPrivatePath, fullCurrentKeyPath, StringComparison.OrdinalIgnoreCase)))
        {
            ReportError(_localizer["SshKeyRotateErrOutputExists"]);
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        IsRunning = true;
        HasStarted = true;
        StatusMessage = string.Empty;
        IsStatusError = false;
        ProgressLog = string.Empty;

        TunnelLease? tunnelLease = null;
        bool wroteNewKeyFiles = false;
        bool profilePersisted = false;
        try
        {
            // Step 1: Generate new key pair & write to new files (never overwrite old key in place).
            AppendStep(_localizer["SshKeyRotateStepGenerate"]);
            string comment = $"{_target.SshUsername}@{_target.RemoteServer}-rotated-{DateTime.UtcNow:yyyyMMdd}";
            GeneratedSshKeyPair pair = await Task.Run(
                () => SshKeyPairGenerator.Generate(
                    SelectedAlgorithm.Algorithm,
                    comment,
                    NewKeyPassphrase,
                    SshPrivateKeyFormat.OpenSsh),
                ct).ConfigureAwait(true);

            string? dir = Path.GetDirectoryName(fullNewPrivatePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            SshKeyFileWriter.WritePrivateKey(privateKeyPath, pair.PrivateKey);
            SshKeyFileWriter.WritePublicKey(publicKeyPath, pair.PublicKeyOpenSsh);
            wroteNewKeyFiles = true;

            // Prepare optional gateway tunnel lease once for deploy + verify + cleanup.
            int targetPort = _target.SshPort > 0 ? _target.SshPort : DefaultPorts.Ssh;
            string connectHost = _target.RemoteServer;
            int connectPort = targetPort;
            string? logicalHost = null;
            int? logicalPort = null;

            if (SshGatewayRoute.GatewayIdFor(_target, _settings.GroupDefaults) is not null)
            {
                if (tunnelService is null)
                {
                    ReportError(_localizer[SshLocalizationKeys.ErrorTunnelFailed]);
                    return;
                }

                var tunnelOutcome = await tunnelService.SetupTunnelIfNeededAsync(
                    _target,
                    targetPort,
                    _settings,
                    ct).ConfigureAwait(true);
                if (!tunnelOutcome.Success)
                {
                    ReportError(tunnelOutcome.ErrorMessage ?? _localizer[SshLocalizationKeys.ErrorTunnelFailed]);
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

            // Step 2: Deploy new public key using current profile credentials.
            AppendStep(_localizer["SshKeyRotateStepDeploy"]);
            var currentPassword = ConnectionHelpers.DecryptPassword(_target.SshPasswordEncrypted);
            var currentPassphrase = ConnectionHelpers.DecryptPassword(_target.SshKeyPassphraseEncrypted);
            if (string.IsNullOrEmpty(currentPassword))
            {
                currentPassword = await SshPublicKeyDeployUiHelper.TryResolveExternalSshPasswordAsync(
                    _services,
                    _target,
                    _settings,
                    targetPort,
                    ct).ConfigureAwait(true);
            }

            if (string.IsNullOrEmpty(currentPassword)
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
                    currentPassword = await dialogService.ShowPasswordInputAsync(
                        _localizer["DialogSshPasswordPromptTitle"],
                        _localizer.Format("DialogSshPasswordPromptMessage", endpoint),
                        ct).ConfigureAwait(true);
                    if (currentPassword is null)
                    {
                        ReportError(_localizer["SshKeyRotateCancelled"]);
                        return;
                    }
                }
            }

            var deployParams = new SshConnectionParams
            {
                Host = connectHost,
                Port = connectPort,
                LogicalHost = logicalHost,
                LogicalPort = logicalPort,
                Username = _target.SshUsername ?? string.Empty,
                KeyPath = string.IsNullOrWhiteSpace(_target.SshKeyPath) ? null : _target.SshKeyPath,
                CertificatePath = string.IsNullOrWhiteSpace(_target.SshCertificatePath) ? null : _target.SshCertificatePath,
                Password = currentPassword,
                KeyPassphrase = currentPassphrase,
                SshAgentPreference = _settings.SshAgentPreference,
                AgentKeyFingerprint = _target.SshAgentKeyFingerprint,
                UseLegacyPasswordAsKeyPassphrase = _target.UsesLegacySshCredentialMapping,
                LegacyCredentialName = _target.DisplayName,
                CryptoPolicy = SshCryptoPolicyResolver.Resolve(_target.SshCryptoPolicy, _settings.SshCryptoPolicy),
                ProxyType = logicalHost is not null ? SshProxyType.None : _target.SshProxyType,
                ProxyHost = logicalHost is not null || string.IsNullOrWhiteSpace(_target.SshProxyHost) ? null : _target.SshProxyHost.Trim(),
                ProxyPort = logicalHost is not null ? 0 : _target.SshProxyPort,
                ProxyUsername = logicalHost is not null || string.IsNullOrWhiteSpace(_target.SshProxyUsername) ? null : _target.SshProxyUsername.Trim(),
                ProxyPassword = logicalHost is not null ? null : ConnectionHelpers.DecryptPassword(_target.SshProxyPasswordEncrypted),
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

            var deployResult = await SshPublicKeyDeployer.DeployAsync(
                    deployParams,
                    pair.PublicKeyOpenSsh,
                    hostKeyStore,
                    hostKeyVerifier,
                    ct)
                .ConfigureAwait(true);

            if (!deployResult.Success)
            {
                ReportError(SshPublicKeyDeployUiHelper.FormatDeployError(deployResult, _localizer));
                return;
            }

            // Step 3: Verify isolated connection with ONLY the new key (agent + password + cert disabled).
            AppendStep(_localizer["SshKeyRotateStepVerify"]);
            var isolatedVerifyParams = SshKeyRotator.BuildIsolatedVerificationParams(
                deployParams,
                privateKeyPath,
                NewKeyPassphrase);

            var verifyResult = await SshKeyRotator.VerifyNewKeyAsync(
                    isolatedVerifyParams,
                    hostKeyStore,
                    hostKeyVerifier,
                    ct)
                .ConfigureAwait(true);

            if (!verifyResult.Success)
            {
                ReportError(FormatRotationStepError(verifyResult));
                return;
            }

            // Step 4: Save updated profile BEFORE removing the old key (eliminates lockout window).
            AppendStep(_localizer["SshKeyRotateStepSave"]);
            string? encryptedPassphrase = string.IsNullOrEmpty(NewKeyPassphrase)
                ? null
                : CredentialProtector.Protect(NewKeyPassphrase);

            bool saved = await configManager.MutateServersAsync(inventory =>
            {
                var profile = inventory.FirstOrDefault(s =>
                    string.Equals(s.Id, _target.Id, StringComparison.Ordinal));
                if (profile is null)
                {
                    return false;
                }

                profile.SshKeyPath = privateKeyPath;
                profile.SshKeyPassphraseEncrypted = encryptedPassphrase;
                profile.SshCertificatePath = null;
                profile.SshAgentKeyFingerprint = null;
                return true;
            }).ConfigureAwait(true);

            if (!saved)
            {
                ReportError(_localizer["SshKeyRotateErrProfileSaveFailed"]);
                return;
            }

            profilePersisted = true;
            Succeeded = true;

            // Step 5: Atomically remove the old public key from remote authorized_keys (optional).
            if (RemoveOldKeyFromServer && _oldPublicKeyLine is not null)
            {
                AppendStep(_localizer["SshKeyRotateStepRemoveOld"]);
                var removeResult = await SshKeyRotator.RemoveOldKeyAsync(
                        isolatedVerifyParams,
                        _oldPublicKeyLine,
                        pair.PublicKeyOpenSsh,
                        hostKeyStore,
                        hostKeyVerifier,
                        ct)
                    .ConfigureAwait(true);

                if (!removeResult.Success)
                {
                    Completed = true;
                    IsStatusError = true;
                    StatusMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        _localizer["SshKeyRotatePartialSuccess"],
                        FormatRotationStepError(removeResult));
                    return;
                }
            }

            Completed = true;
            IsStatusError = false;
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["SshKeyRotateSuccess"],
                pair.Fingerprint);
        }
        catch (OperationCanceledException)
        {
            ReportError(_localizer["SshKeyRotateCancelled"]);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshKeyRotation] rotation failed: {ex.Message}");
            ReportError(ex.Message);
        }
        finally
        {
            if (wroteNewKeyFiles && !profilePersisted)
            {
                TryDeleteOrphanedKeyFile(privateKeyPath);
                TryDeleteOrphanedKeyFile(publicKeyPath);
            }

            tunnelLease?.Dispose();
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    private static void TryDeleteOrphanedKeyFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Debug($"[SshKeyRotation] failed to delete orphaned key file '{path}': {ex.Message}");
        }
    }

    private void AppendStep(string line)
    {
        ProgressLog = string.IsNullOrEmpty(ProgressLog)
            ? line
            : ProgressLog + Environment.NewLine + line;
    }

    private void ReportError(string error)
    {
        IsStatusError = true;
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            _localizer["SshKeyRotateFailed"],
            error);
    }

    private string FormatRotationStepError(SshKeyRotationStepResult result) => result.ErrorCode switch
    {
        SshKeyRotationErrorCode.SymlinkRefused => _localizer["SshKeyRotateErrSymlinkRefused"],
        SshKeyRotationErrorCode.InvariantFailed => _localizer["SshKeyRotateErrInvariantFailed"],
        SshKeyRotationErrorCode.VerificationFailed => _localizer["SshKeyRotateErrVerifyFailed"],
        SshKeyRotationErrorCode.RemoteCommandFailed when result.ExitStatus is { } exitCode =>
            string.Format(CultureInfo.CurrentCulture, _localizer["SshKeyRotateErrRemoteExit"], exitCode),
        _ => string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? _localizer["SshKeyRotateErrVerifyFailed"]
            : result.ErrorMessage
    };

    private static string? TryReadOldPublicKeyLine(string? privateKeyPath)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPath))
        {
            return null;
        }

        string pubPath = privateKeyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)
            ? privateKeyPath
            : privateKeyPath + ".pub";
        try
        {
            if (!File.Exists(pubPath))
            {
                return null;
            }

            var info = new FileInfo(pubPath);
            if (info.Length > 64 * 1024)
            {
                return null;
            }

            string line = File.ReadAllText(pubPath, Encoding.UTF8).Trim();
            return SshPublicKeyDeployer.TryValidatePublicKeyLine(line, out string normalized)
                ? normalized
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SuggestNewKeyPath(ServerProfileDto target)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(target.SshKeyPath))
        {
            string? dir = Path.GetDirectoryName(target.SshKeyPath);
            string baseName = Path.GetFileNameWithoutExtension(target.SshKeyPath);
            if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(baseName))
            {
                return Path.Combine(dir, $"{baseName}_rotated_{stamp}");
            }
        }

        string sshDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh");
        string safeHost = new string((target.RemoteServer ?? "host")
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
            .ToArray());
        return Path.Combine(sshDir, $"id_ed25519_{safeHost}_{stamp}");
    }
}
