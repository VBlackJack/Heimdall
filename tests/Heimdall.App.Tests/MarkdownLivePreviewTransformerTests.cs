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

public sealed class MarkdownLivePreviewTransformerTests
{
    [Fact]
    public void FindNoteLinkSpans_ForAPlainLink_SpansTheMarkersAndTheName()
    {
        var spans = MarkdownLivePreviewTransformer.FindNoteLinkSpans("See [[Note A]] now");

        var span = Assert.Single(spans);
        Assert.Equal(4, span.Start);
        Assert.Equal(6, span.NameStart);
        Assert.Equal(12, span.NameEnd);
        Assert.Equal(14, span.End);
    }

    [Fact]
    public void FindNoteLinkSpans_ForAnEscapedLink_IncludesTheBackslashesInTheMarkers()
    {
        // \[\[Note A]] : the markers the preview dims are the four opening characters.
        var spans = MarkdownLivePreviewTransformer.FindNoteLinkSpans(@"See \[\[Note A]] now");

        var span = Assert.Single(spans);
        Assert.Equal(4, span.Start);
        Assert.Equal(8, span.NameStart);
        Assert.Equal(14, span.NameEnd);
        Assert.Equal(16, span.End);
    }

    [Fact]
    public void FindNoteLinkSpans_FindsEveryLinkOfALine()
    {
        var spans = MarkdownLivePreviewTransformer.FindNoteLinkSpans(@"[[A]] and \[\[B]]");

        Assert.Equal(2, spans.Count);
        Assert.Equal(2, spans[0].NameStart);
        Assert.Equal(14, spans[1].NameStart);
    }

    [Fact]
    public void FindNoteLinkSpans_WithoutALink_IsEmpty()
    {
        Assert.Empty(MarkdownLivePreviewTransformer.FindNoteLinkSpans("a [single] bracket"));
    }
}
