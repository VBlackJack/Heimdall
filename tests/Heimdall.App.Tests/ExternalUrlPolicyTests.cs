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

namespace Heimdall.App.Tests;

/// <summary>
/// The barrier that keeps a scheme the shell would execute out of ShellExecute.
/// </summary>
/// <remarks>
/// <para>It had no oracle. The decision was spelled three times - in
/// <see cref="BrowserLauncher"/>, in the terminal view's open-url handler, and in the diagram
/// editor - and no test constructed any of them: the six test files that name the launcher all
/// use a double that records the URL and decides nothing. A fake standing in front of an
/// implementor cannot test what the implementor decides.</para>
/// <para>Now it is one pure predicate over a string, which is a question that can be asked
/// directly. The discriminating mutant is dropping the scheme test, so that any absolute URI
/// is accepted: the refusal rows below then fail.</para>
/// </remarks>
public sealed class ExternalUrlPolicyTests
{
    /// <summary>
    /// A scheme the shell would act on is refused.
    /// </summary>
    /// <remarks>
    /// <c>file:</c> and the UNC path that parses as one would hand the shell a local
    /// executable; <c>javascript:</c> and <c>vbscript:</c> are script the browser would run;
    /// <c>ms-settings:</c> and <c>ms-msdt:</c> are protocol handlers, the second of which has
    /// been an execution vector. None of these reaches a browser by accident: they arrive from
    /// a release feed, a remote host's terminal output, or an imported diagram.
    /// </remarks>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"\\attacker\share\payload.exe")]
    [InlineData("ftp://example.com/payload")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("mailto:someone@example.com")]
    public void ASchemeTheShellWouldActOn_IsRefused(string url)
    {
        bool launchable = ExternalUrlPolicy.TryResolveLaunchable(url, out string? resolved);

        Assert.False(launchable, $"'{url}' must not reach the shell");
        Assert.Null(resolved);
    }

    /// <summary>
    /// Anything that is not an absolute URL is refused, rather than guessed at.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]
    [InlineData("/releases/latest")]
    [InlineData("://example.com")]
    public void SomethingThatIsNotAnAbsoluteUrl_IsRefused(string? url)
    {
        bool launchable = ExternalUrlPolicy.TryResolveLaunchable(url, out string? resolved);

        Assert.False(launchable);
        Assert.Null(resolved);
    }

    /// <summary>
    /// http and https pass, and come back in the normalized form the shell is given.
    /// </summary>
    [Theory]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("https://example.com/releases/latest", "https://example.com/releases/latest")]
    [InlineData("https://example.com/a?b=c#d", "https://example.com/a?b=c#d")]
    [InlineData("HTTPS://EXAMPLE.COM/Path", "https://example.com/Path")]
    [InlineData("  https://example.com/padded  ", "https://example.com/padded")]
    public void AnHttpUrl_IsLaunchableInItsNormalizedForm(string url, string expected)
    {
        bool launchable = ExternalUrlPolicy.TryResolveLaunchable(url, out string? resolved);

        Assert.True(launchable, $"'{url}' should be launchable");
        Assert.Equal(expected, resolved);
    }

    /// <summary>
    /// The resolved URL is what gets launched, so it is never the caller's raw string.
    /// </summary>
    /// <remarks>
    /// Handing the shell the original text rather than the parsed form would let a candidate
    /// that merely parses as http be launched as something else. The three call sites each
    /// launch the out parameter, never their own input.
    /// </remarks>
    [Fact]
    public void TheResolvedUrl_IsTheParsedForm_NotTheInput()
    {
        const string url = "https://example.com/a b";

        Assert.True(ExternalUrlPolicy.TryResolveLaunchable(url, out string? resolved));
        Assert.NotEqual(url, resolved);
        Assert.Equal("https://example.com/a%20b", resolved);
    }
}
