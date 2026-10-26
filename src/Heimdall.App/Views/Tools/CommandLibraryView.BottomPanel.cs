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
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// The grip that lets the operator choose the height of the bottom panel (generator, history,
/// runbooks) by mouse or keyboard, and remembers it.
/// </summary>
/// <remarks>
/// <para>One height is shared by the three panels: they take turns in the same place, and a
/// height that changed with each panel would make the list jump every time one replaced
/// another.</para>
/// <para>The height is only ever a request. Each panel's <c>MaxHeight</c> still leaves the list
/// its reserve, so a height chosen on a tall window cannot push the list off a shorter one.</para>
/// </remarks>
public partial class CommandLibraryView
{
    /// <summary>How far one press of Up or Down moves the grip.</summary>
    internal const double BottomPanelKeyboardStep = 16;

    /// <summary>
    /// Writes the chosen height, 0 meaning automatic. Replaceable so a test can observe the
    /// write without touching the developer's settings file.
    /// </summary>
    internal Action<int> PersistBottomPanelHeight { get; set; } = PersistBottomPanelHeightToSettings;

    /// <summary>Gives the three bottom panels a height; <see cref="double.NaN"/> restores the automatic one.</summary>
    internal void ApplyBottomPanelHeight(double height)
    {
        foreach (var panel in BottomPanels())
        {
            panel.Height = height;
        }
    }

    /// <summary>
    /// Grows (positive) or shrinks (negative) the open bottom panel, keeping it between its own
    /// minimum and what the body leaves once the list has its reserve.
    /// </summary>
    internal void ResizeBottomPanelBy(double delta)
    {
        var panel = BottomPanels().FirstOrDefault(candidate => candidate.IsVisible);
        if (panel is null)
        {
            return;
        }

        double minimum = (double)FindResource("CmdLibBottomPanelMinHeight");
        double reserve = (double)FindResource("CmdLibActionListMinHeight");
        double room = Math.Max(minimum, BodyPanel.ActualHeight - reserve);

        // The explicit height, once there is one, rather than the laid-out one: a held arrow key
        // repeats faster than layout runs, and reading ActualHeight would apply one step per frame.
        double current = double.IsNaN(panel.Height) ? panel.ActualHeight : Math.Min(panel.Height, room);

        ApplyBottomPanelHeight(Math.Clamp(current + delta, minimum, room));
    }

    /// <summary>Restores the automatic height and forgets the chosen one.</summary>
    internal void ResetBottomPanelHeight()
    {
        ApplyBottomPanelHeight(double.NaN);
        PersistBottomPanelHeight(0);
    }

    /// <summary>Applies the height remembered from an earlier session, if there is one.</summary>
    internal void RestoreBottomPanelHeight(AppSettings? settings)
    {
        if (settings?.CmdLibBottomPanelHeight is int saved and > 0)
        {
            ApplyBottomPanelHeight(saved);
        }
    }

    private void PersistCurrentBottomPanelHeight()
    {
        double height = GeneratorPanel.Height;
        if (!double.IsNaN(height))
        {
            PersistBottomPanelHeight((int)Math.Round(height));
        }
    }

    private IEnumerable<FrameworkElement> BottomPanels()
    {
        yield return GeneratorPanel;
        yield return HistoryPanel;
        yield return RunbooksPanel;
    }

    // A drag moving up has a negative vertical change and must grow the panel.
    private void OnBottomPanelGripDragDelta(object sender, DragDeltaEventArgs e)
        => ResizeBottomPanelBy(-e.VerticalChange);

    private void OnBottomPanelGripDragCompleted(object sender, DragCompletedEventArgs e)
        => PersistCurrentBottomPanelHeight();

    private void OnBottomPanelGripDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ResetBottomPanelHeight();
        e.Handled = true;
    }

    private void OnBottomPanelGripKeyDown(object sender, KeyEventArgs e)
    {
        double delta = e.Key switch
        {
            Key.Up => BottomPanelKeyboardStep,
            Key.Down => -BottomPanelKeyboardStep,
            _ => 0
        };
        if (delta == 0)
        {
            return;
        }

        ResizeBottomPanelBy(delta);
        PersistCurrentBottomPanelHeight();
        e.Handled = true;
    }

    private static void PersistBottomPanelHeightToSettings(int height)
    {
        try
        {
            // The merge also republishes ConfigManager.CurrentSettings, which is what the next
            // library tab reads, so a tab opened later in the same session gets this height too.
            _ = (Application.Current as App)?.Services?.GetService<IConfigManager>()?
                .MergeSettingAsync(s => s.CmdLibBottomPanelHeight = height);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[CommandLibrary] Could not remember the bottom panel height: {ex.Message}");
        }
    }
}
