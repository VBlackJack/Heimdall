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
using Heimdall.App.Views.Tools;
using Heimdall.Core.Network;

namespace Heimdall.App.Tests;

/// <summary>
/// What a screen reader and a keyboard meet on the PIN, vault and lock surfaces, the session
/// overlays, the pane splitters and two tool results.
/// </summary>
/// <remarks>
/// A <c>LiveSetting</c> on its own announces nothing in WPF: the live-region event has to be
/// raised on the element's peer, which is what <c>LiveRegionBehavior</c> does when the element
/// appears or its binding updates. Each region below therefore carries both.
/// </remarks>
public sealed class AccessibilityAnnouncementMarkupTests
{
    private const string LiveSetting = "AutomationProperties.LiveSetting";
    private const string Announce = "LiveRegionBehavior.AnnounceOnTargetUpdated";

    /// <summary>
    /// The live regions this file pins: the file under <c>src/Heimdall.App</c>, then the
    /// <c>Text</c> or <c>x:Name</c> that identifies the element.
    /// </summary>
    public static TheoryData<string, string> LiveRegions { get; } = new()
    {
        { @"Views\Dialogs\PinDialog.xaml", "{Binding ErrorMessage, NotifyOnTargetUpdated=True}" },
        { @"Views\Dialogs\PinDialog.xaml", "{Binding LockoutMessage, NotifyOnTargetUpdated=True}" },
        { @"Views\Dialogs\PinSetupDialog.xaml", "ErrorText" },
        { @"Views\Dialogs\VaultUnlockDialog.xaml", "{Binding ErrorMessage, NotifyOnTargetUpdated=True}" },
        { @"Views\Dialogs\VaultUnlockDialog.xaml", "{Binding LockoutMessage, NotifyOnTargetUpdated=True}" },
        { @"Views\Dialogs\VaultChangePasswordDialog.xaml", "{Binding ErrorMessage, NotifyOnTargetUpdated=True}" },
        { @"Views\Dialogs\VaultDisableDialog.xaml", "{Binding ErrorMessage, NotifyOnTargetUpdated=True}" },
        { @"Views\Dialogs\VaultEnableDialog.xaml", "{Binding ErrorMessage, NotifyOnTargetUpdated=True}" },
        { "MainWindow.xaml", "{Binding ErrorMessage, NotifyOnTargetUpdated=True}" },
        { "MainWindow.xaml", "{Binding LockoutMessage, NotifyOnTargetUpdated=True}" },
        { @"Views\EmbeddedSftpView.xaml", "{loc:Translate SftpStatusDisconnected}" },
        { @"Views\EmbeddedVncView.xaml", "ReconnectMessageText" },
        { @"Views\SessionPaneControl.xaml", "{Binding Status, Converter={StaticResource SessionStatusToDisplayConverter}, NotifyOnTargetUpdated=True}" },
    };

    [Theory]
    [MemberData(nameof(LiveRegions))]
    public void TheMessageIsALiveRegionThatIsRaised(string file, string identity)
    {
        XElement element = Assert.Single(
            Markup(file).Descendants(),
            e => (string?)e.Attribute("Text") == identity || NameOf(e) == identity);

        Assert.False(string.IsNullOrEmpty(Attribute(element, LiveSetting)), $"{file}: {identity} is not a live region");
        Assert.Equal("True", Attribute(element, Announce));
    }

    [Fact]
    public void AnInlineFieldErrorIsALiveRegionThatIsRaised()
    {
        XElement style = Assert.Single(
            Markup(@"Themes\DialogCommonStyles.xaml").Descendants(),
            e => e.Name.LocalName == "Style" && Key(e) == "FieldValidationErrorStyle");

        Assert.Equal("Assertive", SetterValue(style, LiveSetting));
        Assert.Equal("True", SetterValue(style, "behaviors:" + Announce));
    }

    [Fact]
    public void ThePinSetupErrorIsAnnouncedWhereCodeWritesIt()
    {
        string source = File.ReadAllText(AppPath(@"Views\Dialogs\PinSetupDialog.xaml.cs"));
        string logic = ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), ShowErrorSignature);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, WriteErrorStatement), "ShowError does not write the error");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, AnnounceErrorStatement), "ShowError does not announce what it wrote");
    }

    private const string ShowErrorSignature = "private void ShowError(";
    private const string WriteErrorStatement = "ErrorText.Text = text;";
    private const string AnnounceErrorStatement = "Behaviors.LiveRegionBehavior.Announce(ErrorText);";

    /// <summary>
    /// A splitter with no name reads as an unlabelled "thumb" to a screen reader, and it is a
    /// keyboard stop: arrows resize the panes once it has the focus.
    /// </summary>
    [Fact]
    public void EveryGridSplitterHasAnAccessibleName()
    {
        string appRoot = AppPath(string.Empty);
        List<string> unnamed = [];
        int splitters = 0;

        foreach (string file in SourceFileEnumeration.EnumerateFiles(appRoot, "*.xaml"))
        {
            foreach (XElement splitter in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "GridSplitter"))
            {
                splitters++;
                if (string.IsNullOrWhiteSpace(Attribute(splitter, "AutomationProperties.Name")))
                {
                    unnamed.Add(Path.GetRelativePath(appRoot, file) + ":" + ((IXmlLineInfo)splitter).LineNumber);
                }
            }
        }

        // The scan reached the tool views, three folders down, where two of the six live.
        Assert.True(splitters >= 6, $"only {splitters} splitters found: the scan is not reaching the views");
        Assert.True(unnamed.Count == 0, "Unnamed splitters:" + Environment.NewLine + string.Join(Environment.NewLine, unnamed));
    }

    /// <summary>
    /// A button labelled by another element takes that element's name: the four "apply to all"
    /// buttons of the file conflict dialog all read as "Apply to all".
    /// </summary>
    [Fact]
    public void NoButtonWithItsOwnTextIsLabelledByAnotherElement()
    {
        string appRoot = AppPath(string.Empty);
        List<string> relabelled = [];

        foreach (string file in SourceFileEnumeration.EnumerateFiles(appRoot, "*.xaml"))
        {
            relabelled.AddRange(XDocument.Load(file).Descendants()
                .Where(e => e.Name.LocalName == "Button"
                    && e.Attribute("Content") is not null
                    && e.Attribute("AutomationProperties.LabeledBy") is not null)
                .Select(e => Path.GetRelativePath(appRoot, file) + ":" + ((IXmlLineInfo)e).LineNumber));
        }

        Assert.True(relabelled.Count == 0, "Buttons renamed by LabeledBy:" + Environment.NewLine + string.Join(Environment.NewLine, relabelled));
    }

    [Fact]
    public void TheFileConflictBulkButtonsKeepTheirContextAsHelpText()
    {
        List<XElement> bulk = Markup(@"Views\Dialogs\FileConflictDialog.xaml").Descendants()
            .Where(e => e.Name.LocalName == "Button"
                && ((string?)e.Attribute("Command"))?.Contains("ApplyAll", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Equal(4, bulk.Count);
        Assert.All(bulk, button => Assert.Equal(
            "{Binding Text, ElementName=ApplyToAllLabel}",
            Attribute(button, "AutomationProperties.HelpText")));
    }

    /// <summary>
    /// The workspace lock covers the whole window and is the only thing that can be used while
    /// it is up, so Tab wraps inside it instead of walking the hidden window behind.
    /// </summary>
    [Fact]
    public void TabStaysInsideTheWorkspaceLock()
    {
        XElement overlay = Assert.Single(
            Markup("MainWindow.xaml").Descendants(),
            e => ((string?)e.Attribute("Visibility"))?.Contains("IsWorkspaceLocked", StringComparison.Ordinal) == true);

        Assert.Equal("Cycle", Attribute(overlay, "KeyboardNavigation.TabNavigation"));
    }

    /// <summary>
    /// "Use the global defaults" sits above the RDP fields it governs, yet its TabIndex sent the
    /// focus to it only after the whole RDP section.
    /// </summary>
    [Fact]
    public void TheRdpGlobalDefaultsCheckboxIsReachedBeforeTheFieldsItGoverns()
    {
        List<XElement> stops = Markup(@"Views\Dialogs\ServerDialog.xaml").Descendants()
            .Where(e => e.Attribute("TabIndex") is not null)
            .ToList();
        int at = stops.FindIndex(e => NameOf(e) == "DlgSrv_GlobalDefaultsCb");
        Assert.True(at > 0 && at < stops.Count - 1, "DlgSrv_GlobalDefaultsCb not found between two tab stops");

        int previous = TabIndex(stops[at - 1]);
        int own = TabIndex(stops[at]);
        int next = TabIndex(stops[at + 1]);
        Assert.InRange(own, previous, next);
    }

    [Fact]
    public void AFirewallCellSaysOpenOrClosedByShapeAsWellAsColour()
    {
        string open = FirewallTesterView.StatusSymbol(ProbeStatus.Open);
        string closed = FirewallTesterView.StatusSymbol(ProbeStatus.Closed);
        string timeout = FirewallTesterView.StatusSymbol(ProbeStatus.Timeout);

        Assert.Equal(3, new[] { open, closed, timeout }.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(timeout, FirewallTesterView.StatusSymbol(null));
    }

    [Fact]
    public void ASecNumCloudChapterCountCarriesItsMark()
    {
        string[] marks = [SecNumCloudAuditView.PassSymbol, SecNumCloudAuditView.WarnSymbol, SecNumCloudAuditView.FailSymbol];

        Assert.Equal(3, marks.Distinct(StringComparer.Ordinal).Count());
        Assert.StartsWith(SecNumCloudAuditView.FailSymbol, SecNumCloudAuditView.CountLabel(SecNumCloudAuditView.FailSymbol, 4), StringComparison.Ordinal);
        Assert.EndsWith("4", SecNumCloudAuditView.CountLabel(SecNumCloudAuditView.FailSymbol, 4), StringComparison.Ordinal);
    }

    private static XDocument Markup(string file) => XDocument.Load(AppPath(file), LoadOptions.SetLineInfo);

    private static string AppPath(string file) =>
        Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", file);

    private static string? NameOf(XElement element) =>
        (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"));

    private static string? Key(XElement element) =>
        (string?)element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"));

    private static int TabIndex(XElement element) =>
        int.Parse((string)element.Attribute("TabIndex")!, System.Globalization.CultureInfo.InvariantCulture);

    // An attached property in another XML namespace (behaviors:...) keeps only its local part.
    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static string? SetterValue(XElement style, string property) =>
        style.Elements()
            .Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property)
            .Select(e => (string?)e.Attribute("Value"))
            .SingleOrDefault();
}
