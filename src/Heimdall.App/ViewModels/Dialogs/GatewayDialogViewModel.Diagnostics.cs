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
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Ssh;

namespace Heimdall.App.ViewModels.Dialogs;

public partial class GatewayDialogViewModel
{
    private string _diagnosticGatewayId = Guid.NewGuid().ToString();
    private IReadOnlyList<SshGatewayDto> _diagnosticGateways = [];
    private Func<IReadOnlyList<SshGatewayDto>, string?, int, IProgress<GatewayDiagnosticStep>, CancellationToken,
        Task<IReadOnlyList<GatewayDiagnosticStep>>>? _diagnose;
    private CancellationTokenSource? _diagnosticCancellation;
    private bool _diagnosticsClosed;
    private int _diagnosticRevision;

    [ObservableProperty] private bool _isDiagnosticRunning;
    [ObservableProperty] private bool _diagnosticsReady;
    [ObservableProperty] private string _diagnosticTargetHost = "";
    [ObservableProperty] private string _diagnosticTargetPort = "";
    [ObservableProperty] private string _diagnosticStatus = "";
    [ObservableProperty] private string _diagnosticReport = "";
    public bool CanEditGateway => !IsDiagnosticRunning;
    public bool CanStartDiagnostic => CanTestGatewayRoute();
    public bool HasDiagnosticReport => !IsDiagnosticRunning && !string.IsNullOrWhiteSpace(DiagnosticReport);
    public string FullDiagnosticRoute
    {
        get
        {
            try
            {
                string route = string.Join(" → ", BuildDiagnosticRoute(false).Select(g => $"{g.Name} ({g.Host}:{g.Port})"));
                return $"{Text("GatewayDiagnosticWorkstation")} → {route}"
                    + (string.IsNullOrWhiteSpace(DiagnosticTargetHost) ? "" : $" → {DiagnosticTargetHost}:{DiagnosticTargetPort}");
            }
            catch (Exception ex) when (ex is ArgumentException or GatewayChainException)
            {
                return Text("GatewayDiagnosticInvalidRoute");
            }
        }
    }

    /// <summary>Installs a read-only inventory snapshot and an isolated diagnostic runner.</summary>
    public void ConfigureDiagnostics(IReadOnlyList<SshGatewayDto> gateways,
        Func<IReadOnlyList<SshGatewayDto>, string?, int, IProgress<GatewayDiagnosticStep>, CancellationToken,
            Task<IReadOnlyList<GatewayDiagnosticStep>>> diagnose)
    {
        if (_diagnosticsClosed) return;
        _diagnosticGateways = gateways.Select(g => g.CloneFaithfully()).ToArray();
        _diagnose = diagnose;
        DiagnosticsReady = true;
        OnPropertyChanged(nameof(FullDiagnosticRoute));
    }

    internal IReadOnlyList<SshGatewayDto> BuildDiagnosticRoute(bool includeCredentials)
    {
        SshGatewayDto draft = includeCredentials ? ToDto() : new SshGatewayDto
        {
            Name = Name,
            Host = Host,
            Port = Port,
            User = User,
            ParentGatewayId = string.IsNullOrWhiteSpace(SelectedParentGatewayId) ? null : SelectedParentGatewayId
        };
        draft.Id = _diagnosticGatewayId;
        List<SshGatewayDto> inventory = _diagnosticGateways
            .Where(g => !string.Equals(g.Id, draft.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        inventory.Add(draft);
        return GatewayChainResolver.ResolveChainDtos(draft.Id, inventory);
    }

    private bool CanTestGatewayRoute() => DiagnosticsReady && !IsDiagnosticRunning && !_diagnosticsClosed;

    [RelayCommand(CanExecute = nameof(CanTestGatewayRoute))]
    private async Task TestGatewayRouteAsync()
    {
        if (_diagnose is null || !CanTestGatewayRoute()) return;
        ValidateCommand.Execute(null);
        if (ValidationError is not null) return;
        int targetPort = 0;
        if (!string.IsNullOrWhiteSpace(DiagnosticTargetHost)
            && (!int.TryParse(DiagnosticTargetPort, out targetPort) || targetPort is < 1 or > 65535
                || Uri.CheckHostName(DiagnosticTargetHost) == UriHostNameType.Unknown))
        {
            DiagnosticStatus = Text("GatewayDiagnosticInvalidTarget");
            return;
        }

        int revision = ++_diagnosticRevision;
        using CancellationTokenSource cancellation = new();
        _diagnosticCancellation = cancellation;
        IsDiagnosticRunning = true;
        DiagnosticReport = "";
        DiagnosticStatus = Text("GatewayDiagnosticRunning");
        try
        {
            IReadOnlyList<SshGatewayDto> route = BuildDiagnosticRoute(true);
            List<GatewayDiagnosticStep> observed = [];
            Progress<GatewayDiagnosticStep> progress = new(step =>
            {
                if (!_diagnosticsClosed && revision == _diagnosticRevision && IsDiagnosticRunning)
                {
                    DiagnosticStatus = FormatStep(step);
                    if (!step.IsRunning)
                    {
                        observed.Add(step);
                        DiagnosticReport = BuildDiagnosticReport(observed, !string.IsNullOrWhiteSpace(DiagnosticTargetHost));
                    }
                }
            });
            IReadOnlyList<GatewayDiagnosticStep> steps = await _diagnose(route,
                string.IsNullOrWhiteSpace(DiagnosticTargetHost) ? null : DiagnosticTargetHost,
                targetPort, progress, cancellation.Token);
            if (_diagnosticsClosed || revision != _diagnosticRevision) return;
            DiagnosticReport = BuildDiagnosticReport(steps, !string.IsNullOrWhiteSpace(DiagnosticTargetHost));
            DiagnosticStatus = steps.Count == 0 ? Text("GatewayDiagnosticUnavailable") : FormatStep(steps[^1]);
        }
        catch (Exception ex)
        {
            if (_diagnosticsClosed || revision != _diagnosticRevision) return;
            // Neither UI nor clipboard receives raw exception messages (they may contain secrets).
            DiagnosticStatus = ex is OperationCanceledException ? Text("GatewayDiagnosticCancelled")
                : ex is ArgumentException or GatewayChainException ? Text("GatewayDiagnosticInvalidRoute")
                : Text("GatewayDiagnosticUnavailable");
            FileLogger.Warn($"Gateway diagnostic setup failed: {ex.GetType().Name}");
        }
        finally
        {
            _diagnosticCancellation = null;
            IsDiagnosticRunning = false;
        }
    }

    [RelayCommand]
    private void CancelGatewayDiagnostic() => _diagnosticCancellation?.Cancel();

    /// <summary>Prevents late UI updates and cancels work when the dialog actually closes.</summary>
    public void CloseDiagnostics()
    {
        _diagnosticsClosed = true;
        _diagnosticRevision++;
        _diagnosticCancellation?.Cancel();
        TestGatewayRouteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Copies only typed results; endpoint names, accounts, key paths and raw errors are excluded.</summary>
    public void CopyDiagnosticReport(IClipboardService clipboard)
    {
        if (string.IsNullOrWhiteSpace(DiagnosticReport)) return;
        try { clipboard.SetText(DiagnosticReport); }
        catch (Exception ex)
        {
            FileLogger.Warn($"Gateway diagnostic clipboard failed: {ex.GetType().Name}");
            DiagnosticStatus = Text("GatewayDiagnosticCopyFailed");
        }
    }

    internal string BuildDiagnosticReport(IReadOnlyList<GatewayDiagnosticStep> steps, bool hasTarget)
    {
        StringBuilder text = new(Text("GatewayDiagnosticReportHeader"));
        text.AppendLine().AppendLine(DateTimeOffset.UtcNow.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        foreach (GatewayDiagnosticStep step in steps) text.AppendLine(FormatStep(step));
        text.AppendLine(Text(hasTarget ? "GatewayDiagnosticTcpOnly" : "GatewayDiagnosticNoTarget"));
        return text.ToString();
    }

    private string FormatStep(GatewayDiagnosticStep step)
    {
        string label = step.IsTarget ? Text("GatewayDiagnosticTargetStep")
            : Localizer?.Format("GatewayDiagnosticHopStep", step.Hop) ?? $"GatewayDiagnosticHopStep {step.Hop}";
        string outcome = Text(step.IsRunning ? "GatewayDiagnosticRunning"
            : step.Success ? "GatewayDiagnosticSuccess" : FailureKey(step.Failure));
        return $"{label}: {outcome} ({step.ElapsedMilliseconds} ms)";
    }

    private static string FailureKey(SshFailureCode? code) => code switch
    {
        SshFailureCode.HostKeyUnavailable => "GatewayDiagnosticTrustRequired",
        SshFailureCode.HostKeyMismatch => "GatewayDiagnosticTrustChanged",
        SshFailureCode.NetworkTimedOut => "GatewayDiagnosticTimeout",
        SshFailureCode.NetworkRefused or SshFailureCode.NetworkUnreachable => "GatewayDiagnosticNetwork",
        SshFailureCode.ForwardingFailed => "GatewayDiagnosticForwarding",
        SshFailureCode.Cancelled => "GatewayDiagnosticCancelled",
        SshFailureCode.KeyboardInteractiveUnsupportedPrompt => "GatewayDiagnosticInteractive",
        SshFailureCode.AuthRejected or SshFailureCode.PasswordRejected or SshFailureCode.KeyRejected
            or SshFailureCode.PassphraseRejected or SshFailureCode.PassphraseRequired
            or SshFailureCode.KeyFileInvalid or SshFailureCode.KeyFileNotFound or SshFailureCode.NoSupportedAuth
            or SshFailureCode.PageantKeyUnavailable or SshFailureCode.PageantNoIdentities
            or SshFailureCode.TooManyAuthFailures or SshFailureCode.KeyboardInteractiveNoPassword => "GatewayDiagnosticAuth",
        _ => "GatewayDiagnosticUnavailable"
    };

    private string Text(string key) => Localizer?[key] ?? key;

    internal void InvalidateDiagnosticResult()
    {
        _diagnosticRevision++;
        _diagnosticCancellation?.Cancel();
        DiagnosticReport = "";
        DiagnosticStatus = "";
        OnPropertyChanged(nameof(FullDiagnosticRoute));
    }
    partial void OnDiagnosticTargetHostChanged(string value) => InvalidateDiagnosticResult();
    partial void OnDiagnosticTargetPortChanged(string value) => InvalidateDiagnosticResult();
    partial void OnDiagnosticReportChanged(string value) => OnPropertyChanged(nameof(HasDiagnosticReport));
    partial void OnDiagnosticsReadyChanged(bool value)
    {
        TestGatewayRouteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStartDiagnostic));
    }
    partial void OnIsDiagnosticRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditGateway));
        OnPropertyChanged(nameof(HasDiagnosticReport));
        OnPropertyChanged(nameof(CanStartDiagnostic));
        TestGatewayRouteCommand.NotifyCanExecuteChanged();
    }
}
