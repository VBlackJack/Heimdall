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
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class FolderViewModelAccessibilityTests
{
    [Fact]
    public void AccessibleMetadata_WithoutLocalizer_UsesEnglishFallbacks()
    {
        var folder = new FolderViewModel
        {
            Name = "Production"
        };

        Assert.Equal("Production, folder", folder.AccessibleName);
        Assert.StartsWith("Folder.", folder.AccessibleHelpText, StringComparison.Ordinal);
        Assert.Contains("Enter", folder.AccessibleHelpText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessibleMetadata_WithFrenchLocalizer_IdentifiesFolderAndKeyboardActions()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        var folder = new FolderViewModel(localizer)
        {
            Name = "Production"
        };

        Assert.Equal("Production, dossier", folder.AccessibleName);
        Assert.Contains("Maj+F10", folder.AccessibleHelpText, StringComparison.Ordinal);
        Assert.Contains("Entr\u00e9e", folder.AccessibleHelpText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshLocalizedState_AfterLocaleChange_RaisesAutomationMetadata()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        var folder = new FolderViewModel(localizer)
        {
            Name = "Production"
        };
        var changed = new List<string?>();
        folder.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        await localizer.SwitchLocaleAsync("fr");
        folder.RefreshLocalizedState();

        Assert.Equal("Production, dossier", folder.AccessibleName);
        Assert.Contains(nameof(FolderViewModel.AccessibleName), changed);
        Assert.Contains(nameof(FolderViewModel.AccessibleHelpText), changed);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
