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
using Heimdall.Core.Localization;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The markup half of the accessibility pass: what a view-model or arithmetic test cannot reach
/// because it is a binding, a style or a wiring line.
/// </summary>
/// <remarks>
/// These read the source, which is the last resort the project allows: none of it can be driven
/// without a window, and a window poisons the dispatcher the rest of the suite shares. Each test
/// names what it holds, and the behaviour behind it is covered where one can be driven (the notes
/// view-model, the vault dialog view-models, the height policy, the name view-models).
/// </remarks>
public sealed class AccessibilityPassMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // --- The note editors are named (U-06) ---

    [Theory]
    [InlineData("Editor")]
    [InlineData("MilkdownEditor")]
    public void ANoteEditor_IsNamedFromTheLocalizedFormatAndTheNoteTitle(string name)
    {
        XElement editor = Named("Views/Tools/NotesToolView.xaml", name);
        XElement nameProperty = Assert.Single(
            editor.Elements(),
            child => child.Name.LocalName == "AutomationProperties.Name");
        string markup = nameProperty.ToString();

        Assert.Contains("[A11yNoteContent]", markup, StringComparison.Ordinal);
        Assert.Contains("SelectedNoteTitle", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWebViewOfTheRichEditor_TakesTheNameTheHostGivesTheControl()
    {
        XElement webView = Named("Views/Tools/MilkdownEditorControl.xaml", "EditorWebView");

        Assert.Contains(
            "(AutomationProperties.Name)",
            (string?)webView.Attribute("AutomationProperties.Name"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheTextAreaOfTheSourceEditor_TakesTheNameOfTheEditor()
    {
        string source = ViewSource.WithoutCommentsAndLiterals(
            File.ReadAllText(Path.Combine(AppRoot(), "Views", "Tools", "NotesToolView.xaml.cs")));

        // Keyboard focus lands on the text area, not on the editor the markup names.
        string constructor = ViewSource.HandlerBody(source, "public NotesToolView()");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(constructor, "ForwardEditorNameToTextArea();"),
            "the view's constructor does not forward the editor's name to its text area");

        string forward = ViewSource.HandlerBody(source, "private void ForwardEditorNameToTextArea()");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                forward,
                "BindingOperations.SetBinding(Editor.TextArea, AutomationProperties.NameProperty, forwarded);"),
            "the forwarding method does not bind the text area's name");
    }

    // --- The tag filters are a single-select list (U-10) ---

    [Fact]
    public void TheTagFilters_AreASingleSelectListBoundToTheSelectedChip()
    {
        XElement list = Named("Views/Tools/NotesToolView.xaml", "TagFilterWrapPanel");

        Assert.Equal("ListBox", list.Name.LocalName);
        Assert.Equal("Single", (string?)list.Attribute("SelectionMode"));
        Assert.Contains("TagFilters", (string?)list.Attribute("ItemsSource"), StringComparison.Ordinal);
        Assert.Contains("SelectedTagFilter", (string?)list.Attribute("SelectedItem"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNotesView_HasNoToggleButtonAndNoButtonForATagFilter()
    {
        XDocument view = Load("Views/Tools/NotesToolView.xaml");

        // A ToggleButton bound one way with a command desyncs under UI Automation, whose Toggle
        // pattern flips IsChecked without a click.
        Assert.Empty(view.Descendants(Presentation + "ToggleButton"));
        Assert.DoesNotContain(
            view.Descendants(Presentation + "Button"),
            button => ((string?)button.Attribute("Command"))?.Contains("SelectTagCommand", StringComparison.Ordinal) == true);
    }

    // --- The save-time summaries are live regions (U-07) ---

    [Theory]
    [InlineData("Views/Dialogs/ScheduledTaskDialog.xaml")]
    [InlineData("Views/Dialogs/GatewayDialog.xaml")]
    [InlineData("Views/Dialogs/RunbookDialog.xaml")]
    [InlineData("Views/Dialogs/CommandActionDialog.xaml")]
    [InlineData("Views/Dialogs/ServerDialog.xaml")]
    public void TheSaveTimeSummary_IsAnAnnouncedRegion(string file)
    {
        List<XElement> summaries =
        [
            .. Load(file).Descendants(Presentation + "TextBlock")
                .Where(block => ((string?)block.Attribute("Text"))?.StartsWith("{Binding ValidationError", StringComparison.Ordinal) == true)
        ];

        Assert.NotEmpty(summaries);
        foreach (XElement summary in summaries)
        {
            Assert.Equal("{StaticResource FieldValidationErrorStyle}", (string?)summary.Attribute("Style"));

            // The same text refused twice in a row changes nothing but the binding's target update.
            Assert.Contains("NotifyOnTargetUpdated=True", (string?)summary.Attribute("Text"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheStyleOfTheSummary_CarriesTheLiveRegion()
    {
        XElement style = Assert.Single(
            Load("Themes/DialogCommonStyles.xaml").Descendants(Presentation + "Style"),
            candidate => (string?)candidate.Attribute(Xaml + "Key") == "FieldValidationErrorStyle");
        string markup = style.ToString();

        Assert.Contains("AutomationProperties.LiveSetting", markup, StringComparison.Ordinal);
        Assert.Contains("LiveRegionBehavior.AnnounceOnTargetUpdated", markup, StringComparison.Ordinal);
    }

    // --- The vault dialogs put the focus back in the password box (U-03) ---

    [Theory]
    [InlineData("VaultUnlockDialog", "PasswordBox")]
    [InlineData("VaultChangePasswordDialog", "CurrentPasswordBox")]
    [InlineData("VaultEnableDialog", "NewPasswordBox")]
    [InlineData("VaultDisableDialog", "PasswordBox")]
    public void AVaultDialog_WiresItsPasswordBoxToTheRefocusSignal(string dialog, string box)
    {
        string source = ViewSource.WithoutCommentsAndLiterals(
            File.ReadAllText(Path.Combine(AppRoot(), "Views", "Dialogs", dialog + ".xaml.cs")));

        string constructor = ViewSource.HandlerBody(source, $"public {dialog}()");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(constructor, $"PasswordBoxRefocus.Attach(this, {box});"),
            $"{dialog} does not wire {box} to the refocus signal of its view-model");
    }

    [Fact]
    public void EveryVaultDialogViewModel_ExposesTheRefocusSignalTheDialogsPickUp()
    {
        LocalizationManager localizer = new();

        Assert.IsAssignableFrom<IPasswordRefocusHost>(new VaultUnlockDialogViewModel(
            _ => Task.CompletedTask, new PinManager(), localizer, migrationInProgress: false));
        Assert.IsAssignableFrom<IPasswordRefocusHost>(new VaultChangePasswordDialogViewModel((_, _) => Task.CompletedTask, localizer));
        Assert.IsAssignableFrom<IPasswordRefocusHost>(new VaultEnableDialogViewModel(_ => Task.CompletedTask, localizer));
        Assert.IsAssignableFrom<IPasswordRefocusHost>(new VaultDisableDialogViewModel(_ => Task.CompletedTask, localizer));
    }

    // --- Helpers ---

    private static XElement Named(string file, string name) =>
        Assert.Single(Load(file).Descendants(), element => (string?)element.Attribute(Xaml + "Name") == name);

    private static XDocument Load(string file) => XDocument.Load(Path.Combine(AppRoot(), file));

    private static string AppRoot() => Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
}
