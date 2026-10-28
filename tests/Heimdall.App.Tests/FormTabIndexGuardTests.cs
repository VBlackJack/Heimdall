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
using System.Xml;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The key, certificate, password and gateway forms leave their tab order to the layout.
/// </summary>
/// <remarks>
/// <para>
/// WPF gives a container the lowest <c>TabIndex</c> found inside it, and sorts the containers
/// that hold none after every numbered one. Numbering part of a form therefore moved the
/// numbered controls ahead of the rest: the Generate button of the key and certificate forms took
/// the focus before the fields above it. The UI tests in <c>FormTabOrderTests</c> perform the Tab
/// gesture on the forms that can be built outside the application; the password generator cannot
/// (it refuses to be created without the composition root, on purpose), so this guard reads the
/// markup of all of them.
/// </para>
/// <para>
/// A control that must come before a sibling it sits after in the markup is declared first and
/// placed with the layout (a Grid column), not numbered.
/// </para>
/// </remarks>
public sealed class FormTabIndexGuardTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly string[] TabIndexAttributes = ["TabIndex", "KeyboardNavigation.TabIndex"];

    /// <summary>The forms whose tab order is the order of their markup.</summary>
    public static TheoryData<string> Forms { get; } = new()
    {
        Path.Combine("Views", "Tools", "PasswordGeneratorView.xaml"),
        Path.Combine("Views", "Tools", "CertInspectorView.xaml"),
        Path.Combine("Views", "Tools", "CertificateGeneratorView.xaml"),
        Path.Combine("Views", "Tools", "SshKeyGeneratorView.xaml"),
        Path.Combine("Views", "Tools", "SshKeyAuditView.xaml"),
        Path.Combine("Views", "Dialogs", "GatewayDialog.xaml"),
        Path.Combine("Views", "Dialogs", "ServerBulkEditGatewayDialog.xaml"),
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public void TheFormNumbersNoControl(string form)
    {
        XDocument markup = XDocument.Load(
            Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", form),
            LoadOptions.SetLineInfo);

        Assert.Empty(NumberedElements(markup).Select(e => $"{e.Name.LocalName} at line {((IXmlLineInfo)e).LineNumber}"));
    }

    /// <summary>Positive control: the scan finds a number wherever one is written.</summary>
    [Fact]
    public void TheScan_FindsAControlThatIsNumbered()
    {
        XDocument inline = XDocument.Parse(
            $"<StackPanel xmlns=\"{Presentation}\"><Button TabIndex=\"1\"/><WrapPanel KeyboardNavigation.TabIndex=\"0\"/><Button/></StackPanel>");

        Assert.Equal(["Button", "WrapPanel"], NumberedElements(inline).Select(e => e.Name.LocalName));
    }

    private static IEnumerable<XElement> NumberedElements(XDocument markup)
        => markup.Descendants().Where(e => TabIndexAttributes.Any(name => e.Attribute(name) is not null));
}
