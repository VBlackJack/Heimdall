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

using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>
/// Dispatcher-safe snapshot of the main window state.
/// </summary>
internal readonly record struct WindowBoundsSnapshot(
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsMaximized,
    bool IsSidebarHidden,
    double SidebarWidth)
{
    internal bool IsValid =>
        double.IsFinite(Left) &&
        double.IsFinite(Top) &&
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width > 0 &&
        Height > 0;
}

/// <summary>
/// Persists a validated window snapshot without reading dispatcher-owned state.
/// </summary>
internal static class WindowBoundsPersistence
{
    internal static SidebarLayoutProjection RestoreSidebarState(
        WindowUIState state,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);

        state.IsSidebarHidden = settings.SidebarCollapsed;
        state.SavedSidebarWidth = NormalizeSidebarWidth(settings.SidebarWidth);
        return state.SidebarLayout;
    }

    internal static WindowBoundsSnapshot CaptureSnapshot(
        double left,
        double top,
        double width,
        double height,
        bool isMaximized,
        WindowUIState state,
        double actualSidebarWidth)
    {
        ArgumentNullException.ThrowIfNull(state);

        double sidebarWidth = state.SidebarLayout.IsVisible
            && double.IsFinite(actualSidebarWidth)
            && actualSidebarWidth > 0d
                ? actualSidebarWidth
                : state.SavedSidebarWidth;

        return new WindowBoundsSnapshot(
            left,
            top,
            width,
            height,
            isMaximized,
            state.IsSidebarHidden,
            NormalizeSidebarWidth(sidebarWidth));
    }

    internal static Task PersistAsync(
        IConfigManager configManager,
        WindowBoundsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        if (!snapshot.IsValid)
        {
            return Task.CompletedTask;
        }

        return configManager.MergeSettingAsync(settings =>
        {
            settings.WindowWidth = snapshot.Width;
            settings.WindowHeight = snapshot.Height;
            settings.WindowLeft = snapshot.Left;
            settings.WindowTop = snapshot.Top;
            settings.WindowMaximized = snapshot.IsMaximized;
            settings.SidebarCollapsed = snapshot.IsSidebarHidden;
            settings.SidebarWidth = (int)Math.Round(
                NormalizeSidebarWidth(snapshot.SidebarWidth),
                MidpointRounding.AwayFromZero);
        });
    }

    private static double NormalizeSidebarWidth(double width)
    {
        if (!double.IsFinite(width))
        {
            return WindowUIState.DefaultSidebarWidth;
        }

        return Math.Clamp(
            width,
            WindowUIState.MinSidebarWidth,
            WindowUIState.MaxSidebarWidth);
    }
}
