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
using Heimdall.App.Views;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// The detail the status line shows when the control's events cannot be attached is in the
/// user's language, not an English sentence written into the view.
/// </summary>
public sealed class RdpEventSinkAttachFailureTextTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task WithoutAControlError_TheDetailIsTheLocalizedSentence(string language)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), language);

        string detail = EmbeddedRdpView.EventSinkAttachFailureMessage(null, key => localizer[key]);

        Assert.Equal(localizer[EmbeddedRdpView.LocaleKeys.ErrorEventSinkAttachFailed], detail);
        Assert.NotEqual(EmbeddedRdpView.LocaleKeys.ErrorEventSinkAttachFailed, detail);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Fact]
    public async Task InFrench_TheDetailIsNotTheEnglishSentence()
    {
        LocalizationManager english = new();
        await english.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        LocalizationManager french = new();
        await french.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "fr");

        Assert.NotEqual(
            EmbeddedRdpView.EventSinkAttachFailureMessage(null, key => english[key]),
            EmbeddedRdpView.EventSinkAttachFailureMessage(null, key => french[key]));
    }

    [Fact]
    public void TheControlsOwnError_IsShownWhenItGaveOne()
    {
        Assert.Equal(
            "0x80004005",
            EmbeddedRdpView.EventSinkAttachFailureMessage("0x80004005", key => key));
    }
}
