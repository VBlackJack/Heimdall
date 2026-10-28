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

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Heimdall.App.Services;

// Heimdall.App carries a WinForms reference for monitor enumeration, so Size and
// DpiChangedEventArgs are ambiguous here. These aliases pin the WPF ones.
using DpiChangedEventArgs = System.Windows.DpiChangedEventArgs;
using Size = System.Windows.Size;
using WinForms = System.Windows.Forms;

namespace Heimdall.App.Behaviors;

/// <summary>
/// Decides the minimum size a window may actually demand, given the space the display offers.
/// </summary>
/// <remarks>
/// Kept apart from the behavior, and free of WPF types beyond <see cref="Size"/>, because this is
/// the part worth testing: attaching to a live window needs a desktop, whereas the rule itself is
/// pure arithmetic and belongs in the blocking lane.
/// </remarks>
public static class WorkingAreaMinimumPolicy
{
    /// <summary>
    /// The minimum a window may demand: its own, unless the working area cannot supply it.
    /// </summary>
    /// <remarks>
    /// A declared minimum larger than the working area does not make the window bigger - it makes
    /// it unusable, because the user can neither shrink it to fit nor reach what falls off the
    /// screen. Clamping trades a cramped window for an unreachable one.
    /// <para>
    /// A non-positive or non-finite working area means the display could not be measured, and a
    /// measurement that failed is never a reason to shrink a window: the declared minimum stands.
    /// </para>
    /// </remarks>
    public static Size Resolve(Size declaredMinimum, Size workingArea)
    {
        double width = ResolveAxis(declaredMinimum.Width, workingArea.Width);
        double height = ResolveAxis(declaredMinimum.Height, workingArea.Height);
        return new Size(width, height);
    }

    /// <summary>Whether the declared minimum exceeds what the working area can supply.</summary>
    public static bool ExceedsWorkingArea(Size declaredMinimum, Size workingArea)
        => Resolve(declaredMinimum, workingArea) != declaredMinimum;

    private static double ResolveAxis(double declared, double available)
    {
        if (double.IsNaN(declared) || declared <= 0)
        {
            return declared;
        }

        if (double.IsNaN(available) || double.IsInfinity(available) || available <= 0)
        {
            return declared;
        }

        return Math.Min(declared, available);
    }
}

/// <summary>
/// Decides how tall a window may be, and where it must start, so that it fits the working area of the
/// display its owner is on.
/// </summary>
/// <remarks>
/// <para>
/// The minimum clamp above only ever lowers a <em>minimum</em>. A dialog that declares a fixed
/// <c>Height</c> taller than the working area (750 on a 768 px display, whose working area is about
/// 728 once the taskbar is out) is not helped by it: the window is simply taller than the screen, and
/// its Save button sits under the taskbar. The height itself is left alone, because a dialog sized
/// to its content declares none (it is <see cref="double.NaN"/> when this runs): the ceiling is
/// <c>MaxHeight</c>, which WPF applies over whatever <c>Height</c> says and over the content's own
/// size alike.
/// </para>
/// <para>
/// Same discipline as the minimum policy, and free of WPF types beyond <see cref="Rect"/>, for the same
/// reason: the rule is arithmetic and belongs in the blocking lane. A working area that could not be
/// measured is never a reason to move or shrink a window.
/// </para>
/// </remarks>
public static class WorkingAreaHeightPolicy
{
    /// <summary>What to apply to the window.</summary>
    /// <param name="MaxHeight">The ceiling for the window's height.</param>
    /// <param name="Top">The window's top edge.</param>
    public readonly record struct Result(double MaxHeight, double Top);

    /// <summary>
    /// The ceiling and the top edge for a window of <paramref name="height"/> that starts at
    /// <paramref name="top"/> and declared <paramref name="declaredMaxHeight"/>.
    /// </summary>
    /// <param name="top">The window's current top edge, in the working area's units.</param>
    /// <param name="height">The window's declared height; NaN for a window sized to its content.</param>
    /// <param name="declaredMaxHeight">The ceiling the window asked for; infinity when it asked for none.</param>
    /// <param name="workingArea">The working area of the owner's display.</param>
    public static Result Resolve(double top, double height, double declaredMaxHeight, Rect workingArea)
    {
        if (!IsMeasurable(workingArea))
        {
            return new Result(declaredMaxHeight, top);
        }

        double declared = double.IsNaN(declaredMaxHeight) ? double.PositiveInfinity : declaredMaxHeight;
        double maxHeight = Math.Min(declared, workingArea.Height);

        if (double.IsNaN(top) || double.IsInfinity(top))
        {
            return new Result(maxHeight, top);
        }

        // A window of a known height is moved up just far enough to show all of it; one sized to its
        // content is not, because its height is not known here and sliding every small dialog up to
        // make room for a worst case would drag it away from where it was centred.
        bool heightKnown = !double.IsNaN(height) && height > 0;
        double effective = heightKnown ? Math.Min(height, maxHeight) : double.NaN;

        // A window that is not on this display vertically at all (a manual position on another
        // monitor) is not this clamp's to move.
        bool overlaps = top < workingArea.Bottom && (!heightKnown || top + effective > workingArea.Top);
        if (!overlaps)
        {
            return new Result(maxHeight, top);
        }

        double newTop = top;
        if (heightKnown)
        {
            newTop = Math.Min(newTop, workingArea.Bottom - effective);
        }

        // The title bar is the handle that moves the window: it is never left above the display.
        newTop = Math.Max(newTop, workingArea.Top);
        return new Result(maxHeight, newTop);
    }

    private static bool IsMeasurable(Rect area)
        => !area.IsEmpty
            && double.IsFinite(area.Top)
            && double.IsFinite(area.Height)
            && area.Height > 0;
}

/// <summary>
/// Remembers the minimum a window ASKED for, so a clamp can always be recomputed from it.
/// </summary>
/// <remarks>
/// This exists because the clamp is not idempotent across displays. Resolving from the window's
/// current minimum works once and then poisons itself: the clamped value becomes the declared one,
/// and a later application on a larger working area can no longer restore what the XAML asked for.
/// A window clamped to 800x470 on a small display stayed at 470 after being moved to a 1080-tall
/// one. Capturing once, and resolving from that capture every time, is what makes the clamp
/// reversible.
/// </remarks>
internal sealed class WorkingAreaMinimumTracker
{
    private Size? _declaredMinimum;

    /// <summary>Whether a declared minimum has been recorded for this window.</summary>
    internal bool HasCapture => _declaredMinimum.HasValue;

    /// <summary>The minimum the window declared, before any clamping.</summary>
    internal Size DeclaredMinimum => _declaredMinimum ?? default;

    /// <summary>
    /// Records the declared minimum, once. Later calls are ignored on purpose: after a clamp the
    /// window's own properties hold the clamped value, so a second capture would record it as if
    /// the XAML had asked for it.
    /// </summary>
    internal void Capture(Size declaredMinimum) => _declaredMinimum ??= declaredMinimum;

    /// <summary>The minimum to apply for a given working area, always derived from the capture.</summary>
    internal Size Resolve(Size workingArea)
        => WorkingAreaMinimumPolicy.Resolve(DeclaredMinimum, workingArea);

    /// <summary>Gives back the declared minimum and forgets it, so a later capture starts clean.</summary>
    internal Size Release()
    {
        Size declared = DeclaredMinimum;
        _declaredMinimum = null;
        return declared;
    }
}

/// <summary>
/// Holds a window's minimum size within the working area of the display it sits on.
/// </summary>
/// <remarks>
/// Opt-in per window rather than global: a window that genuinely cannot render below a size should
/// say so rather than be silently clamped, and making each one declare its intent keeps that
/// decision visible. The clamp is re-applied on DPI change, because the working area in
/// device-independent units moves with the scale factor even though the monitor did not change.
/// </remarks>
public static class WorkingAreaMinimumBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(WorkingAreaMinimumBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>
    /// Opts a dialog into the height clamp: it is kept within the working area of its owner's display,
    /// and moved up when its bottom edge would fall under the taskbar. Separate from
    /// <see cref="IsEnabledProperty"/> because it is not meant for every window that opts into the
    /// minimum clamp (the main window restores its own placement).
    /// </summary>
    public static readonly DependencyProperty ClampHeightProperty =
        DependencyProperty.RegisterAttached(
            "ClampHeight",
            typeof(bool),
            typeof(WorkingAreaMinimumBehavior),
            new PropertyMetadata(false, OnClampHeightChanged));

    public static void SetClampHeight(DependencyObject element, bool value)
        => element.SetValue(ClampHeightProperty, value);

    public static bool GetClampHeight(DependencyObject element)
        => (bool)element.GetValue(ClampHeightProperty);

    /// <summary>
    /// The ceiling the window declared before any clamp, so that the clamp is recomputed from what the
    /// XAML asked for and not from the previous clamp. Unset (null) until the first use.
    /// </summary>
    private static readonly DependencyProperty DeclaredMaxHeightProperty =
        DependencyProperty.RegisterAttached(
            "DeclaredMaxHeight",
            typeof(double?),
            typeof(WorkingAreaMinimumBehavior),
            new PropertyMetadata(null));

    /// <summary>Per-window capture of the declared minimum. Private: it is not a contract.</summary>
    private static readonly DependencyProperty TrackerProperty =
        DependencyProperty.RegisterAttached(
            "Tracker",
            typeof(WorkingAreaMinimumTracker),
            typeof(WorkingAreaMinimumBehavior),
            new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Window window)
        {
            return;
        }

        if ((bool)e.OldValue)
        {
            window.SourceInitialized -= OnWindowReady;
            window.DpiChanged -= OnWindowDpiChanged;

            // Give the window its declared minimum back, so disabling truly undoes the clamp and a
            // later re-enable captures the XAML value rather than a clamped one.
            if (window.GetValue(TrackerProperty) is WorkingAreaMinimumTracker previous && previous.HasCapture)
            {
                Size declared = previous.Release();
                window.MinWidth = declared.Width;
                window.MinHeight = declared.Height;
            }

            window.ClearValue(TrackerProperty);
        }

        if ((bool)e.NewValue)
        {
            // The tracker is created here but deliberately left EMPTY. This callback fires the
            // moment the XAML parser sets IsEnabled, which is part-way through the attribute list:
            // in some opted-in windows IsEnabled precedes the root MinHeight, so capturing now
            // would record a height of 0 and the first clamp would then write that 0 back over the
            // value the parser was about to apply. Capturing lazily, on first use, makes the
            // attribute order irrelevant - it must never become a contract.
            window.SetValue(TrackerProperty, new WorkingAreaMinimumTracker());

            window.SourceInitialized += OnWindowReady;
            window.DpiChanged += OnWindowDpiChanged;

            if (window.IsLoaded)
            {
                Apply(window);
            }
        }
    }

    private static void OnWindowReady(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            Apply(window);
        }
    }

    private static void OnClampHeightChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Window window)
        {
            return;
        }

        if ((bool)e.OldValue)
        {
            window.SourceInitialized -= OnHeightWindowReady;
            window.Loaded -= OnHeightWindowLoaded;
            window.DpiChanged -= OnHeightWindowDpiChanged;

            if (window.GetValue(DeclaredMaxHeightProperty) is double declared)
            {
                window.MaxHeight = declared;
            }

            window.ClearValue(DeclaredMaxHeightProperty);
        }

        if ((bool)e.NewValue)
        {
            window.SourceInitialized += OnHeightWindowReady;
            window.Loaded += OnHeightWindowLoaded;
            window.DpiChanged += OnHeightWindowDpiChanged;
        }
    }

    // SourceInitialized is the first moment the window has a handle and so a display to be measured
    // on. Loaded repeats it because the position a startup location computes is not always visible
    // to the property yet at that point; the clamp is idempotent, so the second pass changes nothing
    // when the first one already did the work.
    private static void OnHeightWindowReady(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            ApplyHeight(window, repositionTop: true);
        }
    }

    private static void OnHeightWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            ApplyHeight(window, repositionTop: true);
        }
    }

    private static void OnHeightWindowDpiChanged(object sender, DpiChangedEventArgs e)
    {
        if (sender is Window window)
        {
            ApplyHeight(window, repositionTop: false);
        }
    }

    private static void ApplyHeight(Window window, bool repositionTop)
    {
        try
        {
            Rect? area = GetOwnerWorkingArea(window);
            if (area is null)
            {
                return;
            }

            // Captured once, from the window's own property: a second read would find the clamp.
            double declaredMax = window.GetValue(DeclaredMaxHeightProperty) is double captured
                ? captured
                : window.MaxHeight;
            window.SetValue(DeclaredMaxHeightProperty, declaredMax);

            bool canMove = repositionTop && window.WindowState == WindowState.Normal;
            WorkingAreaHeightPolicy.Result resolved = WorkingAreaHeightPolicy.Resolve(
                canMove ? window.Top : double.NaN,
                window.Height,
                declaredMax,
                area.Value);

            window.MaxHeight = resolved.MaxHeight;

            // The ceiling alone bounds the layout; lowering a fixed Height to it as well makes the
            // window's own frame follow at once instead of waiting on the next layout pass.
            if (!double.IsNaN(window.Height) && window.Height > resolved.MaxHeight)
            {
                window.Height = resolved.MaxHeight;
            }

            if (canMove && resolved.Top != window.Top)
            {
                window.Top = resolved.Top;
            }
        }
        catch (Exception ex)
        {
            // A window that cannot be measured keeps the height it declared.
            Core.Logging.FileLogger.Warn(
                $"Working-area height clamp skipped for '{window.Title}': {ex.Message}");
        }
    }

    /// <summary>
    /// The working area, in the window's units, of the display the window's owner is on; of the
    /// window's own display when it has no owner.
    /// </summary>
    private static Rect? GetOwnerWorkingArea(Window window)
    {
        IntPtr handle = window.Owner is { } owner
            ? new WindowInteropHelper(owner).Handle
            : IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            handle = new WindowInteropHelper(window).Handle;
        }

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        if (!double.IsFinite(dpi.DpiScaleX) || !double.IsFinite(dpi.DpiScaleY)
            || dpi.DpiScaleX <= 0 || dpi.DpiScaleY <= 0)
        {
            return null;
        }

        return WindowWorkingAreaProvider.ConvertPixelsToDips(
            WinForms.Screen.FromHandle(handle).WorkingArea,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
    }

    private static void OnWindowDpiChanged(object sender, DpiChangedEventArgs e)
    {
        if (sender is Window window)
        {
            Apply(window);
        }
    }

    private static void Apply(Window window)
    {
        try
        {
            if (window.GetValue(TrackerProperty) is not WorkingAreaMinimumTracker tracker)
            {
                return;
            }

            // First use is where the declared minimum is read, because it is the earliest moment at
            // which the whole attribute list has been applied. Capture is idempotent, so every
            // later Apply keeps the first reading rather than the previous clamp.
            tracker.Capture(new Size(window.MinWidth, window.MinHeight));

            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            IReadOnlyList<Rect> areas = WindowWorkingAreaProvider.GetWorkingAreas(dpi);
            if (areas.Count == 0)
            {
                return;
            }

            // The smallest working area available, not the one the window currently sits on: the
            // user may drag it to any display, and a minimum that only fits the largest monitor
            // strands the window the moment it is moved.
            Size smallest = new(
                areas.Min(area => area.Width),
                areas.Min(area => area.Height));

            // From the CAPTURE, never from the window's current minimum: the latter already holds
            // the previous clamp, so resolving from it would make every clamp permanent.
            Size resolved = tracker.Resolve(smallest);

            window.MinWidth = resolved.Width;
            window.MinHeight = resolved.Height;
        }
        catch (Exception ex)
        {
            // A window that cannot be measured keeps its declared minimum: worse than clamped, but
            // never worse than a window that fails to open.
            Core.Logging.FileLogger.Warn(
                $"Working-area minimum clamp skipped for '{window.Title}': {ex.Message}");
        }
    }
}
