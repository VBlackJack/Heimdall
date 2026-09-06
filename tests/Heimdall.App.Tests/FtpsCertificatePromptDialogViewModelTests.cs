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
/// Pins which button answers Enter on the FTPS certificate prompt. A changed
/// certificate is the one case where the reflex "Enter accepts" must not
/// connect the session, the same rule the SSH host key prompt follows.
/// </summary>
public sealed class FtpsCertificatePromptDialogViewModelTests
{
    [Fact]
    public void FirstUse_AcceptIsTheOnlyDefaultButton()
    {
        FtpsCertificatePromptDialogViewModel vm = CreateViewModel(storedFingerprint: null);

        Assert.False(vm.IsMismatch);
        Assert.True(vm.AcceptIsDefault);
        Assert.False(vm.TrustOnceIsDefault);
        Assert.False(vm.RejectIsDefault);
    }

    [Fact]
    public void Mismatch_RejectIsTheOnlyDefaultButton()
    {
        FtpsCertificatePromptDialogViewModel vm = CreateViewModel(storedFingerprint: "SHA256:stored");

        Assert.True(vm.IsMismatch);
        Assert.True(vm.RejectIsDefault);
        Assert.False(vm.TrustOnceIsDefault);
        Assert.False(vm.AcceptIsDefault);
    }

    private static FtpsCertificatePromptDialogViewModel CreateViewModel(string? storedFingerprint)
    {
        LocalizationManager localizer = new();
        FtpsCertificatePrompt prompt = new(
            "ftps.example.com",
            21,
            "SHA256:presented",
            storedFingerprint,
            "CN=ftps.example.com",
            "CN=Example CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            string.Empty);
        return new FtpsCertificatePromptDialogViewModel(localizer, prompt);
    }
}
