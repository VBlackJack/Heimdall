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

using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Certificates;
using Heimdall.Core.Codecs;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Microsoft.Web.WebView2.Core;

namespace Heimdall.App.Views;

/// <summary>
/// A web console in a session tab, rendered by WebView2 in the browser profile all web tabs
/// share (see <see cref="WebView2Helper.GetWebConsoleEnvironmentAsync"/>).
/// </summary>
public partial class EmbeddedWebView : UserControl, IDisposable, ISessionPaneOwner
{
    private readonly HashSet<string> _answeredChallenges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<FtpsCertificateDecision>> _pendingCertificatePrompts =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejectedCertificates = new(StringComparer.Ordinal);

    private WebSessionResult? _session;
    private SessionTabViewModel? _sessionTab;
    private LocalizationManager? _localizer;
    private SessionPaneModel? _ownerPane;
    private bool _localeChangeSubscribed;
    private bool _connectedRaised;
    private bool _disposed;

    /// <summary>Raised when the console's first page has loaded. Parameter: ServerId.</summary>
    public event Action<string>? SessionConnected;

    /// <summary>Raised when the console cannot be shown. Parameters: ServerId, message.</summary>
    public event Action<string, string>? SessionError;

    /// <summary>
    /// Raised when the user retries after a failure, before the console is opened again, so the
    /// row can leave its error state. Parameter: ServerId.
    /// </summary>
    public event Action<string>? SessionRetrying;

    /// <summary>Raised to request a split pane. Parameter: SessionTab.</summary>
    public event Action<SessionTabViewModel?>? RequestSplit;

    /// <summary>The pins the user gave to consoles the browser refused; set by the session manager.</summary>
    public WebCertificateTrust? CertificateTrust { get; set; }

    /// <summary>The zoom each console was last left at; set by the session manager.</summary>
    public WebZoomMemory? ZoomMemory { get; set; }

    /// <summary>The pane this view lives in, wherever a split, a merge or a detach moved it.</summary>
    internal SessionPaneModel? OwningPane => _ownerPane;

    public EmbeddedWebView()
    {
        InitializeComponent();

        // The console is a page Heimdall does not author, so the release gesture is caught on
        // its way to the page: WebView2 hands accelerator keys to WPF before the page sees them.
        ConsoleWebView.PreviewKeyDown += OnConsoleWebViewPreviewKeyDown;

        if (TryFindResource("BackgroundColor") is System.Windows.Media.Color themeColor)
        {
            ConsoleWebView.DefaultBackgroundColor =
                System.Drawing.Color.FromArgb(themeColor.A, themeColor.R, themeColor.G, themeColor.B);
        }
    }

    private void OnConsoleWebViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (WebViewFocusRelease.IsGesture(e.Key, e.SystemKey, Keyboard.Modifiers))
        {
            e.Handled = true;
            ReloadButton.Focus();
        }
    }

    public void SetOwningPane(SessionPaneModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        _ownerPane = pane;
    }

    /// <summary>Opens the console's address in this view.</summary>
    public async Task InitializeSessionAsync(
        WebSessionResult session,
        SessionTabViewModel sessionTab,
        string displayName,
        LocalizationManager? localizer)
    {
        _session = session;
        _sessionTab = sessionTab;
        _localizer = localizer;

        if (_localizer is not null && !_localeChangeSubscribed)
        {
            _localizer.LocaleChanged += OnLocaleChanged;
            _localeChangeSubscribed = true;
        }

        SessionTitleText.Text = displayName;
        AddressTextBlock.Text = session.Address.AbsoluteUri;
        StatusTextBlock.Text = Text("StatusWebConnecting");
        Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);

        // Behind a gateway the console is only reachable through the tunnel, which an external
        // program would not use: the way out is not offered where it cannot lead anywhere.
        Visibility external = session.CanOpenExternally ? Visibility.Visible : Visibility.Collapsed;
        OpenExternallyButton.Visibility = external;
        FallbackOpenExternallyButton.Visibility = external;
        FallbackOldConsoleHint.Visibility = external;

        ApplyLocalization();

        await InitializeWebViewAsync();
    }

    private void ApplyLocalization()
    {
        ReloadButton.ToolTip = Text("TooltipWebReload");
        BackButton.ToolTip = Text("TooltipWebBack");
        SplitButton.ToolTip = Text("TooltipSplitSession");
        FallbackRetryButton.Content = Text("BtnWebRetry");
        FallbackOpenExternallyButton.Content = Text("BtnWebOpenExternally");
        FallbackOldConsoleHint.Text = Text("WebFallbackOldConsoleHint");
        OpenExternallyButton.ToolTip = Text("TooltipWebOpenExternally");
        System.Windows.Automation.AutomationProperties.SetName(OpenExternallyButton, Text("TooltipWebOpenExternally"));
        System.Windows.Automation.AutomationProperties.SetName(FallbackOpenExternallyButton, Text("BtnWebOpenExternally"));

        FallbackTitleText.Text = Text("WebFallbackTitle");
        System.Windows.Automation.AutomationProperties.SetName(ReloadButton, Text("TooltipWebReload"));
        System.Windows.Automation.AutomationProperties.SetName(BackButton, Text("TooltipWebBack"));
        System.Windows.Automation.AutomationProperties.SetName(SplitButton, Text("A11ySplitSession"));
        System.Windows.Automation.AutomationProperties.SetName(FallbackRetryButton, Text("BtnWebRetry"));
        System.Windows.Automation.AutomationProperties.SetHelpText(StatusTextBlock, Text("A11yConnectionStatus"));
    }

    private void OnLocaleChanged(string locale)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLocaleChanged(locale));
            return;
        }

        ApplyLocalization();
        if (_connectedRaised && FallbackPanel.Visibility != Visibility.Visible)
        {
            StatusTextBlock.Text = Text("StatusWebConnected");
            Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            if (!WebView2Helper.IsAvailable)
            {
                ReportFailure(Text("ErrorWebView2NotFound"));
                return;
            }

            // A console behind a gateway goes through that gateway's SOCKS proxy, in its own profile.
            CoreWebView2Environment environment = await WebView2Helper.GetWebConsoleEnvironmentAsync(
                _session?.GatewayId,
                _session?.ProxyPort);
            await ConsoleWebView.EnsureCoreWebView2Async(environment);
            if (_disposed || _session is null)
            {
                return;
            }

            CoreWebView2 core = ConsoleWebView.CoreWebView2;

            // A console is a web application the user operates: copy and paste, find and zoom
            // stay. Developer tools stay off, as in every embedded view.
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;

            core.NavigationStarting += OnNavigationStarting;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
            core.NavigationCompleted += OnNavigationCompleted;
            core.HistoryChanged += OnHistoryChanged;
            core.ServerCertificateErrorDetected += OnServerCertificateErrorDetected;
            core.BasicAuthenticationRequested += OnBasicAuthenticationRequested;
            if (CertificateTrust is not null)
            {
                CertificateTrust.CertificateRemoved += OnCertificateRemoved;
            }

            // The zoom the console was last left at, then every change the user makes to it.
            string profileId = ZoomProfileId(_session.ServerId);
            if (ZoomMemory?.Get(profileId) is double zoom)
            {
                ConsoleWebView.ZoomFactor = zoom;
            }

            ConsoleWebView.ZoomFactorChanged += OnZoomFactorChanged;

            core.Navigate(_session.Address.AbsoluteUri);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A tab closed while the browser was starting is not a failed session.
            if (_disposed)
            {
                return;
            }

            FileLogger.Error($"Web console init failed for {_session?.ServerId}: {ex.Message}");
            ReportFailure(ex.Message);
        }
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!WebConsolePolicy.IsNavigable(e.Uri))
        {
            e.Cancel = true;
            FileLogger.Warn("Web console blocked a frame navigation to a non-web address.");
        }
    }

    /// <summary>
    /// Opens a popup in this tab rather than in a bare browser window: that window would have
    /// none of this view's rules (navigation filter, certificate pins, developer tools off) while
    /// writing into the shared profile.
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (WebConsolePolicy.IsNavigable(e.Uri) && ConsoleWebView.CoreWebView2 is { } core)
        {
            core.Navigate(e.Uri);
            return;
        }

        FileLogger.Warn("Web console blocked a popup to a non-web address.");
    }

    /// <summary>A page must not hand the machine a custom protocol (ms-settings:, search-ms:...).</summary>
    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        FileLogger.Warn("Web console blocked an external protocol launch.");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (WebConsolePolicy.IsNavigable(e.Uri))
        {
            LoadingBar.Visibility = Visibility.Visible;
            return;
        }

        e.Cancel = true;
        FileLogger.Warn("Web console blocked a navigation to a non-web address.");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingBar.Visibility = Visibility.Collapsed;
        if (_session is null)
        {
            return;
        }

        if (e.IsSuccess)
        {
            AddressTextBlock.Text = ConsoleWebView.CoreWebView2?.Source ?? AddressTextBlock.Text;
            StatusTextBlock.Text = Text("StatusWebConnected");
            Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
            if (!_connectedRaised)
            {
                _connectedRaised = true;
                SessionConnected?.Invoke(_session.ServerId);
            }

            return;
        }

        string reason = _localizer?.Format("ErrorWebNavigationFailed", Text(WebConsolePolicy.ErrorReasonKey(e.WebErrorStatus.ToString())))
            ?? e.WebErrorStatus.ToString();
        bool canceled = e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled;
        if (!WebConsolePolicy.IsSessionFailure(e.IsSuccess, e.HttpStatusCode, canceled, _connectedRaised))
        {
            // The page itself shows what went wrong; the header only says it.
            if (!canceled)
            {
                StatusTextBlock.Text = e.HttpStatusCode > 0
                    ? _localizer?.Format("StatusWebHttpError", e.HttpStatusCode) ?? e.HttpStatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : reason;
                Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
            }

            if (e.HttpStatusCode > 0 && !_connectedRaised)
            {
                // The console answered: the session is up, even if this page is an error.
                _connectedRaised = true;
                SessionConnected?.Invoke(_session.ServerId);
            }

            return;
        }

        FileLogger.Warn($"Web console {_session.ServerId} navigation failed: {e.WebErrorStatus}.");
        ReportFailure(reason);
    }

    private void OnHistoryChanged(object? sender, object e)
    {
        BackButton.IsEnabled = ConsoleWebView.CoreWebView2?.CanGoBack == true;
    }

    private void OnBasicAuthenticationRequested(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
    {
        if (_session is null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? challengeUri))
        {
            return;
        }

        string key = WebConsolePolicy.ChallengeKey(challengeUri);
        if (!WebConsolePolicy.ShouldAnswerChallenge(
                _session.Username,
                _session.Password,
                _answeredChallenges.Contains(key),
                challengeUri,
                _session.Address))
        {
            return;
        }

        _answeredChallenges.Add(key);
        e.Response.UserName = _session.Username;
        e.Response.Password = _session.Password;
    }

    private async void OnServerCertificateErrorDetected(
        object? sender,
        CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        CoreWebView2Deferral deferral = e.GetDeferral();
        CoreWebView2ServerCertificateErrorAction action = CoreWebView2ServerCertificateErrorAction.Cancel;
        try
        {
            action = await DecideCertificateAsync(e);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileLogger.Warn($"Web console certificate check failed: {ex.Message}");
        }

        // The tab may have closed while the user was answering: the browser behind the event is
        // gone, and an exception thrown from here would end the process (async void).
        try
        {
            if (!_disposed)
            {
                e.Action = action;
            }

            deferral.Complete();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            FileLogger.Info($"Web console certificate answer arrived after the tab closed: {ex.Message}");
        }
    }

    private async Task<CoreWebView2ServerCertificateErrorAction> DecideCertificateAsync(
        CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        if (CertificateTrust is null
            || !Uri.TryCreate(e.RequestUri, UriKind.Absolute, out Uri? requestUri))
        {
            return CoreWebView2ServerCertificateErrorAction.Cancel;
        }

        using X509Certificate2 certificate = e.ServerCertificate.ToX509Certificate2();
        string fingerprint = CertificateFingerprint.ComputeSha256(certificate);
        string host = requestUri.Host;
        int port = requestUri.Port;
        string promptKey = $"{host.ToLowerInvariant()}:{port}|{fingerprint}";

        if (_rejectedCertificates.Contains(promptKey))
        {
            return CoreWebView2ServerCertificateErrorAction.Cancel;
        }

        WebCertificateVerdict verdict = CertificateTrust.Evaluate(host, port, fingerprint);
        if (verdict == WebCertificateVerdict.Trusted)
        {
            return CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        }

        // A page loads many resources from one host; every one of them reports the same
        // certificate, and the user must be asked once, not once per resource.
        if (!_pendingCertificatePrompts.TryGetValue(promptKey, out Task<FtpsCertificateDecision>? pending))
        {
            var prompt = new FtpsCertificatePrompt(
                host,
                port,
                fingerprint,
                certificate.Subject,
                certificate.Issuer,
                new DateTimeOffset(certificate.NotBefore),
                new DateTimeOffset(certificate.NotAfter),
                e.ErrorStatus.ToString());
            CertificatePromptWording wording = verdict == WebCertificateVerdict.Changed
                ? CertificatePromptWording.WebChanged
                : CertificatePromptWording.WebFirstUse;
            pending = AskAsync(prompt, wording);
            _pendingCertificatePrompts[promptKey] = pending;
        }

        FtpsCertificateDecision decision;
        try
        {
            decision = await pending;
        }
        finally
        {
            _pendingCertificatePrompts.Remove(promptKey);
        }

        if (decision == FtpsCertificateDecision.Reject)
        {
            _rejectedCertificates.Add(promptKey);
            return CoreWebView2ServerCertificateErrorAction.Cancel;
        }

        var entry = new FtpsCertificateEntry(
            fingerprint,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            certificate.Subject,
            certificate.Issuer,
            new DateTimeOffset(certificate.NotBefore),
            new DateTimeOffset(certificate.NotAfter),
            FtpsCertificateSource.UserConfirmed)
        {
            ValidationErrors = e.ErrorStatus.ToString(),
        };
        CertificateTrust.Apply(host, port, entry, decision);
        return CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
    }

    /// <summary>
    /// A pin was forgotten in the settings. The browser keeps every certificate it was told to
    /// allow until it restarts, and the profile is shared, so without this the forgotten console
    /// would go on loading silently in every open tab.
    /// </summary>
    private void OnCertificateRemoved(string key)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_disposed || ConsoleWebView.CoreWebView2 is not { } core)
            {
                return;
            }

            try
            {
                await core.ClearServerCertificateErrorActionsAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                FileLogger.Info($"Web console could not clear its accepted certificates: {ex.Message}");
            }
        });
    }

    private async Task<FtpsCertificateDecision> AskAsync(FtpsCertificatePrompt prompt, CertificatePromptWording wording)
    {
        // Yield first, so the dialog's nested message loop does not run inside the browser's
        // event callback.
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (_disposed || Application.Current is not { } app || _localizer is null)
        {
            return FtpsCertificateDecision.Reject;
        }

        return DialogFtpsCertificateVerifier.ShowDialog(app, _localizer, prompt, wording, CancellationToken.None);
    }

    private void ReportFailure(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            StatusTextBlock.Text = message;
            Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
            FallbackMessageText.Text = message;
            FallbackPanel.Visibility = Visibility.Visible;

            // Airspace: the browser's window covers any WPF sibling, so it steps aside.
            ConsoleWebView.Visibility = Visibility.Collapsed;
        });

        if (_session?.ServerId is { } serverId)
        {
            SessionError?.Invoke(serverId, message);
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _session is null)
        {
            return;
        }

        bool wasFailed = FallbackPanel.Visibility == Visibility.Visible;
        FallbackPanel.Visibility = Visibility.Collapsed;
        ConsoleWebView.Visibility = Visibility.Visible;
        _answeredChallenges.Clear();
        _rejectedCertificates.Clear();

        // After a failure the row is in error; the next successful load must bring it back.
        if (wasFailed)
        {
            _connectedRaised = false;
            SessionRetrying?.Invoke(_session.ServerId);
        }

        if (ConsoleWebView.CoreWebView2 is not { } core)
        {
            _ = InitializeWebViewAsync();
            return;
        }

        StatusTextBlock.Text = Text("StatusWebConnecting");
        Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);

        // After a failure the browser still shows the error page of the address that failed;
        // reloading it would retry the error page, so the console's own address is opened again.
        if (string.Equals(core.Source, "about:blank", StringComparison.Ordinal) || !_connectedRaised)
        {
            core.Navigate(_session.Address.AbsoluteUri);
        }
        else
        {
            core.Reload();
        }
    }

    private void OnOpenExternallyClick(object sender, RoutedEventArgs e)
    {
        if (_session is not { CanOpenExternally: true } session)
        {
            return;
        }

        System.Diagnostics.ProcessStartInfo? startInfo = WebExternalLaunch.TryCreateForSession(
            session,
            ConsoleWebView.CoreWebView2?.Source,
            out string? errorKey);
        if (startInfo is null)
        {
            StatusTextBlock.Text = Text(errorKey ?? "ErrorWebExternalLaunchFailed");
            Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(startInfo)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            FileLogger.Warn($"Web console external open failed: {ex.Message}");
            StatusTextBlock.Text = _localizer?.Format("ErrorWebExternalLaunchFailed", ex.Message) ?? ex.Message;
            Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (ConsoleWebView.CoreWebView2 is { CanGoBack: true } core)
        {
            core.GoBack();
        }
    }

    private void OnSplitClick(object sender, RoutedEventArgs e) => RequestSplit?.Invoke(_sessionTab);

    private string Text(string key) => _localizer?[key] ?? key;

    private void OnZoomFactorChanged(object? sender, EventArgs e)
    {
        if (!_disposed && _session is not null)
        {
            ZoomMemory?.Remember(ZoomProfileId(_session.ServerId), ConsoleWebView.ZoomFactor);
        }
    }

    /// <summary>
    /// The profile a console's zoom is kept under: the session id carries it, and a console
    /// opened twice shares one zoom.
    /// </summary>
    internal static string ZoomProfileId(string serverId) =>
        SessionIdCodec.TryGetInventoryId(serverId, out string inventoryId) ? inventoryId : serverId;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_localeChangeSubscribed && _localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
            _localeChangeSubscribed = false;
        }

        if (CertificateTrust is not null)
        {
            CertificateTrust.CertificateRemoved -= OnCertificateRemoved;
        }

        ConsoleWebView.PreviewKeyDown -= OnConsoleWebViewPreviewKeyDown;
        ConsoleWebView.ZoomFactorChanged -= OnZoomFactorChanged;

        if (ConsoleWebView.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.FrameNavigationStarting -= OnFrameNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.HistoryChanged -= OnHistoryChanged;
            core.ServerCertificateErrorDetected -= OnServerCertificateErrorDetected;
            core.BasicAuthenticationRequested -= OnBasicAuthenticationRequested;
        }

        try
        {
            ConsoleWebView.Dispose();
        }
        catch (InvalidOperationException)
        {
            // WebView2 may already be disposed.
        }
    }
}
