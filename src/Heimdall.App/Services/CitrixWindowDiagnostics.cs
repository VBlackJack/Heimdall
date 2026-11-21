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
    /// <summary>The window area in square pixels; zero for a window with no size.</summary>
    internal long Area => (long)Math.Max(Width, 0) * Math.Max(Height, 0);

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
/// Decides whether a window title is the anchor title of a published application.
/// </summary>
/// <remarks>
/// Measured with Citrix Workspace 2511 (Polaris, 2026-10-28): every seamless window title reads
/// <c>&lt;remote window title&gt;- \\&lt;localized suffix&gt;</c>. The published application owns an
/// anchor window whose remote title is exactly the application name, <c>MobaXterm- \\Distant</c>,
/// while its visible window carries the application's own title, <c>MobaXterm Professional-
/// \\Distant</c>. Matching the name followed by the separator, and nothing else, keeps the
/// launch window <c>MobaXterm - Citrix Workspace</c> and <c>MobaXterm Pro</c> out.
/// </remarks>
internal static class CitrixWindowTitleMatcher
{
    /// <summary>What Citrix puts between the remote window title and its localized suffix.</summary>
    internal const string SeamlessTitleSeparator = "- \\\\";

    /// <summary>True when <paramref name="title"/> is the anchor title of <paramref name="applicationName"/>.</summary>
    internal static bool IsApplicationWindowTitle(string? title, string? applicationName)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(applicationName))
        {
            return false;
        }

        return title.Trim().StartsWith(applicationName.Trim() + SeamlessTitleSeparator, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Picks the window of one published application out of a window survey.
/// </summary>
/// <remarks>
/// The application is found by its anchor: an unowned seamless window of a session-host process
/// titled after the application. The anchor has no size; the window to embed is the largest
/// visible window it owns. Session-wide helpers (language bar, tooltips) are owned by another
/// window and never compete.
/// </remarks>
internal static class CitrixApplicationWindowSelector
{
    /// <summary>The processes that host published-application windows, as opposed to the Workspace shell.</summary>
    internal static readonly IReadOnlyList<string> SessionHostProcessNames = ["wfica32", "CDViewer"];

    /// <summary>The class of every seamless application window, the anchor included.</summary>
    internal const string SeamlessWindowClassName = "Transparent Windows Client";

    // The anchor also owns 1x1 helper windows, which appear a second before the real one.
    internal const long MinimumWindowAreaPx = 10_000;

    /// <summary>
    /// The anchors of <paramref name="applicationName"/> that appeared after the launch, in survey order.
    /// </summary>
    internal static IReadOnlyList<CitrixWindowRecord> FindAnchors(
        IEnumerable<CitrixWindowRecord> survey,
        IReadOnlySet<IntPtr> preLaunchWindows,
        string? applicationName)
    {
        ArgumentNullException.ThrowIfNull(survey);
        ArgumentNullException.ThrowIfNull(preLaunchWindows);

        return survey
            .Where(window => window.OwnerHwnd == IntPtr.Zero
                && !preLaunchWindows.Contains(window.Hwnd)
                && SessionHostProcessNames.Contains(window.ProcessName, StringComparer.OrdinalIgnoreCase)
                && string.Equals(window.ClassName, SeamlessWindowClassName, StringComparison.Ordinal)
                && CitrixWindowTitleMatcher.IsApplicationWindowTitle(window.Title, applicationName))
            .ToList();
    }

    /// <summary>
    /// The window to embed for <paramref name="applicationName"/>: the largest visible window owned
    /// by one of its anchors and not a helper, or null while none is there yet.
    /// </summary>
    internal static CitrixWindowRecord? Select(
        IReadOnlyCollection<CitrixWindowRecord> survey,
        IReadOnlySet<IntPtr> preLaunchWindows,
        string? applicationName)
    {
        ArgumentNullException.ThrowIfNull(survey);

        HashSet<IntPtr> anchors = FindAnchors(survey, preLaunchWindows, applicationName)
            .Select(anchor => anchor.Hwnd)
            .ToHashSet();
        if (anchors.Count == 0)
        {
            return null;
        }

        CitrixWindowRecord? best = null;
        foreach (CitrixWindowRecord window in survey)
        {
            if (window.IsVisible
                && anchors.Contains(window.OwnerHwnd)
                && window.Area >= MinimumWindowAreaPx
                && (best is null || window.Area > best.Value.Area))
            {
                best = window;
            }
        }

        return best;
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
    private const uint GaParent = 1;
    private const string EmbeddedProcessLabel = "embedded";
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
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

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

    /// <summary>
    /// The state of a window embedded in a tab, with its parent, or null when it no longer exists.
    /// Tells whether Citrix keeps a reparented seamless window where Heimdall put it.
    /// </summary>
    internal static string? DescribeEmbedded(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return null;
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        CitrixWindowRecord window = Read(hwnd, (int)pid, EmbeddedProcessLabel);
        return $"{window.Describe()} parent=0x{GetAncestor(hwnd, GaParent).ToInt64():X}";
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
    /// <param name="options">Duration and interval.</param>
    /// <param name="embeddedWindow">The window the tab embeds right now, zero for none.</param>
    /// <param name="cancellationToken">Cancelled when the tab closes.</param>
    internal static async Task RunAsync(
        CitrixWindowDiagnosticsOptions options,
        Func<IntPtr> embeddedWindow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embeddedWindow);
        ArgumentNullException.ThrowIfNull(options);

        Core.Logging.FileLogger.Info(
            $"{LogPrefix} trace started duration={options.TraceDuration.TotalSeconds}s interval={options.TraceInterval.TotalMilliseconds}ms processes={string.Join(',', CitrixWindowSurvey.TracedProcessNames)}");

        Dictionary<IntPtr, CitrixWindowRecord> previous = [];
        string? previousEmbedded = null;
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

                IntPtr embedded = embeddedWindow();
                string? embeddedState = embedded == IntPtr.Zero ? null : CitrixWindowSurvey.DescribeEmbedded(embedded) ?? CitrixWindowTraceDiff.GoneTag;
                if (embeddedState is not null && embeddedState != previousEmbedded)
                {
                    Core.Logging.FileLogger.Info($"{LogPrefix} embedded {embeddedState}");
                }

                previousEmbedded = embeddedState;

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
