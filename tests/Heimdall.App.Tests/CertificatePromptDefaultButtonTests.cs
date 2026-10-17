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
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Certificates;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// Which button answers Enter on the certificate prompt. When a web console presents a
/// certificate that replaces a trusted one, Reject does; before, no button was the default but
/// Accept held the focus, so Enter trusted what may be an interception.
/// </summary>
public sealed class CertificatePromptDefaultButtonTests
{
    private const string DialogXaml = "FtpsCertificatePromptDialog.xaml";
    private const string OnLoadedSignature = "private void OnLoaded(";
    private const string PickFirstStatement = "Button first = _viewModel?.RejectIsDefault == true ? RejectButton : AcceptButton;";
    private const string FocusFirstStatement = "first.Focus();";

    [Fact]
    public void AChangedCertificateRejectsOnEnter()
    {
        FtpsCertificatePromptDialogViewModel vm = Create(CertificatePromptWording.WebChanged);

        Assert.True(vm.RejectIsDefault);
        Assert.False(vm.AcceptIsDefault);
        Assert.False(vm.TrustOnceIsDefault);
        Assert.True(vm.IsWarning);
    }

    [Theory]
    [MemberData(nameof(FirstUseWordings))]
    public void AFirstUseCertificateAcceptsOnEnter(CertificatePromptWording wording)
    {
        FtpsCertificatePromptDialogViewModel vm = Create(wording);

        Assert.True(vm.AcceptIsDefault);
        Assert.False(vm.RejectIsDefault);
        Assert.False(vm.IsWarning);
    }

    public static TheoryData<CertificatePromptWording> FirstUseWordings { get; } = new()
    {
        CertificatePromptWording.Ftps,
        CertificatePromptWording.WebFirstUse,
    };

    [Fact]
    public void TheRejectButtonTakesItsDefaultFromTheViewModel()
    {
        XElement reject = NamedButton("RejectButton");

        Assert.Equal("{Binding RejectIsDefault}", (string?)reject.Attribute("IsDefault"));
        Assert.Equal("True", (string?)reject.Attribute("IsCancel"));
    }

    [Fact]
    public void TheDialogFocusesRejectWhenRejectIsTheDefault()
    {
        // A focused button answers Enter whatever IsDefault says, so the focus has to follow.
        string source = File.ReadAllText(DialogPath(DialogXaml + ".cs"));
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(source), OnLoadedSignature);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, PickFirstStatement), "OnLoaded does not pick the focused button from RejectIsDefault");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, FocusFirstStatement), "OnLoaded does not focus the button it picked");
    }

    private static XElement NamedButton(string name)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument dialog = XDocument.Load(DialogPath(DialogXaml));
        return Assert.Single(
            dialog.Descendants(),
            e => e.Name.LocalName == "Button" && (string?)e.Attribute(x + "Name") == name);
    }

    private static string DialogPath(string fileName) => Path.Combine(
        ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Dialogs", fileName);

    private static FtpsCertificatePromptDialogViewModel Create(CertificatePromptWording wording)
    {
        FtpsCertificatePrompt prompt = new(
            "console.example.com",
            443,
            "SHA256:presented",
            "CN=console.example.com",
            "CN=Example CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            string.Empty);
        return new FtpsCertificatePromptDialogViewModel(new LocalizationManager(), prompt, wording);
    }
}
