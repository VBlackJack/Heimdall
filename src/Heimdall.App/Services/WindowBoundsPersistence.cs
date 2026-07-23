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
    bool IsMaximized)
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
        });
    }
}
