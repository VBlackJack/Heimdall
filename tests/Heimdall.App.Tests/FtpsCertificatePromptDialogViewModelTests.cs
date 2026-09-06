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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Certificates;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The FTPS certificate prompt is a first-use prompt only: a changed certificate is refused by
/// the browser before any prompt can be asked. Accept answers Enter; "Trust this session" and
/// Reject never do.
/// </summary>
public sealed class FtpsCertificatePromptDialogViewModelTests
{
    [Fact]
    public void FirstUse_AcceptIsTheOnlyDefaultButton()
    {
        FtpsCertificatePromptDialogViewModel vm = CreateViewModel();

        Assert.True(vm.AcceptIsDefault);
        Assert.False(vm.TrustOnceIsDefault);
    }

    [Fact]
    public void FirstUse_SpeaksOfAnUnknownCertificateNotAChangedOne()
    {
        LocalizationManager localizer = new();
        FtpsCertificatePromptDialogViewModel vm = CreateViewModel(localizer);

        Assert.Equal(localizer["FtpsCertificateFirstUseTitle"], vm.HeaderText);
        Assert.Equal(localizer["FtpsCertificateAcceptButton"], vm.AcceptButtonText);
    }

    private static FtpsCertificatePromptDialogViewModel CreateViewModel(LocalizationManager? localizer = null)
    {
        FtpsCertificatePrompt prompt = new(
            "ftps.example.com",
            21,
            "SHA256:presented",
            "CN=ftps.example.com",
            "CN=Example CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            string.Empty);
        return new FtpsCertificatePromptDialogViewModel(localizer ?? new LocalizationManager(), prompt);
    }
}
