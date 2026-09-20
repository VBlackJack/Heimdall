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

using System.Diagnostics.CodeAnalysis;

namespace Heimdall.App.Services;

/// <summary>
/// Decides whether a URL may be handed to the shell, and in what form.
/// </summary>
/// <remarks>
/// <para>This is the barrier that keeps <c>javascript:</c>, <c>file:</c>, <c>ms-settings:</c>
/// and every other scheme out of <see cref="System.Diagnostics.Process"/> with
/// <c>UseShellExecute</c>, where the shell would happily run them. Three call sites reached it
/// through a copy of the same three lines: the release-notes launcher, the terminal's
/// <c>open-url</c> message, and the diagram editor's external links. None of the three was
/// tested, because the two views hold the decision inline and the launcher is only ever
/// reached through a test double that records the URL and decides nothing.</para>
/// <para>It lives here so the decision is made once. A predicate over a string is a pure
/// question, so it is answered here and tested directly, rather than inferred from what a
/// caller happened to do with it.</para>
/// </remarks>
internal static class ExternalUrlPolicy
{
    /// <summary>
    /// Returns true when <paramref name="url"/> is an absolute http or https URL, and hands
    /// back the normalized form to launch.
    /// </summary>
    /// <param name="url">The candidate URL, from a release feed, a terminal message, or a
    /// diagram node. All three are content this application did not write.</param>
    /// <param name="launchableUrl">The absolute, normalized URL to hand to the shell, or
    /// <c>null</c> when the candidate was refused.</param>
    public static bool TryResolveLaunchable(string? url, [NotNullWhen(true)] out string? launchableUrl)
    {
        launchableUrl = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        // Uri lowercases the scheme it parsed, so an ordinal comparison is the whole test.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        launchableUrl = uri.AbsoluteUri;
        return true;
    }
}
