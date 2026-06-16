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

using FluentAssertions;
using Heimdall.App.Behaviors;

namespace Heimdall.App.Tests;

/// <summary>
/// Producer-first tests for the pure <see cref="HighlightTextBehavior.HighlightSplit"/>
/// helper that backs the palette result highlight behavior.
/// </summary>
public sealed class HighlightTextBehaviorTests
{
    [Fact]
    public void HighlightSplit_EmptyQuery_ReturnsWholeSourceAsBefore()
    {
        var (before, match, after) = HighlightTextBehavior.HighlightSplit("Server-01", "");

        before.Should().Be("Server-01");
        match.Should().BeEmpty();
        after.Should().BeEmpty();
    }

    [Fact]
    public void HighlightSplit_NoMatch_ReturnsWholeSourceAsBefore()
    {
        var (before, match, after) = HighlightTextBehavior.HighlightSplit("Server-01", "xyz");

        before.Should().Be("Server-01");
        match.Should().BeEmpty();
        after.Should().BeEmpty();
    }

    [Fact]
    public void HighlightSplit_MatchAtStart()
    {
        var (before, match, after) = HighlightTextBehavior.HighlightSplit("Server-01", "Ser");

        before.Should().BeEmpty();
        match.Should().Be("Ser");
        after.Should().Be("ver-01");
    }

    [Fact]
    public void HighlightSplit_MatchInMiddle()
    {
        var (before, match, after) = HighlightTextBehavior.HighlightSplit("Server-01", "ver");

        before.Should().Be("Ser");
        match.Should().Be("ver");
        after.Should().Be("-01");
    }

    [Fact]
    public void HighlightSplit_MatchAtEnd()
    {
        var (before, match, after) = HighlightTextBehavior.HighlightSplit("Server-01", "-01");

        before.Should().Be("Server");
        match.Should().Be("-01");
        after.Should().BeEmpty();
    }

    [Fact]
    public void HighlightSplit_IsCaseInsensitive_PreservesSourceCasing()
    {
        var (before, match, after) = HighlightTextBehavior.HighlightSplit("Server-01", "SERVER");

        before.Should().BeEmpty();
        match.Should().Be("Server");
        after.Should().Be("-01");
    }

    [Fact]
    public void HighlightSplit_HighlightsFirstOccurrenceOnly()
    {
        var (before, match, after) = HighlightTextBehavior.HighlightSplit("aXaXa", "X");

        before.Should().Be("a");
        match.Should().Be("X");
        after.Should().Be("aXa");
    }

    [Fact]
    public void HighlightSplit_NullSource_ReturnsEmpty()
    {
        var (before, match, after) = HighlightTextBehavior.HighlightSplit(null, "x");

        before.Should().BeEmpty();
        match.Should().BeEmpty();
        after.Should().BeEmpty();
    }
}
