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

using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Network;
using Heimdall.Core.Security;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// ViewModel for the SNMP walker tool.
/// </summary>
public sealed partial class SnmpWalkerViewModel : ObservableObject, IDisposable
{
    private readonly ISnmpWalkerService _service;
    private readonly List<SnmpEntry> _entries = [];
    private readonly List<CommunityResult> _communityResults = [];
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private LocalizationManager? _localizer;
    private bool _hasGateway;

    [ObservableProperty] private bool _isWalking;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _showError;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _showResults;

    public SnmpWalkerViewModel(ISnmpWalkerService? service = null)
    {
        _service = service ?? new SnmpWalkerService();
    }

    public void Initialize(LocalizationManager? localizer)
    {
        _localizer = localizer;
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _hasGateway = gateway is not null;
        _service.SetGateway(gateway);
    }

    public async Task WalkAsync(string host, string community, string startOid)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            lock (_lock)
            {
                _entries.Clear();
            }

            SetError(Lk("ToolValidationHostRequired"));
            return;
        }

        host = host.Trim();

        if (string.IsNullOrWhiteSpace(community))
        {
            community = NetworkToolPresets.SnmpDefaultCommunity;
        }

        startOid = string.IsNullOrWhiteSpace(startOid)
            ? NetworkToolPresets.SnmpDefaultOid
            : startOid.Trim();

        // A value starting with "-" would be read as an option by the remote snmpwalk command.
        // The direct walk only understands numeric OIDs, so a symbolic one needs a gateway.
        var invalidKey = !InputValidator.IsValidDiagnosticHost(host)
            ? "ErrorInvalidHost"
            : !InputValidator.Validate(startOid, InputValidator.SnmpOidPatternName)
                || (!_hasGateway && !InputValidator.Validate(startOid, InputValidator.SnmpNumericOidPatternName))
                ? "ErrorInvalidSnmpOid"
                : null;
        if (invalidKey is not null)
        {
            lock (_lock)
            {
                _entries.Clear();
            }

            SetError(Lk(invalidKey));
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        lock (_lock)
        {
            _entries.Clear();
        }

        IsWalking = true;
        ShowResults = true;
        ShowError = false;
        ErrorText = string.Empty;
        StatusText = string.Empty;

        try
        {
            var progress = new Progress<SnmpWalkProgress>(update =>
            {
                StatusText = string.Format(Lk("ToolSnmpProgress"), update.EntryCount);
            });
            Action<SnmpWalkProgress> report = ((IProgress<SnmpWalkProgress>)progress).Report;

            var results = _hasGateway
                ? await _service.WalkViaTunnelAsync(host, community, startOid, report, _cts.Token)
                : await _service.WalkDirectAsync(host, community, startOid, SnmpCodec.DefaultTimeoutMs, report, _cts.Token);

            lock (_lock)
            {
                _entries.Clear();
                _entries.AddRange(results);
            }

            StatusText = string.Format(Lk("ToolSnmpProgress"), _entries.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText = string.Format(Lk("ToolSnmpProgress"), GetEntries().Count);
        }
        catch (Exception ex)
        {
            if (GetEntries().Count == 0)
            {
                SetError(string.Format(Lk("ToolSnmpErrorConnection"), ex.Message));
            }
            else
            {
                StatusText = string.Format(Lk("ToolSnmpProgress"), GetEntries().Count);
            }
        }
        finally
        {
            IsWalking = false;
        }
    }

    public async Task TestCommunitiesAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            lock (_lock)
            {
                _communityResults.Clear();
            }

            SetError(Lk("ToolValidationHostRequired"));
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        lock (_lock)
        {
            _communityResults.Clear();
        }

        ShowError = false;
        ErrorText = string.Empty;
        IsWalking = true;

        try
        {
            var results = await _service.TestCommunitiesAsync(host, CreateLocalize(), _cts.Token);
            lock (_lock)
            {
                _communityResults.Clear();
                _communityResults.AddRange(results);
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled.
        }
        finally
        {
            IsWalking = false;
        }
    }

    public void CancelWalk()
    {
        _cts?.Cancel();
    }

    public IReadOnlyList<SnmpEntry> GetEntries()
    {
        lock (_lock)
        {
            return [.. _entries];
        }
    }

    public IReadOnlyList<CommunityResult> GetCommunityResults()
    {
        lock (_lock)
        {
            return [.. _communityResults];
        }
    }

    public string BuildCsvExport()
    {
        return SnmpCodec.BuildCsvExport(GetEntries(), CreateLocalize());
    }

    public string BuildClipboardText()
    {
        var entries = GetEntries();
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{Lk("ToolSnmpColOid"),-40}{Lk("ToolSnmpColName"),-24}{Lk("ToolSnmpColType"),-16}{Lk("ToolSnmpColValue")}");
        builder.AppendLine(new string('-', 90));
        foreach (var entry in entries)
        {
            builder.AppendLine($"{entry.Oid,-40}{entry.Name,-24}{entry.Type,-16}{entry.Value}");
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void SetError(string message)
    {
        ShowResults = false;
        ShowError = true;
        ErrorText = message;
        StatusText = message;
    }

    private string Lk(string key) => _localizer?[key] ?? key;

    private Func<string, string> CreateLocalize() => key => _localizer?[key] ?? key;
}
