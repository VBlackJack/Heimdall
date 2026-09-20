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

namespace Heimdall.App.Tests;

/// <summary>
/// Each of the three sites that hands a URL to the shell asks the shared decision first.
/// </summary>
/// <remarks>
/// <para>Read from the source, because none of the three can be reached from a test: two are
/// private methods of a WPF view, and the third ends in
/// <c>Process.Start(UseShellExecute)</c>, which a test must not perform. The predicate itself
/// is covered behaviourally in <see cref="ExternalUrlPolicyTests"/>; what is left, and what
/// these tests hold, is that each call site still goes through it rather than growing its own
/// copy back.</para>
/// <para>Kept in its own file: reading production source taints a whole test file for the
/// repository's own guard, so it does not sit beside the behavioural tests.</para>
/// </remarks>
public sealed class ExternalUrlPolicyWiringTests
{
    /// <summary>The release-notes launcher asks before it launches.</summary>
    [Fact]
    public void TheReleaseLauncher_AsksThePolicy()
    {
        string logic = SourceStatements.Method(
            SourceStatements.Logic("src", "Heimdall.App", "Services", "BrowserLauncher.cs"),
            "public void Open(");

        SourceStatements.AssertStatementChain(
            logic,
            "if (!ExternalUrlPolicy.TryResolveLaunchable(url, out string? launchableUrl))");
    }

    /// <summary>The terminal's open-url message asks before it launches.</summary>
    /// <remarks>
    /// This URL is the least trustworthy of the three: it is whatever the remote host printed
    /// into the terminal.
    /// </remarks>
    [Fact]
    public void TheTerminalsOpenUrlMessage_AsksThePolicy()
    {
        string logic = SourceStatements.Method(
            SourceStatements.ViewLogic(),
            "private void OnWebMessageReceived(");

        // Chained rather than searched for: the call must be a step of the block the open-url
        // branch opens, so folding it behind a dead branch fails the chain instead of passing
        // a presence check.
        SourceStatements.AssertStatementChain(
            logic,
            "if (message.StartsWith(MsgOpenUrl, StringComparison.Ordinal))",
            "if (Services.ExternalUrlPolicy.TryResolveLaunchable(url, out string? launchableUrl))");
    }

    /// <summary>The diagram editor's external links ask before they launch.</summary>
    [Fact]
    public void TheDiagramEditorsExternalLinks_AskThePolicy()
    {
        string logic = SourceStatements.Method(
            SourceStatements.Logic("src", "Heimdall.App", "Views", "Tools", "DiagramEditorView.xaml.cs"),
            "private static void OpenExternalLink(");

        SourceStatements.AssertStatementChain(
            logic,
            "if (!Services.ExternalUrlPolicy.TryResolveLaunchable(href, out string? launchableUrl))");
    }

    /// <summary>
    /// No call site keeps a scheme test of its own.
    /// </summary>
    /// <remarks>
    /// The decision was spelled three times before this change, and a copy growing back is the
    /// failure this guards. Other files legitimately test schemes for their own reasons - a
    /// StoreFront URL, an https-only release client - so this is scoped to the three sites
    /// that launch through the shell.
    /// </remarks>
    [Theory]
    [InlineData("src", "Heimdall.App", "Services", "BrowserLauncher.cs")]
    [InlineData("src", "Heimdall.App", "Views", "Tools", "DiagramEditorView.xaml.cs")]
    public void ALaunchingSite_KeepsNoSchemeTestOfItsOwn(params string[] relativePath)
    {
        string logic = SourceStatements.Logic(relativePath);

        Assert.DoesNotContain("UriSchemeHttp", logic, StringComparison.Ordinal);
    }
}
