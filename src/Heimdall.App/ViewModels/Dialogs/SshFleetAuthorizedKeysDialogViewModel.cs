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
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>Selectable key row displayed by <see cref="Views.Dialogs.SshFleetAuthorizedKeysDialog"/>.</summary>
public sealed partial class SshFleetKeyRow : ObservableObject
{
    public SshFleetKeyRow(
        SshFleetAuthorizedKeysTarget target,
        string host,
        int port,
        AuthorizedKeyEntry entry,
        int totalKeysOnHost)
    {
        Target = target;
        Host = host;
        Port = port;
        Entry = entry;
        TotalKeysOnHost = totalKeysOnHost;
        IsLastKeyOnHost = totalKeysOnHost <= 1;
    }

    public SshFleetAuthorizedKeysTarget Target { get; }

    public string ServerId => Target.ServerId;

    public string DisplayName => Target.DisplayName;

    public string Host { get; }

    public int Port { get; }

    public string Endpoint => $"{Host}:{Port}";

    public AuthorizedKeyEntry Entry { get; }

    public int LineNumber => Entry.LineNumber;

    public string KeyType => Entry.KeyType;

    public string Fingerprint => Entry.Fingerprint;

    public string Options => Entry.Options;

    public string Comment => Entry.Comment;

    public int TotalKeysOnHost { get; }

    public bool IsLastKeyOnHost { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// View-model for searching <c>~/.ssh/authorized_keys</c> across all configured SSH servers,
/// exporting audit reports, and revoking matched keys selectively.
/// </summary>
public sealed partial class SshFleetAuthorizedKeysDialogViewModel : ObservableObject
{
    private readonly IConfigManager _configManager;
    private readonly LocalizationManager _localizer;
    private readonly IServiceProvider? _services;
    private readonly IDialogService? _dialogService;
    private readonly Func<IReadOnlyList<SshFleetAuthorizedKeysTarget>, string?, CancellationToken, Task<IReadOnlyList<SshFleetAuthorizedKeysHostResult>>>? _scanOverride;
    private readonly Func<IReadOnlyList<(SshFleetAuthorizedKeysTarget Target, AuthorizedKeyEntry Entry)>, bool, CancellationToken, Task<IReadOnlyList<SshFleetKeyRevocationResult>>>? _revokeOverride;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<SshFleetAuthorizedKeysHostResult> _lastHostResults = [];
    private string _lastExecutedQuery = string.Empty;

    public SshFleetAuthorizedKeysDialogViewModel(
        IServiceProvider? services,
        IConfigManager configManager,
        LocalizationManager localizer,
        string? initialQuery = null,
        IDialogService? dialogService = null,
        Func<IReadOnlyList<SshFleetAuthorizedKeysTarget>, string?, CancellationToken, Task<IReadOnlyList<SshFleetAuthorizedKeysHostResult>>>? scanOverride = null,
        Func<IReadOnlyList<(SshFleetAuthorizedKeysTarget Target, AuthorizedKeyEntry Entry)>, bool, CancellationToken, Task<IReadOnlyList<SshFleetKeyRevocationResult>>>? revokeOverride = null)
    {
        _services = services;
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _dialogService = dialogService ?? services?.GetService<IDialogService>();
        _scanOverride = scanOverride;
        _revokeOverride = revokeOverride;
        _searchQuery = initialQuery?.Trim() ?? string.Empty;
    }

    public ObservableCollection<SshFleetKeyRow> Matches { get; } = [];

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(CanRevokeSelected))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    public bool CanInteract => !IsBusy;

    public bool HasSummary => !string.IsNullOrWhiteSpace(SummaryText);

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool CanExport => !IsBusy && _lastHostResults.Count > 0;

    public bool CanRevokeSelected => !IsBusy && Matches.Any(static m => m.IsSelected);

    public bool HasMatches => Matches.Count > 0;

    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(CanRevokeSelected));
    }

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
    public async Task ScanFleetAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        IsBusy = true;
        IsStatusError = false;
        StatusMessage = string.Empty;
        Matches.Clear();
        _lastHostResults = [];
        NotifyCountsChanged();

        try
        {
            AppSettings settings = await _configManager.LoadSettingsAsync().ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<ServerProfileDto> allServers = await _configManager.LoadServersAsync().ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();

            List<ServerProfileDto> sshServers = allServers
                .Where(static s => string.Equals(s.ConnectionType, "SSH", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(s.RemoteServer))
                .ToList();

            if (sshServers.Count == 0)
            {
                ReportStatus(_localizer["SshFleetAuthKeysNoServers"], isError: true);
                return;
            }

            ITunnelService? tunnelService = _services?.GetService<ITunnelService>();
            var targets = sshServers.Select(s => BuildTarget(s, settings, tunnelService, _services)).ToList();
            string query = SearchQuery?.Trim() ?? string.Empty;
            _lastExecutedQuery = query;

            IReadOnlyList<SshFleetAuthorizedKeysHostResult> results;
            if (_scanOverride is not null)
            {
                results = await _scanOverride(targets, query, ct).ConfigureAwait(true);
            }
            else
            {
                HostKeyStore? hostKeyStore = _services?.GetService<HostKeyStore>();
                IHostKeyVerifier? hostKeyVerifier = _services?.GetService<IHostKeyVerifier>();
                if (hostKeyStore is null || hostKeyVerifier is null)
                {
                    ReportStatus(_localizer["ToolSshKeyGenDeployNoServers"], isError: true);
                    return;
                }

                results = await SshFleetAuthorizedKeysService.ScanFleetAsync(
                        targets,
                        query,
                        hostKeyStore,
                        hostKeyVerifier,
                        SshFleetAuthorizedKeysService.DefaultMaxConcurrency,
                        listRunner: null,
                        ct)
                    .ConfigureAwait(true);
            }

            _lastHostResults = results;
            PopulateMatchRows(results, targets);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ReportStatus(_localizer["ToolSshServerAuditCancelled"], isError: true);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshFleetAuthorizedKeys] scan failed: {ex.Message}");
            ReportStatus(ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
            NotifyCountsChanged();
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (SshFleetKeyRow row in Matches)
        {
            row.IsSelected = true;
        }

        OnPropertyChanged(nameof(CanRevokeSelected));
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (SshFleetKeyRow row in Matches)
        {
            row.IsSelected = false;
        }

        OnPropertyChanged(nameof(CanRevokeSelected));
    }

    [RelayCommand]
    public async Task RevokeSelectedAsync()
    {
        if (IsBusy)
        {
            return;
        }

        List<SshFleetKeyRow> selectedRows = Matches.Where(static r => r.IsSelected).ToList();
        if (selectedRows.Count == 0 || _dialogService is null)
        {
            return;
        }

        int distinctHosts = selectedRows.Select(static r => r.ServerId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        bool anyLastKeyRisk = selectedRows
            .GroupBy(static r => r.ServerId, StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Any(r => r.IsLastKeyOnHost) || g.Count() >= g.First().TotalKeysOnHost);

        string entrySummary = _localizer.FormatCount(
            selectedRows.Count,
            "SshFleetAuthKeysEntryCountOne",
            "SshFleetAuthKeysEntryCount",
            selectedRows.Count);
        string serverSummary = _localizer.FormatCount(
            distinctHosts,
            "SshFleetAuthKeysServerCountOne",
            "SshFleetAuthKeysServerCount",
            distinctHosts);
        string confirmBody = _localizer.Format(
            "SshFleetAuthKeysConfirmRevoke",
            entrySummary,
            serverSummary);

        bool confirmed = await _dialogService.ShowConfirmAsync(
                _localizer["SshFleetAuthKeysDialogTitle"],
                confirmBody,
                "warning",
                _localizer["SshFleetAuthKeysBtnRevoke"],
                _localizer["BtnCancel"])
            .ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        bool allowRemoveLastKey = false;
        if (anyLastKeyRisk)
        {
            allowRemoveLastKey = await _dialogService.ShowConfirmAsync(
                    _localizer["SshFleetAuthKeysDialogTitle"],
                    _localizer["SshFleetAuthKeysConfirmRevokeLastKeyWarning"],
                    "warning",
                    _localizer["SshFleetAuthKeysBtnRevoke"],
                    _localizer["BtnCancel"])
                .ConfigureAwait(true);
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        IsBusy = true;
        try
        {
            var selections = selectedRows
                .Select(static r => (r.Target, r.Entry))
                .ToList();

            IReadOnlyList<SshFleetKeyRevocationResult> results;
            if (_revokeOverride is not null)
            {
                results = await _revokeOverride(selections, allowRemoveLastKey, ct).ConfigureAwait(true);
            }
            else
            {
                HostKeyStore? hostKeyStore = _services?.GetService<HostKeyStore>();
                IHostKeyVerifier? hostKeyVerifier = _services?.GetService<IHostKeyVerifier>();
                if (hostKeyStore is null || hostKeyVerifier is null)
                {
                    ReportStatus(_localizer["ToolSshKeyGenDeployNoServers"], isError: true);
                    return;
                }

                results = await SshFleetAuthorizedKeysService.RevokeSelectedAsync(
                        selections,
                        allowRemoveLastKey,
                        hostKeyStore,
                        hostKeyVerifier,
                        SshFleetAuthorizedKeysService.DefaultMaxConcurrency,
                        revokeRunner: null,
                        ct)
                    .ConfigureAwait(true);
            }

            int revokedCount = 0;
            int failedCount = 0;
            int lockoutBlocked = 0;
            foreach (SshFleetKeyRevocationResult outcome in results)
            {
                if (outcome.Mutation.Success)
                {
                    revokedCount++;
                    SshFleetKeyRow? row = Matches.FirstOrDefault(r =>
                        string.Equals(r.ServerId, outcome.ServerId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.Entry.RawLine, outcome.Entry.RawLine, StringComparison.Ordinal));
                    if (row is not null)
                    {
                        Matches.Remove(row);
                    }
                }
                else
                {
                    failedCount++;
                    if (outcome.Mutation.ErrorCode == SshAuthorizedKeysErrorCode.LastKeyProtected)
                    {
                        lockoutBlocked++;
                    }
                }
            }

            if (failedCount == 0)
            {
                ReportStatus(
                    _localizer.FormatCount(
                        revokedCount,
                        "SshFleetAuthKeysRevokeDoneOne",
                        "SshFleetAuthKeysRevokeDone",
                        revokedCount),
                    isError: false);
            }
            else
            {
                string revokedSummary = _localizer.FormatCount(
                    revokedCount,
                    "SshFleetAuthKeysRevokedCountOne",
                    "SshFleetAuthKeysRevokedCount",
                    revokedCount);
                string failedSummary = _localizer.FormatCount(
                    failedCount,
                    "SshFleetAuthKeysFailedCountOne",
                    "SshFleetAuthKeysFailedCount",
                    failedCount);
                if (lockoutBlocked > 0)
                {
                    string lockoutSummary = _localizer.FormatCount(
                        lockoutBlocked,
                        "SshFleetAuthKeysLockoutBlockedCountOne",
                        "SshFleetAuthKeysLockoutBlockedCount",
                        lockoutBlocked);
                    ReportStatus(
                        _localizer.Format(
                            "SshFleetAuthKeysRevokePartialLockout",
                            revokedSummary,
                            failedSummary,
                            lockoutSummary),
                        isError: true);
                }
                else
                {
                    ReportStatus(
                        _localizer.Format("SshFleetAuthKeysRevokePartial", revokedSummary, failedSummary),
                        isError: true);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ReportStatus(_localizer["ToolSshServerAuditCancelled"], isError: true);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshFleetAuthorizedKeys] revocation failed: {ex.Message}");
            ReportStatus(ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
            NotifyCountsChanged();
        }
    }

    public string BuildCsvReport()
        => SshFleetAuthorizedKeysService.FormatCsvReport(_lastHostResults, _lastExecutedQuery);

    public string BuildMarkdownReport()
        => SshFleetAuthorizedKeysService.FormatMarkdownReport(_lastHostResults, _lastExecutedQuery);

    private void PopulateMatchRows(
        IReadOnlyList<SshFleetAuthorizedKeysHostResult> results,
        IReadOnlyList<SshFleetAuthorizedKeysTarget> targets)
    {
        var targetMap = targets.ToDictionary(static t => t.ServerId, StringComparer.OrdinalIgnoreCase);

        foreach (SshFleetAuthorizedKeysHostResult hostResult in results)
        {
            if (!hostResult.Success || !targetMap.TryGetValue(hostResult.ServerId, out var target))
            {
                continue;
            }

            foreach (AuthorizedKeyEntry entry in hostResult.MatchedEntries)
            {
                var row = new SshFleetKeyRow(
                    target,
                    hostResult.Host,
                    hostResult.Port,
                    entry,
                    hostResult.TotalKeyCount);
                Matches.Add(row);
            }
        }

        int totalHosts = results.Count;
        int succeeded = results.Count(static r => r.Success);
        int errors = totalHosts - succeeded;
        int matchCount = Matches.Count;

        SummaryText = _localizer.Format(
            "SshFleetAuthKeysSummary",
            matchCount,
            succeeded,
            totalHosts,
            errors);

        if (matchCount == 0 && errors == 0)
        {
            ReportStatus(_localizer["SshFleetAuthKeysNoMatches"], isError: false);
        }
        else if (errors > 0)
        {
            ReportStatus(
                _localizer.FormatCount(
                    errors,
                    "SshFleetAuthKeysScanWithErrorsOne",
                    "SshFleetAuthKeysScanWithErrors",
                    errors),
                isError: true);
        }
    }

    private void NotifyCountsChanged()
    {
        OnPropertyChanged(nameof(HasMatches));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanRevokeSelected));
    }

    private void ReportStatus(string message, bool isError)
    {
        IsStatusError = isError;
        StatusMessage = message;
    }

    private static SshFleetAuthorizedKeysTarget BuildTarget(
        ServerProfileDto server,
        AppSettings settings,
        ITunnelService? tunnelService,
        IServiceProvider? services)
    {
        int targetPort = server.SshPort > 0 && Heimdall.Core.Security.InputValidator.ValidatePortRange(server.SshPort)
            ? server.SshPort
            : DefaultPorts.Ssh;
        AppSettings effectiveSettings = settings;
        string displayName = string.IsNullOrWhiteSpace(server.DisplayName) ? server.RemoteServer : server.DisplayName;

        SshConnectionParams baseParams = BuildConnParams(
            server,
            effectiveSettings,
            connectHost: server.RemoteServer.Trim(),
            connectPort: targetPort,
            logicalHost: null,
            logicalPort: null);

        Func<CancellationToken, Task<(SshConnectionParams ResolvedParams, IDisposable? Lease)>>? resolver = null;
        if (SshGatewayRoute.GatewayIdFor(server, effectiveSettings.GroupDefaults) is not null && tunnelService is not null)
        {
            resolver = async ct =>
            {
                var outcome = await tunnelService.SetupTunnelIfNeededAsync(server, targetPort, effectiveSettings, ct)
                    .ConfigureAwait(false);
                if (!outcome.Success)
                {
                    throw new InvalidOperationException(outcome.ErrorMessage ?? "Tunnel setup failed.");
                }

                if (outcome.UsesTunnel && outcome.Port > 0)
                {
                    IDisposable lease = new TunnelLease(tunnelService, usesTunnel: true, outcome.Port, outcome.TunnelLeaseId);
                    var tunnelParams = BuildConnParams(
                        server,
                        effectiveSettings,
                        connectHost: outcome.Host,
                        connectPort: outcome.Port,
                        logicalHost: server.RemoteServer.Trim(),
                        logicalPort: targetPort);
                    return (tunnelParams, lease);
                }

                return (baseParams, (IDisposable?)null);
            };
        }

        return new SshFleetAuthorizedKeysTarget(server.Id, displayName, baseParams, resolver);
    }

    private static SshConnectionParams BuildConnParams(
        ServerProfileDto target,
        AppSettings settings,
        string connectHost,
        int connectPort,
        string? logicalHost,
        int? logicalPort)
        => new()
        {
            Host = connectHost,
            Port = connectPort,
            LogicalHost = logicalHost,
            LogicalPort = logicalPort,
            Username = target.SshUsername ?? string.Empty,
            KeyPath = string.IsNullOrWhiteSpace(target.SshKeyPath) ? null : target.SshKeyPath,
            CertificatePath = string.IsNullOrWhiteSpace(target.SshCertificatePath) ? null : target.SshCertificatePath,
            Password = ConnectionHelpers.DecryptPassword(target.SshPasswordEncrypted),
            KeyPassphrase = ConnectionHelpers.DecryptPassword(target.SshKeyPassphraseEncrypted),
            SshAgentPreference = settings.SshAgentPreference,
            AgentKeyFingerprint = target.SshAgentKeyFingerprint,
            UseLegacyPasswordAsKeyPassphrase = target.UsesLegacySshCredentialMapping,
            LegacyCredentialName = target.DisplayName,
            CryptoPolicy = SshCryptoPolicyResolver.Resolve(target.SshCryptoPolicy, settings.SshCryptoPolicy),
            ProxyType = logicalHost is not null ? SshProxyType.None : target.SshProxyType,
            ProxyHost = logicalHost is not null || string.IsNullOrWhiteSpace(target.SshProxyHost) ? null : target.SshProxyHost.Trim(),
            ProxyPort = logicalHost is not null ? 0 : target.SshProxyPort,
            ProxyUsername = logicalHost is not null || string.IsNullOrWhiteSpace(target.SshProxyUsername) ? null : target.SshProxyUsername.Trim(),
            ProxyPassword = logicalHost is not null ? null : ConnectionHelpers.DecryptPassword(target.SshProxyPasswordEncrypted),
            ConnectTimeout = TimeSpan.FromMilliseconds(settings.HostKeyProbeTimeoutMs)
        };
}
