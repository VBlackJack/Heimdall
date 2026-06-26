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
using Heimdall.App.Themes;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Heimdall.App.Tests;

/// <summary>
/// Regression tests for <see cref="DraculaSyntaxPalette"/>: applying the palette to a highlighting
/// definition whose rule-set graph contains a cycle must terminate, not overflow the stack. A cyclic
/// graph previously drove <c>ApplyColorsToRuleSet</c> into infinite recursion and crashed the process
/// when opening the embedded editor on files mapped to nested definitions (e.g. ".conf" -> MarkDown).
/// </summary>
public sealed class DraculaSyntaxPaletteTests
{
    [Fact]
    public void Apply_SelfReferencingRuleSet_DoesNotOverflow()
    {
        var ruleSet = new HighlightingRuleSet { Name = "self" };
        ruleSet.Spans.Add(new HighlightingSpan { RuleSet = ruleSet }); // span loops back to its own set

        var definition = new CyclicDefinition(ruleSet);

        Action act = () => DraculaSyntaxPalette.Apply(definition);

        act.Should().NotThrow();
    }

    [Fact]
    public void Apply_MutuallyReferencingRuleSets_DoesNotOverflow()
    {
        var a = new HighlightingRuleSet { Name = "a" };
        var b = new HighlightingRuleSet { Name = "b" };
        a.Spans.Add(new HighlightingSpan { RuleSet = b });
        b.Spans.Add(new HighlightingSpan { RuleSet = a }); // a -> b -> a cycle

        var definition = new CyclicDefinition(a);

        Action act = () => DraculaSyntaxPalette.Apply(definition);

        act.Should().NotThrow();
    }

    // Minimal IHighlightingDefinition exposing only the members the palette walk touches.
    private sealed class CyclicDefinition(HighlightingRuleSet mainRuleSet) : IHighlightingDefinition
    {
        public string Name => "Cyclic";

        public HighlightingRuleSet MainRuleSet { get; } = mainRuleSet;

        public IEnumerable<HighlightingColor> NamedHighlightingColors => [];

        public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();

        public HighlightingColor? GetNamedColor(string name) => null;

        public HighlightingRuleSet? GetNamedRuleSet(string name) => null;
    }
}
