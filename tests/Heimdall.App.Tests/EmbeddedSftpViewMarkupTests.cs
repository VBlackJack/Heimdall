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
/// Properties of the SFTP browser's markup that a rendering test would not notice losing: the
/// columns carry their own identity, the path bar stays enabled while a listing runs, no size is a
/// bare number, and the status is announced and shown once.
/// </summary>
public sealed class EmbeddedSftpViewMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Regex BareLayoutNumber = new(
        @"(?<![\w.])(Width|MinWidth|MaxWidth|Height|MinHeight|MaxHeight|FontSize)=""[1-9]",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void TheColumnsCannotBeReorderedAndEachCarriesItsSortKey()
    {
        XElement gridView = Markup().Descendants(Presentation + "GridView").Single();

        Assert.Equal("False", (string?)gridView.Attribute("AllowsColumnReorder"));
        List<string> keys = gridView
            .Elements(Presentation + "GridViewColumn")
            .Select(column => (string?)column.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "SftpColumn.Key") ?? string.Empty)
            .ToList();
        Assert.Equal(["Name", "Size", "Modified", "Permissions", "Owner"], keys);
    }

    [Fact]
    public void TheListSupportsTypeAheadOnTheNameColumn()
    {
        XElement list = ViewSource.NamedElement(Markup(), "FileListView");

        Assert.Equal("Name", (string?)list.Attribute("TextSearch.TextPath"));
        Assert.Equal("True", (string?)list.Attribute("IsTextSearchEnabled"));
    }

    [Fact]
    public void ThePathBarFollowsTheConnectionNotTheListing()
    {
        XElement pathBox = ViewSource.NamedElement(Markup(), "PathTextBox");

        // A control that turns disabled loses the keyboard focus for good: while the listing runs
        // it is IsLoading that flips, so the path bar must not bind to anything derived from it.
        Assert.Equal("{Binding IsToolbarEnabled}", (string?)pathBox.Attribute("IsEnabled"));
        Assert.Null(pathBox.Attribute("IsReadOnly"));
    }

    [Fact]
    public void TheFilterIsDebouncedThroughTheSharedMetric()
    {
        XElement filter = ViewSource.NamedElement(Markup(), "FilterTextBox");

        string text = (string?)filter.Attribute("Text") ?? string.Empty;
        Assert.Contains("Delay={x:Static local:SftpViewMetrics.FilterInputDelayMilliseconds}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoLayoutSizeIsABareNumber()
    {
        string markup = MarkupText();
        // The tokens themselves, declared once as resources, are the only numbers.
        string withoutResources = Regex.Replace(
            markup,
            @"<sys:Double[^>]*>[^<]*</sys:Double>",
            string.Empty,
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        MatchCollection found = BareLayoutNumber.Matches(withoutResources);

        Assert.True(
            found.Count == 0,
            "Bare layout numbers in EmbeddedSftpView.xaml: "
                + string.Join(", ", found.Select(match => match.Value)));
    }

    [Fact]
    public void TheToolbarActionsAreAtLeastAsTallAsAPointerTargetShouldBe()
    {
        foreach (string name in new[] { "DisconnectButton", "ReconnectButton", "BtnUpload", "BtnDownload", "BtnNewFolder", "BtnSudoMode", "BtnFollowSshDirectory" })
        {
            XElement element = ViewSource.NamedElement(Markup(), name);
            Assert.Equal("{StaticResource SftpToolbarButtonMinHeight}", (string?)element.Attribute("MinHeight"));
        }
    }

    [Fact]
    public void TheStatusIsShownInOnePlaceAndAnnouncedToAssistiveTechnology()
    {
        XDocument markup = Markup();
        List<XElement> statusTexts = markup
            .Descendants(Presentation + "TextBlock")
            .Where(text => ((string?)text.Attribute("Text") ?? string.Empty).Contains("{Binding StatusText}", StringComparison.Ordinal))
            .ToList();

        XElement bar = Assert.Single(statusTexts);
        Assert.Equal("StatusBarText", (string?)bar.Attribute(Xaml + "Name"));
        Assert.Equal("Polite", (string?)bar.Attribute("AutomationProperties.LiveSetting"));
        Assert.Contains(
            "Assertive",
            bar.ToString(),
            StringComparison.Ordinal);
        Assert.NotNull(ViewSource.NamedElement(markup, "StatusErrorIcon"));
    }

    [Fact]
    public void TheDisconnectedStateHasAnOverlayWithAReconnectAction()
    {
        XDocument markup = Markup();

        XElement overlay = ViewSource.NamedElement(markup, "DisconnectedOverlay");
        XElement reconnect = ViewSource.NamedElement(markup, "OverlayReconnectButton");

        Assert.Equal("{Binding IsDisconnected, Converter={StaticResource BoolToVisibilityConverter}}", (string?)overlay.Attribute("Visibility"));
        Assert.Equal("OnReconnectClick", (string?)reconnect.Attribute("Click"));
    }

    [Fact]
    public void TheDownloadActionIsOnTheToolbarNextToUpload()
    {
        XDocument markup = Markup();
        XElement upload = ViewSource.NamedElement(markup, "BtnUpload");
        XElement download = ViewSource.NamedElement(markup, "BtnDownload");

        Assert.Same(upload.Parent, download.Parent);
        Assert.Equal("{Binding CanDownloadSelected}", (string?)download.Attribute("IsEnabled"));
    }

    [Fact]
    public void TheDateColumnsAreFormattedByTheRegionalConverterNotAFixedPattern()
    {
        string markup = MarkupText();

        Assert.DoesNotContain("yyyy-MM-dd", markup, StringComparison.Ordinal);
        Assert.Contains("FileDateTimeConverter", markup, StringComparison.Ordinal);
    }

    private static string MarkupPath() => Path.Combine(
        ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedSftpView.xaml");

    private static XDocument Markup() => XDocument.Load(MarkupPath());

    private static string MarkupText() => File.ReadAllText(MarkupPath());
}
