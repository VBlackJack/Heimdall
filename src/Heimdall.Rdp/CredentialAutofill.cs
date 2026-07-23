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

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Heimdall.Core.Logging;

namespace Heimdall.Rdp;

/// <summary>
/// Detects RDP credential dialog (CredUI) windows and auto-fills the password.
/// Uses EnumWindows to find secondary CredUI dialogs that are invisible to
/// Process.MainWindowTitle (which returns only one window per process).
/// </summary>
public static partial class CredentialAutofill
{
    #region Win32 P/Invoke

    private delegate bool EnumWindowsDelegate(IntPtr hWnd, IntPtr lParam);
    private delegate bool EnumChildWindowsDelegate(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsDelegate lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(IntPtr hWndParent, EnumChildWindowsDelegate lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumThreadWindows(uint dwThreadId, EnumWindowsDelegate lpfn, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(IntPtr hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong32(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageString(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    private const int GWL_STYLE = -16;
    private const long ES_PASSWORD = 0x0020L;
    private const uint WM_SETTEXT = 0x000C;
    private const uint BM_CLICK = 0x00F5;

    #endregion

    internal static bool HasPasswordStyle(long style) => (style & ES_PASSWORD) != 0;

    private static long GetWindowStyle(IntPtr hWnd)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, GWL_STYLE).ToInt64()
            : GetWindowLong32(hWnd, GWL_STYLE);

    #region Configuration

    /// <summary>Default interval between window scans.</summary>
    private static readonly TimeSpan DefaultScanInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Default delay after bringing the credential dialog to the foreground.</summary>
    private static readonly TimeSpan DefaultFocusDelay = TimeSpan.FromMilliseconds(300);

    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Known credential broker process names spawned by Windows for CredUI.</summary>
    private static readonly string[] CredentialBrokerProcessNames =
        ["CredentialUIBroker", "LogonUI", "consent"];

    /// <summary>
    /// Known top-level classes used by modern CredUI hosts.
    /// Note: "#32770" (generic Win32 dialog) is intentionally excluded to avoid
    /// matching unrelated dialogs (e.g., Open File, Save As, confirmation prompts).
    /// </summary>
    private static readonly string[] CredentialDialogClassNames =
        ["Credential Dialog Xaml Host", "Windows Security"];

    /// <summary>
    /// Regex pattern matching credential dialog titles across supported locales.
    /// Covers common Windows Security and CredUI titles, plus mstsc prompts.
    /// </summary>
    internal static readonly Regex TitlePattern = new(
        @"Windows Security"
        + @"|S[e\u00e9]curit[e\u00e9](\s+de)?\s+Windows"
        + @"|Windows-Sicherheit"
        + @"|Seguridad de Windows"
        + @"|Sicurezza di Windows"
        + @"|Seguran[c\u00e7]a do Windows"
        + @"|Windows-beveiliging"
        + @"|Zabezpieczenia systemu Windows"
        + @"|Credential|Credenziale|Credencial|Anmeldeinformation"
        + @"|mstsc",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Regex pattern matching password fields in UI Automation trees.</summary>
    private static readonly Regex PasswordFieldPattern = new(
        @"Password|Mot de passe",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Regex pattern matching confirmation buttons in credential dialogs.</summary>
    private static readonly Regex OkButtonPattern = new(
        @"^(OK|&OK|Sign in|Connect|Se connecter)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    #endregion

    /// <summary>
    /// Polls for a CredUI credential dialog associated with the given mstsc process
    /// and fills in the password when found. The password is sent via WM_SETTEXT
    /// to the password edit control, then a Tab + Enter sequence submits the dialog.
    /// </summary>
    /// <param name="mstscProcessId">Process ID of the mstsc.exe instance.</param>
    /// <param name="targetHost">Hostname hint used to disambiguate multiple credential dialogs.</param>
    /// <param name="password">Cleartext password to inject.</param>
    /// <param name="timeout">Maximum time to wait for the credential dialog to appear.</param>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>True if the password was successfully injected, false if timed out or cancelled.</returns>
    public static async Task<bool> WaitAndFillAsync(
        int mstscProcessId,
        string targetHost,
        string password,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var hostHintPattern = string.IsNullOrWhiteSpace(targetHost)
            ? null
            : new Regex(Regex.Escape(targetHost), RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var passwordBuffer = password.ToCharArray();

        var deadline = DateTime.UtcNow + timeout;
        var scan = 0;

        FileLogger.Info(
            $"CredentialAutofill started: pid={mstscProcessId} hostHint={targetHost} timeout={timeout.TotalSeconds:0}s interval={DefaultScanInterval.TotalMilliseconds:0}ms");

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scan++;

                var target = FindCredentialDialog(mstscProcessId, targetHost, hostHintPattern, scan);
                if (target is not null)
                {
                    FileLogger.Info(
                        $"CredentialAutofill dialog candidate found on scan {scan}: handle=0x{target.Value.Handle.ToInt64():X} class={target.Value.ClassName} title='{target.Value.Title}' pid={target.Value.ProcessId} process={target.Value.ProcessName}");

                    // Allow the dialog to fully render.
                    await Task.Delay(DefaultFocusDelay, cancellationToken).ConfigureAwait(false);

                    if (InjectPassword(target.Value, passwordBuffer))
                    {
                        FileLogger.Info($"CredentialAutofill succeeded on scan {scan}");
                        return true;
                    }

                    FileLogger.Warn(
                        $"CredentialAutofill injection attempt failed on scan {scan}; continuing to watch for CredUI.");
                }

                await Task.Delay(DefaultScanInterval, cancellationToken).ConfigureAwait(false);
            }

            FileLogger.Info($"CredentialAutofill timed out after {scan} scans; brokerMatch=no-match.");
            return false;
        }
        finally
        {
            Array.Clear(passwordBuffer, 0, passwordBuffer.Length);
        }
    }

    /// <summary>
    /// Scans all visible top-level windows via EnumWindows to find a CredUI dialog
    /// matching the title pattern and owned by the target process or a known credential broker.
    /// </summary>
    private static WindowInfo? FindCredentialDialog(
        int mstscProcessId,
        string targetHost,
        Regex? hostHintPattern,
        int scan)
    {
        var windows = GetVisibleWindows();
        var candidates = windows.Where(IsCredentialDialogCandidate).ToList();

        return SelectCredentialDialogTarget(
            mstscProcessId,
            hostHintPattern,
            candidates,
            scan,
            targetHost,
            windows.Count);
    }

    internal static WindowInfo? SelectCredentialDialogTarget(
        int mstscProcessId,
        Regex? hostHintPattern,
        IReadOnlyList<WindowInfo> candidates,
        int scan,
        string? targetHost = null,
        int? visibleWindowCount = null,
        Func<WindowInfo, bool>? isCredentialBroker = null)
    {
        var evaluation = EvaluateCredentialDialogTarget(
            mstscProcessId,
            hostHintPattern,
            candidates,
            scan,
            targetHost,
            visibleWindowCount,
            isCredentialBroker ?? (window => IsCredentialBroker(window.ProcessId, window.ProcessName)));

        // OS window titles are already public desktop metadata; log them as-is for diagnostics.
        FileLogger.Debug(
            "CredentialAutofillBrokerMatch "
            + JsonSerializer.Serialize(evaluation.Diagnostics, DiagnosticJsonOptions));

        if (evaluation.BrokerEnumerationException is not null)
        {
            FileLogger.Warn(
                "CredentialAutofillBrokerEnumerationFailed "
                + JsonSerializer.Serialize(
                    new CredentialBrokerEnumerationFailureLog(
                        scan,
                        targetHost ?? string.Empty,
                        evaluation.BrokerEnumerationException.GetType().Name,
                        evaluation.BrokerEnumerationException.Message),
                    DiagnosticJsonOptions));
        }

        FileLogger.Info(
            $"CredentialAutofill broker match outcome: scan={scan} targetHost={targetHost ?? string.Empty} outcome={evaluation.Diagnostics.Outcome} matchedHandle={evaluation.Diagnostics.MatchedHandle}");

        return evaluation.Selected;
    }

    private static CredentialBrokerMatchEvaluation EvaluateCredentialDialogTarget(
        int mstscProcessId,
        Regex? hostHintPattern,
        IReadOnlyList<WindowInfo> candidates,
        int scan,
        string? targetHost,
        int? visibleWindowCount,
        Func<WindowInfo, bool> isCredentialBroker)
    {
        var selected = (WindowInfo?)null;
        var outcome = "no-match";
        Exception? brokerEnumerationException = null;
        var decisions = new Dictionary<IntPtr, (string Decision, string Reason)>();

        // Prefer windows owned by the target mstsc process.
        var ownedByTarget = candidates.Where(w => w.ProcessId == mstscProcessId).ToList();
        if (ownedByTarget.Count > 0)
        {
            selected = SelectBestMatch(ownedByTarget, hostHintPattern);
            outcome = "matched-target-process";
            foreach (var candidate in candidates)
            {
                decisions[candidate.Handle] = candidate.Handle == selected.Value.Handle
                    ? ("accepted", "owned-by-target-process")
                    : candidate.ProcessId == mstscProcessId
                        ? ("rejected", "owned-by-target-process-not-selected")
                        : ("rejected", "target-process-match-took-priority");
            }
        }
        else
        {
            var brokerClassifications = new Dictionary<IntPtr, bool>();
            try
            {
                foreach (var candidate in candidates)
                {
                    brokerClassifications[candidate.Handle] = isCredentialBroker(candidate);
                }
            }
            catch (Exception ex)
            {
                brokerEnumerationException = ex;
                outcome = "broker-enumeration-failed";
                foreach (var candidate in candidates)
                {
                    decisions[candidate.Handle] = ("rejected", "broker-enumeration-failed");
                }
            }

            if (brokerEnumerationException is null)
            {
                var brokerMatches = candidates
                    .Where(w => brokerClassifications.TryGetValue(w.Handle, out var isBroker) && isBroker)
                    .ToList();

                if (brokerMatches.Count > 0)
                {
                    if (hostHintPattern is not null)
                    {
                        var hostMatched = brokerMatches.Where(w => hostHintPattern.IsMatch(w.Title)).ToList();
                        if (hostMatched.Count > 0)
                        {
                            selected = SelectBestMatch(hostMatched, hostHintPattern);
                            outcome = "matched-broker-host-title";
                        }
                    }

                    foreach (var candidate in candidates)
                    {
                        var isBroker = brokerClassifications.TryGetValue(candidate.Handle, out var value) && value;
                        decisions[candidate.Handle] = ResolveBrokerCandidateDecision(
                            candidate,
                            isBroker,
                            hostHintPattern,
                            selected);
                    }
                }
                else
                {
                    foreach (var candidate in candidates)
                    {
                        decisions[candidate.Handle] = ("rejected", "not-target-process-or-known-broker");
                    }
                }
            }
        }

        var candidateLogs = candidates
            .Select(candidate =>
            {
                var decision = decisions.TryGetValue(candidate.Handle, out var value)
                    ? value
                    : ("rejected", "not-evaluated");
                return new CredentialBrokerCandidateLog(
                    FormatHandle(candidate.Handle),
                    candidate.Title,
                    candidate.ClassName,
                    candidate.ProcessId,
                    candidate.ProcessName,
                    decision.Item1,
                    decision.Item2);
            })
            .ToArray();

        return new CredentialBrokerMatchEvaluation(
            selected,
            brokerEnumerationException,
            new CredentialBrokerMatchLog(
                scan,
                targetHost ?? string.Empty,
                mstscProcessId,
                visibleWindowCount.GetValueOrDefault(candidates.Count),
                candidates.Count,
                candidateLogs,
                outcome,
                selected is { } selectedWindow ? FormatHandle(selectedWindow.Handle) : "no-match"));
    }

    private static (string Decision, string Reason) ResolveBrokerCandidateDecision(
        WindowInfo candidate,
        bool isBroker,
        Regex? hostHintPattern,
        WindowInfo? selected)
    {
        if (!isBroker)
        {
            return ("rejected", "not-known-broker");
        }

        if (hostHintPattern is null)
        {
            return ("rejected", "missing-host-hint");
        }

        if (!hostHintPattern.IsMatch(candidate.Title))
        {
            return ("rejected", "host-title-mismatch");
        }

        return selected is { } selectedWindow && selectedWindow.Handle == candidate.Handle
            ? ("accepted", "host-title-match")
            : ("rejected", "host-title-match-not-selected");
    }

    /// <summary>
    /// Selects the best matching window handle, preferring one whose title contains
    /// the host hint if available.
    /// </summary>
    private static WindowInfo SelectBestMatch(List<WindowInfo> candidates, Regex? hostHintPattern)
    {
        if (hostHintPattern is not null)
        {
            var hostMatch = candidates.FirstOrDefault(w => hostHintPattern.IsMatch(w.Title));
            if (hostMatch.Handle != IntPtr.Zero)
            {
                return hostMatch;
            }
        }

        var foregroundHandle = GetForegroundWindow();
        var foregroundMatch = candidates.FirstOrDefault(w => w.Handle == foregroundHandle);
        if (foregroundMatch.Handle != IntPtr.Zero)
        {
            return foregroundMatch;
        }

        var xamlHost = candidates.FirstOrDefault(w =>
            w.ClassName.Contains("Credential Dialog Xaml Host", StringComparison.OrdinalIgnoreCase));
        if (xamlHost.Handle != IntPtr.Zero)
        {
            return xamlHost;
        }

        return candidates[0];
    }

    /// <summary>
    /// Checks whether the given process ID belongs to a known credential broker.
    /// </summary>
    private static bool IsCredentialBroker(int processId, string? processName = null)
    {
        try
        {
            var name = processName;
            if (string.IsNullOrWhiteSpace(name))
            {
                using var proc = System.Diagnostics.Process.GetProcessById(processId);
                name = proc.ProcessName;
            }

            return CredentialBrokerProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[CredentialAutofill] IsCredentialBroker: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks whether a top-level window matches known CredUI classes or titles.
    /// </summary>
    private static bool IsCredentialDialogCandidate(WindowInfo window)
    {
        if (TitlePattern.IsMatch(window.Title))
        {
            return true;
        }

        return CredentialDialogClassNames.Any(className =>
            window.ClassName.Contains(className, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Injects the password into the credential dialog by:
    /// 1. Bringing the dialog to the foreground
    /// 2. Finding the password Edit control
    /// 3. Sending WM_SETTEXT with the password
    /// </summary>
    private static bool InjectPassword(WindowInfo dialog, char[] passwordBuffer)
    {
        FileLogger.Info(
            $"CredentialAutofill injecting: handle=0x{dialog.Handle.ToInt64():X} class={dialog.ClassName} title='{dialog.Title}'");

        SetForegroundWindow(dialog.Handle);
        var foregroundHandle = GetForegroundWindow();
        FileLogger.Info(
            $"CredentialAutofill foreground after SetForegroundWindow: requested=0x{dialog.Handle.ToInt64():X} actual=0x{foregroundHandle.ToInt64():X}");

        if (TryInjectPasswordViaAutomation(dialog, passwordBuffer))
        {
            return true;
        }

        FileLogger.Warn(
            $"CredentialAutofill UI Automation path failed for handle=0x{dialog.Handle.ToInt64():X}; trying Win32 fallback.");
        return TryInjectPasswordViaWin32(dialog, passwordBuffer);
    }

    /// <summary>
    /// Uses UI Automation for modern XAML-hosted CredUI dialogs and as a first choice
    /// for classic dialogs when the automation tree is available.
    /// </summary>
    private static bool TryInjectPasswordViaAutomation(WindowInfo dialog, char[] passwordBuffer)
    {
        try
        {
            var root = AutomationElement.FromHandle(dialog.Handle);
            if (root is null)
            {
                FileLogger.Warn("CredentialAutofill UIA root was null.");
                return false;
            }

            var passwordText = new string(passwordBuffer);
            IReadOnlyList<PasswordFieldCandidate<AutomationElement>> passwordFields =
                FindPasswordElements(root);
            bool injected = TryAutofillConfirmedPasswordField(
                passwordFields,
                passwordText,
                TrySetAutomationValue,
                () =>
                {
                    var okButton = FindOkButtonElement(root);
                    if (okButton is null)
                    {
                        FileLogger.Warn("CredentialAutofill UIA could not locate the confirmation button.");
                        return false;
                    }

                    if (!TryInvokeAutomationButton(okButton))
                    {
                        FileLogger.Warn("CredentialAutofill UIA could not invoke the confirmation button.");
                        return false;
                    }

                    return true;
                });

            if (!injected)
            {
                return false;
            }

            FileLogger.Info("CredentialAutofill UIA injection succeeded.");
            return true;
        }
        catch (ElementNotAvailableException ex)
        {
            FileLogger.Warn($"CredentialAutofill UIA element disappeared: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"CredentialAutofill UIA failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Falls back to recursive Win32 child enumeration for classic dialogs where direct
    /// descendant Edit/Button handles are exposed.
    /// </summary>
    private static bool TryInjectPasswordViaWin32(WindowInfo dialog, char[] passwordBuffer)
    {
        List<WindowInfo> editControls = GetDescendantWindows(dialog.Handle)
            .Where(w => string.Equals(w.ClassName, "Edit", StringComparison.OrdinalIgnoreCase))
            .ToList();

        FileLogger.Info($"CredentialAutofill Win32 found {editControls.Count} descendant Edit controls.");

        if (editControls.Count == 0)
        {
            return false;
        }

        IReadOnlyList<PasswordFieldCandidate<WindowInfo>> passwordFields = editControls
            .Select(edit => new PasswordFieldCandidate<WindowInfo>(
                edit,
                string.Empty,
                IsEnabled: true,
                IsPassword: HasPasswordStyle(GetWindowStyle(edit.Handle))))
            .ToList();

        return TryAutofillConfirmedPasswordField(
            passwordFields,
            new string(passwordBuffer),
            (passwordEdit, passwordText) =>
            {
                SendMessageString(passwordEdit.Handle, WM_SETTEXT, IntPtr.Zero, passwordText);
                FileLogger.Info(
                    $"CredentialAutofill Win32 password text sent to handle=0x{passwordEdit.Handle.ToInt64():X}");
                return true;
            },
            () =>
            {
                var buttons = GetDescendantWindows(dialog.Handle)
                    .Where(w => string.Equals(w.ClassName, "Button", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                FileLogger.Info($"CredentialAutofill Win32 found {buttons.Count} descendant Button controls.");

                if (buttons.Count == 0)
                {
                    return true;
                }

                var targetButton = buttons.FirstOrDefault(w => OkButtonPattern.IsMatch(w.Title));
                if (targetButton.Handle == IntPtr.Zero)
                {
                    targetButton = buttons.Count >= 2 ? buttons[1] : buttons[0];
                }

                SendMessage(targetButton.Handle, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                FileLogger.Info(
                    $"CredentialAutofill Win32 clicked button handle=0x{targetButton.Handle.ToInt64():X} title='{targetButton.Title}'");
                return true;
            });
    }

    private static IReadOnlyList<PasswordFieldCandidate<AutomationElement>> FindPasswordElements(
        AutomationElement root)
    {
        var edits = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

        FileLogger.Info($"CredentialAutofill UIA discovered {edits.Count} Edit elements.");

        var passwordFields = new List<PasswordFieldCandidate<AutomationElement>>(edits.Count);

        for (var index = 0; index < edits.Count; index++)
        {
            var element = edits[index];
            var name = SafeAutomationName(element);
            var isEnabled = SafeAutomationBool(element, AutomationElement.IsEnabledProperty);
            var isPassword = SafeAutomationBool(element, AutomationElement.IsPasswordProperty);

            FileLogger.Debug(
                $"CredentialAutofill UIA edit[{index}]: enabled={isEnabled} isPassword={isPassword}");

            passwordFields.Add(new PasswordFieldCandidate<AutomationElement>(
                element,
                name,
                isEnabled,
                isPassword));
        }

        return passwordFields;
    }

    internal static bool TryAutofillConfirmedPasswordField<T>(
        IReadOnlyList<PasswordFieldCandidate<T>> fields,
        string password,
        Func<T, string, bool> writePassword,
        Func<bool> submit)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(writePassword);
        ArgumentNullException.ThrowIfNull(submit);

        var passwordFlagIndex = -1;
        var passwordNameIndex = -1;

        for (var index = 0; index < fields.Count; index++)
        {
            PasswordFieldCandidate<T> field = fields[index];
            if (!field.IsEnabled)
            {
                continue;
            }

            if (passwordFlagIndex < 0 && field.IsPassword)
            {
                passwordFlagIndex = index;
            }

            if (passwordNameIndex < 0 && PasswordFieldPattern.IsMatch(field.Name))
            {
                passwordNameIndex = index;
            }
        }

        int selectedIndex = passwordFlagIndex >= 0 ? passwordFlagIndex : passwordNameIndex;
        if (selectedIndex < 0)
        {
            FileLogger.Info(
                "CredentialAutofill abandoned because no confirmed password field was found.");
            return false;
        }

        if (!writePassword(fields[selectedIndex].Element, password))
        {
            FileLogger.Warn("CredentialAutofill could not set the confirmed password field value.");
            return false;
        }

        return submit();
    }

    private static AutomationElement? FindOkButtonElement(AutomationElement root)
    {
        var buttons = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        FileLogger.Info($"CredentialAutofill UIA discovered {buttons.Count} Button elements.");

        AutomationElement? firstEnabled = null;

        for (var index = 0; index < buttons.Count; index++)
        {
            var button = buttons[index];
            var isEnabled = SafeAutomationBool(button, AutomationElement.IsEnabledProperty);

            FileLogger.Debug(
                $"CredentialAutofill UIA button[{index}]: enabled={isEnabled}");

            if (!isEnabled)
            {
                continue;
            }

            if (firstEnabled is null)
            {
                firstEnabled = button;
            }

            if (OkButtonPattern.IsMatch(SafeAutomationName(button)))
            {
                return button;
            }
        }

        return firstEnabled;
    }

    private static bool TrySetAutomationValue(AutomationElement element, string value)
    {
        try
        {
            element.SetFocus();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"CredentialAutofill UIA SetFocus failed: {ex.Message}");
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject)
            && valuePatternObject is ValuePattern valuePattern)
        {
            valuePattern.SetValue(value);
            FileLogger.Info("CredentialAutofill UIA password set via ValuePattern.");
            return true;
        }

        var nativeHandle = element.Current.NativeWindowHandle;
        if (nativeHandle != 0)
        {
            SendMessageString(new IntPtr(nativeHandle), WM_SETTEXT, IntPtr.Zero, value);
            FileLogger.Info($"CredentialAutofill UIA password set via native handle 0x{nativeHandle:X}.");
            return true;
        }

        return false;
    }

    private static bool TryInvokeAutomationButton(AutomationElement button)
    {
        try
        {
            button.SetFocus();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"CredentialAutofill UIA button SetFocus failed: {ex.Message}");
        }

        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObject)
            && invokeObject is InvokePattern invokePattern)
        {
            invokePattern.Invoke();
            FileLogger.Info("CredentialAutofill UIA confirmation invoked via InvokePattern.");
            return true;
        }

        var nativeHandle = button.Current.NativeWindowHandle;
        if (nativeHandle != 0)
        {
            SendMessage(new IntPtr(nativeHandle), BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            FileLogger.Info($"CredentialAutofill UIA confirmation invoked via native handle 0x{nativeHandle:X}.");
            return true;
        }

        return false;
    }

    #region Window Enumeration

    internal readonly record struct WindowInfo(
        IntPtr Handle,
        string Title,
        string ClassName,
        int ProcessId,
        string ProcessName);

    internal readonly record struct PasswordFieldCandidate<T>(
        T Element,
        string Name,
        bool IsEnabled,
        bool IsPassword);

    private sealed record CredentialBrokerMatchEvaluation(
        WindowInfo? Selected,
        Exception? BrokerEnumerationException,
        CredentialBrokerMatchLog Diagnostics);

    private sealed record CredentialBrokerMatchLog(
        int Scan,
        string TargetHost,
        int MstscProcessId,
        int VisibleWindowCount,
        int CandidateCount,
        IReadOnlyList<CredentialBrokerCandidateLog> Candidates,
        string Outcome,
        string MatchedHandle);

    private sealed record CredentialBrokerCandidateLog(
        string Handle,
        string Title,
        string ClassName,
        int ProcessId,
        string ProcessName,
        string Decision,
        string Reason);

    private sealed record CredentialBrokerEnumerationFailureLog(
        int Scan,
        string TargetHost,
        string ExceptionType,
        string Message);

    /// <summary>
    /// Enumerates ALL visible top-level windows with non-empty titles via EnumWindows.
    /// Unlike Process.MainWindowTitle which returns only one window per process,
    /// this finds secondary windows such as credential dialogs spawned by ActiveX controls.
    /// </summary>
    private static List<WindowInfo> GetVisibleWindows()
    {
        var result = new List<WindowInfo>();
        var currentPid = Environment.ProcessId;

        // 1. EnumWindows for top-level windows
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out var pid);
            var title = GetWindowText(hWnd);
            var className = GetClassName(hWnd);
            var processName = GetProcessName(pid);

            if (string.IsNullOrWhiteSpace(title)
                && !CredentialDialogClassNames.Any(classNameHint =>
                    className.Contains(classNameHint, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            result.Add(new WindowInfo(hWnd, title, className, pid, processName));

            return true;
        }, IntPtr.Zero);

        // 2. Also scan all threads of the current process for child/owned dialogs
        // The CredUI dialog from embedded ActiveX may not be a top-level window
        var existingHandles = new HashSet<IntPtr>(result.Select(w => w.Handle));
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
            foreach (System.Diagnostics.ProcessThread thread in process.Threads)
            {
                EnumThreadWindows((uint)thread.Id, (hWnd, _) =>
                {
                    if (existingHandles.Contains(hWnd) || !IsWindowVisible(hWnd))
                        return true;

                    GetWindowThreadProcessId(hWnd, out var pid);
                    var title = GetWindowText(hWnd);
                    var className = GetClassName(hWnd);

                    if (!string.IsNullOrWhiteSpace(title) || CredentialDialogClassNames.Any(cn =>
                        className.Contains(cn, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add(new WindowInfo(hWnd, title, className, pid, GetProcessName(pid)));
                        existingHandles.Add(hWnd);
                    }
                    return true;
                }, IntPtr.Zero);
            }
        }
        catch (Exception ex) { FileLogger.Warn($"[CredentialAutofill] thread enumeration: {ex.Message}"); }

        return result;
    }

    private static List<WindowInfo> GetDescendantWindows(IntPtr parentHandle)
    {
        var result = new List<WindowInfo>();

        EnumChildWindows(parentHandle, (hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            result.Add(new WindowInfo(
                hWnd,
                GetWindowText(hWnd),
                GetClassName(hWnd),
                pid,
                GetProcessName(pid)));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static string GetWindowText(IntPtr hWnd)
    {
        var len = GetWindowTextLengthW(hWnd);
        if (len <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[len + 1];
        GetWindowTextW(hWnd, buffer, buffer.Length);
        return new string(buffer, 0, len);
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var buffer = new char[256];
        var len = GetClassNameW(hWnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer, 0, len) : string.Empty;
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[CredentialAutofill] GetProcessName: {ex.Message}");
            return string.Empty;
        }
    }

    private static string SafeAutomationName(AutomationElement element)
    {
        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[CredentialAutofill] SafeAutomationName: {ex.Message}");
            return string.Empty;
        }
    }

    private static bool SafeAutomationBool(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property, true);
            return value is bool b && b;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[CredentialAutofill] SafeAutomationBool: {ex.Message}");
            return false;
        }
    }

    private static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";

    #endregion
}
