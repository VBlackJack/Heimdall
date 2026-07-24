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

using Heimdall.App.Services;
using Heimdall.App.Views;
using Heimdall.App.Views.Tools;

namespace Heimdall.App.Tests;

public sealed class MilkdownEditorSecurityTests
{
    [Theory]
    [InlineData("about:blank")]
    [InlineData("data:text/html,<h1>untrusted</h1>")]
    [InlineData("https://attacker.example/?appassets.local")]
    [InlineData("https://appassets.local.attacker.example/index.html")]
    [InlineData("https://appassets.local/other.html")]
    [InlineData("https://appassets.local/index.html?alternate=true")]
    [InlineData("https://appassets.local/index.html#alternate")]
    public void MilkdownEditor_NavigationToUntrustedOrigin_IsCancelled(string target)
    {
        var decision = MilkdownEditorControl.GetNavigationDecision(
            target,
            isUserInitiated: false);

        Assert.Equal(WebViewNavigationDecision.Cancel, decision);
    }

    [Fact]
    public void MilkdownEditor_NavigationToExactTrustedPage_IsAllowed()
    {
        var decision = MilkdownEditorControl.GetNavigationDecision(
            MilkdownEditorControl.TrustedEditorDocument,
            isUserInitiated: false);

        Assert.Equal("https://appassets.local", MilkdownEditorControl.TrustedEditorOrigin);
        Assert.Equal(WebViewNavigationDecision.Allow, decision);
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("data:text/html,<script>chrome.webview.postMessage({type:'change'})</script>")]
    [InlineData("https://attacker.example/?appassets.local")]
    [InlineData("https://appassets.local.attacker.example/index.html")]
    [InlineData("https://appassets.local/other.html")]
    public void MilkdownEditor_WebMessageFromUntrustedSource_IsIgnored(string source)
    {
        var accepted = MilkdownEditorControl.ShouldAcceptWebMessage(
            source,
            MilkdownEditorControl.TrustedEditorDocument);

        Assert.False(accepted);
    }

    [Fact]
    public void MilkdownEditor_WebMessageFromTrustedDocument_IsAccepted()
    {
        var accepted = MilkdownEditorControl.ShouldAcceptWebMessage(
            MilkdownEditorControl.TrustedEditorDocument,
            MilkdownEditorControl.TrustedEditorDocument);

        Assert.True(accepted);
    }

    [Theory]
    [InlineData(false, "https://appassets.local/index.html", false)]
    [InlineData(true, "https://attacker.example/?appassets.local", false)]
    [InlineData(true, "https://appassets.local/index.html", true)]
    public void MilkdownEditor_ContentPostingRequiresReadyTrustedDocument(
        bool isReady,
        string activeDocument,
        bool expected)
    {
        var policy = new WebViewDocumentPolicy(
            MilkdownEditorControl.TrustedEditorDocument);

        var canPost = policy.CanExchangeMessages(isReady, activeDocument);

        Assert.Equal(expected, canPost);
    }

    [Fact]
    public void MilkdownEditor_ExternalLink_OpensInSystemBrowser_NotInWebView()
    {
        var browserLauncher = new RecordingBrowserLauncher();
        const string externalUrl = "https://example.com/docs";

        var decision = MilkdownEditorControl.GetNavigationDecision(
            externalUrl,
            isUserInitiated: true);
        var opened = MilkdownEditorControl.TryOpenExternalLink(
            externalUrl,
            browserLauncher);

        Assert.Equal(WebViewNavigationDecision.CancelAndOpenExternally, decision);
        Assert.True(opened);
        Assert.Equal(externalUrl, browserLauncher.LastOpenedUrl);
    }

    [Fact]
    public void MilkdownEditor_SameOriginNonEditorPage_IsCancelledWithoutOpeningBrowser()
    {
        var browserLauncher = new RecordingBrowserLauncher();
        const string sameOriginUrl = "https://appassets.local/other.html";

        var decision = MilkdownEditorControl.GetNavigationDecision(
            sameOriginUrl,
            isUserInitiated: true);
        var opened = MilkdownEditorControl.TryOpenExternalLink(
            sameOriginUrl,
            browserLauncher);

        Assert.Equal(WebViewNavigationDecision.Cancel, decision);
        Assert.False(opened);
        Assert.Null(browserLauncher.LastOpenedUrl);
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("data:text/html,<h1>untrusted</h1>")]
    [InlineData("https://attacker.example/?heimdall-vnc.local")]
    [InlineData("https://heimdall-vnc.local.attacker.example/vnc.html")]
    [InlineData("https://heimdall-vnc.local/other.html")]
    public void EmbeddedVncView_UntrustedOriginMessageRejected(string source)
    {
        var accepted = EmbeddedVncView.ShouldAcceptWebMessage(
            source,
            "https://heimdall-vnc.local/vnc.html");

        Assert.False(accepted);
    }

    [Fact]
    public void EmbeddedVncView_TrustedDocumentMessageAccepted()
    {
        const string trustedDocument = "https://heimdall-vnc.local/vnc.html";

        var accepted = EmbeddedVncView.ShouldAcceptWebMessage(
            trustedDocument,
            trustedDocument);

        Assert.True(accepted);
    }

    private sealed class RecordingBrowserLauncher : IBrowserLauncher
    {
        public string? LastOpenedUrl { get; private set; }

        public void Open(string url)
        {
            LastOpenedUrl = url;
        }
    }
}
