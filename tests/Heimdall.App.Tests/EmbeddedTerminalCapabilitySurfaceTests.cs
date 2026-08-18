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

namespace Heimdall.App.Tests;

public sealed class EmbeddedTerminalCapabilitySurfaceTests
{
    [Fact]
    public void SftpContextMenu_ChmodRequiresSelectionAndSftpBrowser()
    {
        string source = ReadRepoFile(
            "src",
            "Heimdall.App",
            "Views",
            "EmbeddedSftpView.xaml.cs");
        string method = ExtractMethodBody(
            source,
            "private void OnContextMenuOpened(object sender, RoutedEventArgs e)");

        Assert.Contains(
            "CtxChmod.Visibility = hasSelection && _browser is SftpBrowser",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SftpSymlinkRenameRefusal_UsesRawBrowserFactInMenuAndViewModelGuard()
    {
        string viewSource = ReadRepoFile(
            "src",
            "Heimdall.App",
            "Views",
            "EmbeddedSftpView.xaml.cs");
        string initialization = ExtractMethodBody(
            viewSource,
            "public void InitializeSession(");
        string contextMenu = ExtractMethodBody(
            viewSource,
            "private void OnContextMenuOpened(object sender, RoutedEventArgs e)");
        string viewModelSource = ReadRepoFile(
            "src",
            "Heimdall.App",
            "ViewModels",
            "EmbeddedSftpViewModel.cs");
        string rename = ExtractMethodBody(
            viewModelSource,
            "public async Task RenameEntryAsync(SftpFileInfo file)");

        Assert.Contains(
            "_viewModel.RenameFollowsSymlinkTarget = browser is SftpBrowser;",
            initialization,
            StringComparison.Ordinal);
        Assert.Contains("CtxRename.Visibility = singleSelection", contextMenu, StringComparison.Ordinal);
        Assert.Contains("_browser is SftpBrowser", contextMenu, StringComparison.Ordinal);
        Assert.Contains(
            "Kind: RemoteEntryKind.SymbolicLink",
            contextMenu,
            StringComparison.Ordinal);
        Assert.Contains(
            "RenameFollowsSymlinkTarget && file.Kind == RemoteEntryKind.SymbolicLink",
            rename,
            StringComparison.Ordinal);
        Assert.Contains("SftpStatusRenameUnsupportedEntry", rename, StringComparison.Ordinal);
    }

    [Fact]
    public void SftpEntryKindSurface_DefinesSpecialKindIconsAndLocalizedTooltipType()
    {
        string source = ReadRepoFile(
            "src",
            "Heimdall.App",
            "Views",
            "EmbeddedSftpView.xaml");

        // An entry nobody could classify must be visibly distinct. Asserting only that the trigger exists
        // would pass even if it set the ordinary file appearance, so the block is extracted and read on
        // its own: a global search would find the default glyph elsewhere in the template and prove
        // nothing about this trigger.
        string unknownTrigger = ExtractDataTriggerBlock(source, "Unknown");

        Assert.Contains("&#xE7BA;", unknownTrigger, StringComparison.Ordinal);
        Assert.Contains("WarningBrush", unknownTrigger, StringComparison.Ordinal);

        // The plain-file appearance must not be what this trigger applies.
        Assert.DoesNotContain("&#xE7C3;", unknownTrigger, StringComparison.Ordinal);
        Assert.DoesNotContain("TextSecondaryBrush", unknownTrigger, StringComparison.Ordinal);
        Assert.Contains(
            "<DataTrigger Binding=\"{Binding Kind}\" Value=\"SymbolicLink\">",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "<DataTrigger Binding=\"{Binding Kind}\" Value=\"Fifo\">",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "<DataTrigger Binding=\"{Binding Kind}\" Value=\"Socket\">",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "<DataTrigger Binding=\"{Binding Kind}\" Value=\"Device\">",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Run Text=\"{loc:Translate SftpPropertiesType}\"/>",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Run Text=\"{Binding Kind, Converter={StaticResource RemoteEntryKindToDisplayNameConverter}}\"/>",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SshHealthButton_DefaultsHiddenUntilDirectSessionIsAttached()
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "src",
            "Heimdall.App",
            "Views",
            "EmbeddedSshView.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement? button = document
            .Descendants()
            .SingleOrDefault(element =>
                element.Name.LocalName == "Button"
                && string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "HealthToggleButton",
                    StringComparison.Ordinal));

        Assert.NotNull(button);
        Assert.Equal("Collapsed", (string?)button!.Attribute("Visibility"));
    }

    [Fact]
    public void SshHealthButton_TracksMaterializedSessionType()
    {
        string source = ReadRepoFile(
            "src",
            "Heimdall.App",
            "Views",
            "EmbeddedSshView.xaml.cs");
        string directSession = ExtractMethodBody(
            source,
            "public void AttachSession(");
        string terminalSession = ExtractMethodBody(
            source,
            "public void AttachTerminalSession(");

        Assert.Contains(
            "ShowHealthButton(true);",
            directSession,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowHealthButton(false);",
            terminalSession,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ElevateButton_RequiresAnActiveSubscriber()
    {
        string source = ReadRepoFile(
            "src",
            "Heimdall.App",
            "Views",
            "EmbeddedSshView.xaml.cs");
        string method = ExtractExpressionMember(
            source,
            "public void ShowElevateButton(bool visible)");

        Assert.Contains("ElevateRequested is not null", method, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine([FindRepoRoot(), .. relativeParts]));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Heimdall repository root.");
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Method signature was not found: {signature}");

        int openingBraceIndex = source.IndexOf('{', signatureIndex + signature.Length);
        Assert.True(openingBraceIndex >= 0, $"Opening brace was not found for: {signature}");

        var depth = 0;
        for (int index = openingBraceIndex; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[(openingBraceIndex + 1)..index];
                    }

                    break;
            }
        }

        throw new InvalidDataException($"Closing brace was not found for: {signature}");
    }

    private static string ExtractExpressionMember(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Member signature was not found: {signature}");

        int endIndex = source.IndexOf(';', signatureIndex + signature.Length);
        Assert.True(endIndex > signatureIndex, $"Member terminator was not found: {signature}");
        return source[signatureIndex..(endIndex + 1)];
    }

    /// <summary>
    /// Returns the single <c>DataTrigger</c> block for the given <c>Kind</c> value, and nothing else.
    /// </summary>
    /// <remarks>
    /// Bounding is the whole point: assertions about an appearance are meaningless if they can be
    /// satisfied by markup belonging to another trigger, or by the template's default icon.
    /// </remarks>
    private static string ExtractDataTriggerBlock(string xaml, string kindValue)
    {
        string opening = $"<DataTrigger Binding=\"{{Binding Kind}}\" Value=\"{kindValue}\">";
        int start = xaml.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the {kindValue} trigger must exist");
        Assert.Equal(1, CountOccurrences(xaml, opening));

        int end = xaml.IndexOf("</DataTrigger>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"the {kindValue} trigger must be closed");

        return xaml[start..(end + "</DataTrigger>".Length)];
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
