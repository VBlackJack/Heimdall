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

namespace Heimdall.App.Tests;

/// <summary>
/// A string shown by a control that reads access keys loses its first underscore: the controls
/// below show data or a label with a literal underscore, so they draw it through the shared
/// template instead of handing it to the parser.
/// </summary>
/// <remarks>
/// Read from the markup, not from a window: the themes and views need the application's
/// resources. What is established is the markup shape, and that is the cause of the defect.
/// </remarks>
public sealed class AccessKeyLiteralMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ComboBoxItemStyle_DoesNotReadAccessKeys_AndTheClosedBoxNeverDid()
    {
        XDocument common = Load("Themes", "CommonControls.xaml");

        XElement itemStyle = Style(common, "ThemedComboBoxItemStyle");
        XElement itemPresenter = Assert.Single(itemStyle.Descendants(Presentation + "ContentPresenter"));
        Assert.Equal("False", (string?)itemPresenter.Attribute("RecognizesAccessKey"));

        XElement comboStyle = Style(common, "ThemedComboBoxStyle");
        foreach (XElement presenter in comboStyle.Descendants(Presentation + "ContentPresenter"))
        {
            Assert.NotEqual("True", (string?)presenter.Attribute("RecognizesAccessKey"));
        }
    }

    [Fact]
    public void LiteralTextTemplate_IsASharedResource_DrawingTheContentInATextBlock()
    {
        XElement template = Assert.Single(
            Load("Themes", "CommonControls.xaml").Descendants(Presentation + "DataTemplate"),
            element => (string?)element.Attribute(Xaml + "Key") == "LiteralTextTemplate");

        XElement text = Assert.Single(template.Descendants(Presentation + "TextBlock"));
        Assert.Equal("{Binding}", (string?)text.Attribute("Text"));
    }

    // The content stays a string, because the settings search reads Content only when it is one; the
    // template is what keeps an underscore from being read as an access key.
    [Theory]
    [InlineData("Views/Tools/TextCaseConverterView.xaml", "BtnSnakeCase")]
    [InlineData("Views/Tools/TextCaseConverterView.xaml", "BtnConstantCase")]
    [InlineData("MainWindow.xaml", "Mw_SettingsSyncKnownHostsAtStartup")]
    public void LabelWithALiteralUnderscore_KeepsStringContentThroughTheSharedTemplate(string file, string name)
    {
        XElement control = Named(Load(file.Split('/')), name);

        AssertStringContentWithTemplate(control);
        Assert.NotNull(control.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void DataBoundControlContent_KeepsStringContentThroughTheSharedTemplate()
    {
        XDocument main = Load("MainWindow.xaml");
        XElement addButton = Assert.Single(
            main.Descendants(Presentation + "Button"),
            button => (string?)button.Attribute("AutomationProperties.Name") == "{Binding AddAccessibleName}");
        AssertStringContentWithTemplate(addButton);
        Assert.Equal("{Binding AddLabel}", (string?)addButton.Attribute("Content"));

        XElement protocolStyle = Assert.Single(
            main.Descendants(Presentation + "Style"),
            style => style.Elements(Presentation + "Setter").Any(setter =>
                (string?)setter.Attribute("Property") == "Header"
                && (string?)setter.Attribute("Value") == "{Binding DisplayName}"));
        Assert.Contains(
            protocolStyle.Elements(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "HeaderTemplate"
                && (string?)setter.Attribute("Value") == SharedTemplate);

        foreach ((string file, string binding) in new[]
        {
            ("Views/Dialogs/SnapshotRestoreDialog.xaml", "{Binding DisplayName}"),
            ("Views/Tools/CommandLibraryView.xaml", "{Binding AccessibleName}"),
        })
        {
            XElement check = Assert.Single(
                Load(file.Split('/')).Descendants(Presentation + "CheckBox"),
                box => (string?)box.Attribute("Content") == binding
                    && (string?)box.Attribute("ContentTemplate") == SharedTemplate);
            Assert.Equal(binding, (string?)check.Attribute("AutomationProperties.Name"));
        }
    }

    private const string SharedTemplate = "{StaticResource LiteralTextTemplate}";

    private static void AssertStringContentWithTemplate(XElement control)
    {
        Assert.NotNull(control.Attribute("Content"));
        Assert.Equal(SharedTemplate, (string?)control.Attribute("ContentTemplate"));
    }

    private static XElement Style(XDocument document, string key)
    {
        return Assert.Single(
            document.Descendants(Presentation + "Style"),
            style => (string?)style.Attribute(Xaml + "Key") == key);
    }

    private static XElement Named(XDocument document, string name)
    {
        return Assert.Single(
            document.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == name);
    }

    private static XDocument Load(params string[] relative)
    {
        string path = Path.Combine([ViewSource.RepoRoot(), "src", "Heimdall.App", .. relative]);
        Assert.True(File.Exists(path), $"Missing XAML file: {path}");
        return XDocument.Load(path);
    }
}
