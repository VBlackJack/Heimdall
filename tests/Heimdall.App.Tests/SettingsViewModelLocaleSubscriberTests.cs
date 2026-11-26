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

using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed partial class SettingsViewModelTests
{
    // A subscriber that fails while the language is being re-applied is the localizer's business:
    // the string table is already swapped, so the box must keep the new pick and no warning may
    // claim the language was not changed. Subscribers after the failing one still run.
    [Fact]
    public async Task AFailingLocaleSubscriber_DoesNotUndoOrReportTheLanguagePick()
    {
        LocalizationManager localizer = await CreateProductLocalizerAsync();
        FakeConfigManager config = new();
        FakeDialogService dialog = new();
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);
        string? laterSubscriberLocale = null;
        localizer.LocaleChanged += _ => throw new InvalidOperationException("subscriber failure");
        localizer.LocaleChanged += locale => laterSubscriberLocale = locale;

        viewModel.DefaultLocale = "fr";
        await viewModel.WhenLocaleAppliedAsync();

        Assert.Equal("fr", localizer.CurrentLocale);
        Assert.Equal("fr", viewModel.DefaultLocale);
        Assert.Empty(dialog.WarningCalls);
        Assert.Equal("fr", laterSubscriberLocale);
    }
}
