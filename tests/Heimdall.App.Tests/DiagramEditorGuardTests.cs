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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The decisions the diagram editor's view must keep taking: an explicit save
/// reaches the disk, browser accelerators cannot reload the surface, closing
/// asks about unsaved work, and no tool view spells a file dialog filter in
/// English.
/// </summary>
/// <remarks>
/// <para>Every anchor here is a whole statement of the method body it is read
/// from, carried through <see cref="ViewSource.IsStatementOfTheMethodBody"/>, so
/// a statement folded behind a term that is false by construction is not
/// mistaken for one that stands. What this cannot say is that the site runs: the
/// predicate walks past every conditional early return above it.</para>
/// <para>These come from one audit. Ctrl+S inside the editor wrote nothing
/// because the save was only ever recorded in a field, F5 dropped the diagram
/// because browser accelerators were left on, closing a tab with unsaved work
/// said nothing because <c>CanClose()</c> was never implemented, and three views
/// spelled the same .drawio filter three different ways.</para>
/// <para>The vendored bundle and the iframe host page are guarded separately, in
/// <see cref="DrawioAssetGuardTests"/>: they are web assets, and the statement
/// predicate reads C#.</para>
/// </remarks>
public sealed class DiagramEditorGuardTests
{
    private const string ViewFile = "Views/Tools/DiagramEditorView.xaml.cs";

    private const string SettingsMember = "private static void ConfigureWebViewSettings(CoreWebView2 core)";
    private const string SaveRequestMember = "private void HandleSaveRequest(string xml)";
    private const string CanCloseMember = "public bool CanClose()";
    private const string OpenDocumentMember = "private void OpenDocument(string? path = null)";

    private const string AcceleratorKeysStatement = "core.Settings.AreBrowserAcceleratorKeysEnabled = false;";
    private const string ScriptDialogsStatement = "core.Settings.AreDefaultScriptDialogsEnabled = false;";
    private const string SaveStatement = "PerformSave(chooseNewLocation: false);";
    private const string ConfirmStatement = "bool allowed = ConfirmDiscardUnsavedChanges();";
    private const string DraftRecoveryStatement = "OfferDraftRecovery();";

    /// <summary>
    /// A string literal assigned to <c>Filter</c>. The view source reader blanks every literal,
    /// quotes included, so the literal is the run of blanks that follows the equals sign and is
    /// itself followed by something that cannot start an operand. A real operand (an identifier, a
    /// call, a bracket) starts right after the blanks and does not match.
    /// </summary>
    private static readonly Regex LiteralFilterAssignment = new(
        @"\bFilter\s*=(?>\s+)(?![\w@$!(\[{<~-])", RegexOptions.CultureInvariant);

    /// <summary>
    /// F5 and Ctrl+R reload the host page and drop whatever the editor holds.
    /// </summary>
    [Fact]
    public void WebViewSettings_TurnOffBrowserAcceleratorKeys()
    {
        string logic = Logic(ViewFile, SettingsMember);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, AcceleratorKeysStatement),
            "browser accelerator keys are not turned off as a step of the WebView2 configuration, "
                + "so F5 reloads the editor and the diagram is gone");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, ScriptDialogsStatement),
            "default script dialogs are not turned off as a step of the WebView2 configuration");
    }

    /// <summary>
    /// The editor's explicit save must reach the disk. Recording it in a field
    /// is what made Ctrl+S a no-op for the entire life of the tool.
    /// </summary>
    [Fact]
    public void ExplicitSaveRequest_ReachesTheDisk()
    {
        string logic = Logic(ViewFile, SaveRequestMember);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, SaveStatement),
            "handling the editor's save request does not perform a save, so Ctrl+S writes nothing");
    }

    /// <summary>
    /// The shell honours <c>CanClose()</c>. Returning true unconditionally is
    /// how a tab full of unsaved work closed without a word.
    /// </summary>
    [Fact]
    public void ClosingTheTool_AsksAboutUnsavedWork()
    {
        string logic = Logic(ViewFile, CanCloseMember);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, ConfirmStatement),
            "CanClose() does not go through the unsaved-changes prompt, so closing the tool "
                + "throws the diagram away in silence");
    }

    /// <summary>
    /// A draft left by a run that did not finish must be offered where the user
    /// meets the document again: on opening it.
    /// </summary>
    /// <remarks>
    /// Found by a manual pass. The recovery was offered only from
    /// <c>Initialize</c>, for a document handed over in the tool context. The tool
    /// opens empty and the file arrives through Open or Recent, so the prompt
    /// never fired: the draft sat on disk while the user reopened the saved
    /// version and saw nothing.
    /// </remarks>
    [Fact]
    public void OpeningADocument_OffersTheDraftLeftBehind()
    {
        string logic = Logic(ViewFile, OpenDocumentMember);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, DraftRecoveryStatement),
            "opening a document does not offer the draft a previous run left for it, so work "
                + "kept on disk is never given back");
    }

    /// <summary>
    /// File dialog filters are user-facing text: they belong in the locale
    /// catalogue, not in three different English spellings across three views.
    /// </summary>
    [Fact]
    public void ToolViews_DeclareNoHardcodedFileDialogFilter()
    {
        string toolsDirectory = Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Tools");

        Assert.True(Directory.Exists(toolsDirectory),
            $"Tool views directory not found: {toolsDirectory}");

        foreach (string file in Directory.EnumerateFiles(toolsDirectory, "*.cs", SearchOption.TopDirectoryOnly))
        {
            // File dialog filters must come from the locale catalogue.
            string code = ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(file));
            Assert.DoesNotMatch(LiteralFilterAssignment, code);
        }
    }

    private static string Logic(string relativePath, string signature)
    {
        return ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(ReadAppSource(relativePath)), signature);
    }

    private static string ReadAppSource(string relativePath)
    {
        string full = Path.Combine(
            ViewSource.RepoRoot(),
            "src",
            "Heimdall.App",
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }
}
