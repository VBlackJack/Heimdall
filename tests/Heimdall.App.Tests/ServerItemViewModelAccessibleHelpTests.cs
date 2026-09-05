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
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// A session row announces the gestures that are its own, not WPF's.
/// </summary>
/// <remarks>
/// Ctrl+Space, Shift+Up and Shift+Down drive a multi-selection the stock tree does not have, and
/// Enter, F2 and Shift+F10 are just as invisible to a screen reader. Folders announced their two
/// gestures while the rows carrying the custom ones announced nothing.
/// </remarks>
public sealed class ServerItemViewModelAccessibleHelpTests
{
    [Fact]
    public void AccessibleHelpText_WithoutLocalizer_NamesEveryCustomGesture()
    {
        ServerItemViewModel server = CreateServer(localizer: null);

        string help = Assert.IsType<string>(server.AccessibleHelpText);
        Assert.Contains("Enter", help, StringComparison.Ordinal);
        Assert.Contains("F2", help, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Space", help, StringComparison.Ordinal);
        Assert.Contains("Shift+Up", help, StringComparison.Ordinal);
        Assert.Contains("Shift+F10", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessibleHelpText_WithFrenchLocalizer_IsTranslated()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        ServerItemViewModel server = CreateServer(localizer);

        string help = Assert.IsType<string>(server.AccessibleHelpText);
        Assert.Contains("Ctrl+Espace", help, StringComparison.Ordinal);
        Assert.Contains("Maj+F10", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessibleHelpText_IsRefreshedOnLocaleChange()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ServerItemViewModel server = CreateServer(localizer);
        List<string?> changed = [];
        server.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await localizer.SwitchLocaleAsync("fr");
        server.RefreshLocalizedState();

        Assert.Contains(nameof(ServerItemViewModel.AccessibleHelpText), changed);
        Assert.Contains("Maj+F10", server.AccessibleHelpText, StringComparison.Ordinal);
    }

    private static ServerItemViewModel CreateServer(LocalizationManager? localizer) =>
        ServerItemViewModel.FromDto(
            new ServerProfileDto
            {
                Id = "server",
                DisplayName = "server",
                RemoteServer = "server.example.test",
                ConnectionType = "SSH"
            },
            localizer: localizer);

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
