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
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The WinRM check boxes sit in the session dialog's 150 px port column. Their text did not wrap,
/// and "Skip certificate validation (insecure)" read "Skip certificate vali" on screen (seen
/// 2026-10-18); French and Spanish are longer still.
/// </summary>
/// <remarks>
/// The first fix only changed the markup, and the screen did not change: ApplyLocalization wrote a
/// plain string into each box's Content, which replaced the wrapping TextBlock at load. So the text
/// is checked where the code writes it, not only where the markup declares it.
/// </remarks>
public sealed class ServerDialogWinRmLabelWrapTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("DlgSrv_WinRmSslCb", "DlgSrv_WinRmSslText")]
    [InlineData("DlgSrv_WinRmSkipCertCb", "DlgSrv_WinRmSkipCertText")]
    public void AWinRmCheckBoxLabelIsAWrappingTextBlock(string checkBoxName, string textName)
    {
        XDocument dialog = XDocument.Load(DialogPath("ServerDialog.xaml"));
        XElement checkBox = Assert.Single(dialog.Descendants(), e => (string?)e.Attribute(X + "Name") == checkBoxName);

        XElement label = Assert.Single(checkBox.Elements(), e => e.Name.LocalName == "TextBlock");
        Assert.Equal(textName, (string?)label.Attribute(X + "Name"));
        Assert.Equal("Wrap", (string?)label.Attribute("TextWrapping"));
    }

    [Theory]
    [InlineData("DlgSrv_WinRmSslCb", "DlgSrv_WinRmSslText")]
    [InlineData("DlgSrv_WinRmSkipCertCb", "DlgSrv_WinRmSkipCertText")]
    public void TheLocalizedTextGoesIntoTheTextBlockNotOverIt(string checkBoxName, string textName)
    {
        string code = ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(DialogPath("ServerDialog.xaml.cs")));
        string logic = ViewSource.HandlerBody(code, "private void ApplyLocalization(");

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, textName + ".Text = _localizer["), $"ApplyLocalization does not write {textName}");
        Assert.DoesNotMatch(new Regex(@"\b" + checkBoxName + @"\.Content\s*="), code);
    }

    private static string DialogPath(string file) =>
        Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Dialogs", file);
}
