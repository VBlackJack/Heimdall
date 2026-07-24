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

namespace Heimdall.App.Services;

internal enum WebViewNavigationDecision
{
    Allow,
    Cancel,
    CancelAndOpenExternally
}

/// <summary>
/// Defines the single trusted top-level document for a local WebView2 surface.
/// </summary>
internal sealed class WebViewDocumentPolicy
{
    public WebViewDocumentPolicy(string trustedDocumentUrl)
    {
        if (!Uri.TryCreate(trustedDocumentUrl, UriKind.Absolute, out var trustedDocument)
            || trustedDocument.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(trustedDocument.Host)
            || !string.IsNullOrEmpty(trustedDocument.UserInfo))
        {
            throw new ArgumentException(
                "The trusted WebView document must be an absolute HTTPS URL without user information.",
                nameof(trustedDocumentUrl));
        }

        TrustedDocument = trustedDocument;
    }

    public Uri TrustedDocument { get; }

    public string TrustedOrigin => TrustedDocument.GetLeftPart(UriPartial.Authority);

    public bool IsTrustedOrigin(string? candidate)
    {
        return TryCreateAbsoluteUri(candidate, out var candidateUri)
            && string.IsNullOrEmpty(candidateUri.UserInfo)
            && string.Equals(
                candidateUri.Scheme,
                TrustedDocument.Scheme,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                candidateUri.Host,
                TrustedDocument.Host,
                StringComparison.OrdinalIgnoreCase)
            && candidateUri.Port == TrustedDocument.Port;
    }

    public bool IsTrustedDocument(string? candidate)
    {
        return IsTrustedOrigin(candidate)
            && Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri)
            && string.Equals(
                candidateUri.AbsolutePath,
                TrustedDocument.AbsolutePath,
                StringComparison.Ordinal)
            && string.Equals(
                candidateUri.Query,
                TrustedDocument.Query,
                StringComparison.Ordinal)
            && string.Equals(
                candidateUri.Fragment,
                TrustedDocument.Fragment,
                StringComparison.Ordinal);
    }

    public bool ShouldAcceptMessage(string? source, string? activeDocument)
    {
        return IsTrustedDocument(source) && IsTrustedDocument(activeDocument);
    }

    public bool CanExchangeMessages(bool isReady, string? activeDocument)
    {
        return isReady && IsTrustedDocument(activeDocument);
    }

    public WebViewNavigationDecision GetNavigationDecision(
        string? target,
        bool isUserInitiated)
    {
        if (IsTrustedDocument(target))
        {
            return WebViewNavigationDecision.Allow;
        }

        return isUserInitiated && IsExternalHttpUri(target)
            ? WebViewNavigationDecision.CancelAndOpenExternally
            : WebViewNavigationDecision.Cancel;
    }

    public bool TryOpenExternalHttpLink(
        string? target,
        IBrowserLauncher browserLauncher)
    {
        ArgumentNullException.ThrowIfNull(browserLauncher);

        if (!TryGetExternalHttpUri(target, out var externalUri)
            || IsTrustedOrigin(externalUri.AbsoluteUri))
        {
            return false;
        }

        browserLauncher.Open(externalUri.AbsoluteUri);
        return true;
    }

    private bool IsExternalHttpUri(string? candidate)
    {
        return TryGetExternalHttpUri(candidate, out var externalUri)
            && !IsTrustedOrigin(externalUri.AbsoluteUri);
    }

    private static bool TryGetExternalHttpUri(string? candidate, out Uri externalUri)
    {
        if (TryCreateAbsoluteUri(candidate, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            externalUri = uri;
            return true;
        }

        externalUri = null!;
        return false;
    }

    private static bool TryCreateAbsoluteUri(string? candidate, out Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(candidate)
            && Uri.TryCreate(candidate, UriKind.Absolute, out var absoluteUri))
        {
            uri = absoluteUri;
            return true;
        }

        uri = null!;
        return false;
    }
}
