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
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views.Dialogs;

namespace Heimdall.App.UiTests;

/// <summary>
/// Pins that <see cref="MessageDialog"/> assigns its WPF window title from the caller-supplied
/// title and falls back to the application name when that title is blank, so screen readers and
/// UI automation never see an unnamed dialog window.
/// </summary>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed partial class MessageDialogTitleTests
{
    [StaFact]
    public void NonBlankTitle_SetsWindowTitleAndHeaderText()
    {
        WpfTestHost.Invoke(() =>
        {
            var dialog = new MessageDialog();
            try
            {
                dialog.ApplyTitle("Import recipe");

                Assert.Equal("Import recipe", dialog.Title);
                Assert.Equal("Import recipe", dialog.TitleText.Text);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [StaFact]
    public void TitleWithSurroundingWhitespace_SetsTrimmedWindowTitle()
    {
        WpfTestHost.Invoke(() =>
        {
            var dialog = new MessageDialog();
            try
            {
                dialog.ApplyTitle("  Import recipe  ");

                Assert.Equal("Import recipe", dialog.Title);
                Assert.Equal("  Import recipe  ", dialog.TitleText.Text);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [StaTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankTitle_FallsBackToApplicationNameForWindowTitle(string? title)
    {
        WpfTestHost.Invoke(() =>
        {
            var dialog = new MessageDialog();
            try
            {
                string expectedAppName = WpfTestHost.Translate("AppName");
                Assert.False(string.IsNullOrWhiteSpace(expectedAppName));

                dialog.ApplyTitle(title);

                Assert.Equal(expectedAppName, dialog.Title);
                Assert.Equal(title ?? string.Empty, dialog.TitleText.Text);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void AllFourEntryPoints_ApplyTheWindowTitleThroughTheSharedHelper()
    {
        string path = Path.Combine(
            WpfTestHost.RepoRoot,
            "src",
            "Heimdall.App",
            "Views",
            "Dialogs",
            "MessageDialog.xaml.cs");
        string source = File.ReadAllText(path);

        Assert.DoesNotContain("dialog.TitleText.Text", source, StringComparison.Ordinal);
        Assert.Equal(4, ApplyTitleCallPattern().Matches(source).Count);
    }

    [GeneratedRegex(@"dialog\.ApplyTitle\s*\(\s*title\s*\)\s*;")]
    private static partial Regex ApplyTitleCallPattern();
}
