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
using System.Runtime.InteropServices;
using System.Text;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>
/// One top-level window of a Citrix process, as read at one instant.
/// </summary>
/// <param name="Hwnd">The window handle.</param>
/// <param name="ProcessId">The process that owns the window.</param>
/// <param name="ProcessName">That process's name, without extension.</param>
/// <param name="Title">The window text. Can name a server: logged only in diagnostic mode.</param>
/// <param name="ClassName">The window class.</param>
/// <param name="Style">GWL_STYLE.</param>
/// <param name="ExStyle">GWL_EXSTYLE.</param>
/// <param name="OwnerHwnd">The owner window (GW_OWNER), zero for an unowned window.</param>
/// <param name="IsVisible">Whether the window has WS_VISIBLE.</param>
/// <param name="Width">Window width in pixels.</param>
/// <param name="Height">Window height in pixels.</param>
internal readonly record struct CitrixWindowRecord(
    IntPtr Hwnd,
    int ProcessId,
    string ProcessName,
    string Title,
    string ClassName,
    uint Style,
    uint ExStyle,
    IntPtr OwnerHwnd,
    bool IsVisible,
    int Width,
    int Height)
{
    /// <summary>The record as one log field list. Carries the title: diagnostic mode only.</summary>
    internal string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"hwnd=0x{Hwnd.ToInt64():X} pid={ProcessId} process={ProcessName} class=\"{ClassName}\" title=\"{Title}\" style=0x{Style:X8} exStyle=0x{ExStyle:X8} owner=0x{OwnerHwnd.ToInt64():X} visible={IsVisible} size={Width}x{Height}");
}

/// <summary>
/// The diagnostic switches, read once per launch from the settings and clamped to their ranges.
/// </summary>
/// <param name="Enabled">Whether the trace runs and the title-matched capture replaces the default one.</param>
/// <param name="TraceDuration">How long the trace runs after a launch.</param>
/// <param name="TraceInterval">The time between two snapshots.</param>
/// <param name="SessionWindowWait">How long the title-matched capture waits for the window.</param>
internal sealed record CitrixWindowDiagnosticsOptions(
    bool Enabled,
    TimeSpan TraceDuration,
    TimeSpan TraceInterval,
    TimeSpan SessionWindowWait)
{
    /// <summary>Diagnostics off: the default capture runs unchanged.</summary>
    internal static CitrixWindowDiagnosticsOptions Disabled { get; } = FromSettings(null);

    /// <summary>Reads the options from <paramref name="settings"/>; null means all defaults.</summary>
    internal static CitrixWindowDiagnosticsOptions FromSettings(AppSettings? settings)
    {
        AppSettings source = settings ?? new AppSettings();
        return new CitrixWindowDiagnosticsOptions(
            source.CitrixWindowDiagnosticsEnabled,
            TimeSpan.FromSeconds(Clamp(nameof(AppSettings.CitrixWindowDiagnosticsDurationSeconds), source.CitrixWindowDiagnosticsDurationSeconds)),
            TimeSpan.FromMilliseconds(Clamp(nameof(AppSettings.CitrixWindowDiagnosticsIntervalMs), source.CitrixWindowDiagnosticsIntervalMs)),
            TimeSpan.FromSeconds(Clamp(nameof(AppSettings.CitrixSessionWindowWaitSeconds), source.CitrixSessionWindowWaitSeconds)));
    }

    private static int Clamp(string propertyName, int value) => SettingRanges.Of(propertyName).Clamp(value);
}

/// <summary>
/// Decides whether a window title names a published application.
/// </summary>
/// <remarks>
/// Measured with Citrix Workspace 2511: an ICA window title reads
/// <c>&lt;application&gt;- \\&lt;localized suffix&gt;</c>, for example <c>MobaXterm- \\Distant</c>.
/// Several applications can share one wfica32 process, so the title, not the process id, tells
/// them apart. The application name must be followed by the separator or end the title: a bare
/// prefix test would let <c>MobaXterm</c> claim the window of <c>MobaXterm Pro</c>.
/// </remarks>
internal static class CitrixWindowTitleMatcher
{
    /// <summary>The character that ends the application name in an ICA window title.</summary>
    internal const char ApplicationNameSeparator = '-';

    /// <summary>True when <paramref name="title"/> is the window of <paramref name="applicationName"/>.</summary>
    internal static bool IsApplicationWindowTitle(string? title, string? applicationName)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(applicationName))
        {
            return false;
        }

        string trimmedTitle = title.Trim();
        string trimmedName = applicationName.Trim();
        if (!trimmedTitle.StartsWith(trimmedName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> rest = trimmedTitle.AsSpan(trimmedName.Length).TrimStart();
        return rest.IsEmpty || rest[0] == ApplicationNameSeparator;
    }
}

/// <summary>
/// Picks the ICA window of one published application out of a window survey.
/// </summary>
internal static class CitrixApplicationWindowSelector
{
    /// <summary>The processes that host published-application windows, as opposed to the Workspace shell.</summary>
    internal static readonly IReadOnlyList<string> SessionHostProcessNames = ["wfica32", "CDViewer"];

    /// <summary>
    /// The visible, new, session-host windows whose title names <paramref name="applicationName"/>,
    /// in survey order. Empty when none is there yet.
    /// </summary>
    internal static IReadOnlyList<CitrixWindowRecord> Select(
        IEnumerable<CitrixWindowRecord> survey,
        IReadOnlySet<IntPtr> preLaunchWindows,
        string? applicationName)
    {
        ArgumentNullException.ThrowIfNull(survey);
        ArgumentNullException.ThrowIfNull(preLaunchWindows);

        return survey
            .Where(window => window.IsVisible
                && !preLaunchWindows.Contains(window.Hwnd)
                && SessionHostProcessNames.Contains(window.ProcessName, StringComparer.OrdinalIgnoreCase)
                && CitrixWindowTitleMatcher.IsApplicationWindowTitle(window.Title, applicationName))
            .ToList();
    }
}

/// <summary>
/// Tells what changed between two window surveys, one log line per window.
/// </summary>
internal static class CitrixWindowTraceDiff
{
    internal const string AppearedTag = "appeared";
    internal const string ChangedTag = "changed";
    internal const string GoneTag = "gone";

    /// <summary>
    /// The lines for the windows that appeared, changed or disappeared since
    /// <paramref name="previous"/>, and the state to compare the next survey with.
    /// </summary>
    internal static IReadOnlyList<string> Compare(
        IReadOnlyDictionary<IntPtr, CitrixWindowRecord> previous,
        IReadOnlyList<CitrixWindowRecord> current,
        out Dictionary<IntPtr, CitrixWindowRecord> next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        List<string> lines = [];
        next = [];
        foreach (CitrixWindowRecord window in current)
        {
            next[window.Hwnd] = window;
            if (!previous.TryGetValue(window.Hwnd, out CitrixWindowRecord before))
            {
                lines.Add($"{AppearedTag} {window.Describe()}");
            }
            else if (before != window)
            {
                lines.Add($"{ChangedTag} {window.Describe()}");
            }
        }

        foreach (CitrixWindowRecord window in previous.Values)
        {
            if (!next.ContainsKey(window.Hwnd))
            {
                lines.Add($"{GoneTag} {window.Describe()}");
            }
        }

        return lines;
    }
}

/// <summary>
/// Reads every top-level window of the Workspace and ICA processes of this Windows session.
/// </summary>
internal static class CitrixWindowSurvey
{
    /// <summary>The processes the diagnostic trace follows.</summary>
    internal static readonly IReadOnlyList<string> TracedProcessNames = ["SelfService", "wfica32", "CDViewer"];

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint GwOwner = 4;
    private const int ClassNameCapacity = 256;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>All top-level windows, visible or not, of the processes named in <paramref name="processNames"/>.</summary>
    internal static IReadOnlyList<CitrixWindowRecord> Capture(IEnumerable<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);

        Dictionary<int, string> namesByPid = [];
        foreach (string name in processNames)
        {
            foreach (int pid in CitrixProcessScan.LivePidsInCurrentSession([name]))
            {
                namesByPid[pid] = name;
            }
        }

        List<CitrixWindowRecord> windows = [];
        if (namesByPid.Count == 0)
        {
            return windows;
        }

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (namesByPid.TryGetValue((int)pid, out string? processName))
            {
                windows.Add(Read(hwnd, (int)pid, processName));
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static CitrixWindowRecord Read(IntPtr hwnd, int pid, string processName)
    {
        StringBuilder className = new(ClassNameCapacity);
        GetClassName(hwnd, className, ClassNameCapacity);

        int titleLength = GetWindowTextLength(hwnd);
        StringBuilder title = new(titleLength + 1);
        if (titleLength > 0)
        {
            GetWindowText(hwnd, title, title.Capacity);
        }

        GetWindowRect(hwnd, out Rect rect);
        return new CitrixWindowRecord(
            hwnd,
            pid,
            processName,
            title.ToString(),
            className.ToString(),
            GetWindowLong(hwnd, GwlStyle),
            GetWindowLong(hwnd, GwlExStyle),
            GetWindow(hwnd, GwOwner),
            IsWindowVisible(hwnd),
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
    }
}

/// <summary>
/// The diagnostic trace: logs every top-level window the Workspace and ICA processes create,
/// change or destroy after a launch, until the trace duration ends or the tab closes.
/// </summary>
internal static class CitrixWindowDiagnostics
{
    /// <summary>The prefix of every trace line, to grep the log by.</summary>
    internal const string LogPrefix = "Citrix diag:";

    /// <summary>Runs the trace. Never throws: a failure ends the trace and is logged.</summary>
    internal static async Task RunAsync(CitrixWindowDiagnosticsOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        Core.Logging.FileLogger.Info(
            $"{LogPrefix} trace started duration={options.TraceDuration.TotalSeconds}s interval={options.TraceInterval.TotalMilliseconds}ms processes={string.Join(',', CitrixWindowSurvey.TracedProcessNames)}");

        Dictionary<IntPtr, CitrixWindowRecord> previous = [];
        DateTime deadline = DateTime.UtcNow + options.TraceDuration;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                IReadOnlyList<CitrixWindowRecord> survey = CitrixWindowSurvey.Capture(CitrixWindowSurvey.TracedProcessNames);
                foreach (string line in CitrixWindowTraceDiff.Compare(previous, survey, out previous))
                {
                    Core.Logging.FileLogger.Info($"{LogPrefix} {line}");
                }

                await Task.Delay(options.TraceInterval, cancellationToken).ConfigureAwait(false);
            }

            Core.Logging.FileLogger.Info($"{LogPrefix} trace ended");
        }
        catch (OperationCanceledException)
        {
            Core.Logging.FileLogger.Info($"{LogPrefix} trace stopped (tab closed)");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"{LogPrefix} trace failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
