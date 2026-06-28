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

using System.Text;
using Heimdall.Terminal.Macros;

namespace Heimdall.Terminal.Tests;

public sealed class ExpectMatcherTests
{
    [Fact]
    public void TryMatch_SubstringAcrossChunkBoundaries_ReturnsTrue()
    {
        var matcher = new ExpectMatcher();

        matcher.Feed(Utf8("pro"));
        matcher.Feed(Utf8("mpt>"));

        Assert.True(matcher.TryMatch("prompt>", isRegex: false));
    }

    [Fact]
    public void TryMatch_StripsAnsiSequencesBeforeMatching()
    {
        var matcher = new ExpectMatcher();

        matcher.Feed(Utf8("state: \u001b[32mready\u001b[0m"));

        Assert.True(matcher.TryMatch("state: ready", isRegex: false));
    }

    [Fact]
    public void TryMatch_RegexPattern_ReturnsTrue()
    {
        var matcher = new ExpectMatcher();

        matcher.Feed(Utf8("exit code 42"));

        Assert.True(matcher.TryMatch(@"code\s+\d+", isRegex: true));
    }

    [Fact]
    public void TryMatch_RegexTimeout_ReturnsFalseWithoutThrowing()
    {
        var matcher = new ExpectMatcher(regexTimeout: TimeSpan.FromMilliseconds(1));
        matcher.Feed(Utf8(new string('a', 30_000) + "!"));

        var matched = true;
        var exception = Record.Exception(() => matched = matcher.TryMatch("^(a+)+$", isRegex: true));

        Assert.Null(exception);
        Assert.False(matched);
    }

    [Fact]
    public void TryMatch_BoundedBufferTrimsOldestTextAndMatchesRecentText()
    {
        var matcher = new ExpectMatcher(bufferCapacity: 12);

        matcher.Feed(Utf8("old-pattern-"));
        matcher.Feed(Utf8("recent"));

        Assert.False(matcher.TryMatch("old-pattern", isRegex: false));
        Assert.True(matcher.TryMatch("recent", isRegex: false));
    }

    [Fact]
    public void TryMatch_NoMatch_ReturnsFalse()
    {
        var matcher = new ExpectMatcher();

        matcher.Feed(Utf8("server output"));

        Assert.False(matcher.TryMatch("missing", isRegex: false));
    }

    private static byte[] Utf8(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }
}
