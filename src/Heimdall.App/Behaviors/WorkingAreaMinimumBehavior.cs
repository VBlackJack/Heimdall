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
        }

        if ((bool)e.NewValue)
        {
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

            Size resolved = WorkingAreaMinimumPolicy.Resolve(
                new Size(window.MinWidth, window.MinHeight),
                smallest);

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
