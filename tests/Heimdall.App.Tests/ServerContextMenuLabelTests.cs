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

using System.IO;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The connect entries an RDP session's context menu shows one under the other.
/// </summary>
public sealed class ServerContextMenuLabelTests
{
    /// <summary>
    /// The three entries sit adjacent in every RDP session's menu (the order is frozen by
    /// NonToolContextMenus_PreserveServerAndFolderItemOrder) and they do three different things:
    /// connect now, pick the RDP rendering mode for this one connect, re-dial the host over
    /// another protocol. The French build shipped the same words for two of them, which leaves a
    /// reader with nothing to choose on. It also destroys the label as an identifier: menu
    /// lookups in this suite return the first header that matches, so one collision silently
    /// binds a test to the wrong submenu.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task ConnectEntries_ReadDifferentlyInEveryLocale(string locale)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);
        string[] keys = ["TreeCtxConnect", "MenuItemConnectWith", "TreeCtxConnectAs"];

        string[] labels = keys.Select(key => localizer[key]).ToArray();

        for (int index = 0; index < keys.Length; index++)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(labels[index]),
                $"{keys[index]} has no value in {locale}.");
            Assert.NotEqual(keys[index], labels[index]);
        }

        List<string> collisions = labels
            .Select((label, index) => (Label: label, Key: keys[index]))
            .GroupBy(entry => entry.Label, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"\"{group.Key}\" <- {string.Join(", ", group.Select(e => e.Key))}")
            .ToList();

        Assert.True(
            collisions.Count == 0,
            $"Two connect entries read alike in {locale}, one line apart in the same menu:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, collisions));
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
