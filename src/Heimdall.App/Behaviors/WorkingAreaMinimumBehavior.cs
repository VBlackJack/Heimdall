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
using System.Windows.Media;
using Heimdall.App.Services;

// Heimdall.App carries a WinForms reference for monitor enumeration, so Size and
// DpiChangedEventArgs are ambiguous here. These aliases pin the WPF ones.
using DpiChangedEventArgs = System.Windows.DpiChangedEventArgs;
using Size = System.Windows.Size;

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
            // in every dialog wired so far, IsEnabled precedes the root MinHeight, so capturing now
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
