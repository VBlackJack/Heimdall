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
using Heimdall.App.ViewModels;

namespace Heimdall.App.Services;

/// <summary>
/// Builds the menu of every open session tab, shown by the button at the end of an overflowing
/// tab strip.
/// </summary>
/// <remarks>
/// Every tab is listed, those scrolled out of the strip and those folded away in a collapsed
/// folder included: the menu exists for the tabs the strip is not showing. Each entry carries the
/// tab's announced name, which already tells two tabs of the same title apart, and its folder in
/// the shortcut column when the tabs are arranged by folder.
/// </remarks>
internal static class TabListMenuFactory
{
    internal static ContextMenu Create(ConnectionViewModel connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var menu = new ContextMenu();
        foreach (SessionTabViewModel session in connection.ActiveSessions)
        {
            bool isActive = ReferenceEquals(session, connection.ActiveSession);

            // The open tab is drawn semi-bold: the menu theme draws no tick on an item that is not
            // checkable, so IsChecked alone left it unmarked on screen. IsChecked stays for UIA.
            var item = new MenuItem
            {
                InputGestureText = session.TabGroupLabel,
                IsChecked = isActive,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
            };
            LiteralAccessText.SetLiteral(
                item,
                string.IsNullOrEmpty(session.AccessibleName) ? session.DisplayTitle : session.AccessibleName);
            item.Click += (_, _) => connection.ActiveSession = session;
            menu.Items.Add(item);
        }

        return menu;
    }
}
