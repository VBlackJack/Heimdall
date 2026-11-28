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

using System.Text.Json;
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

public sealed class TerminalHighlightRuleTests
{
    [Fact]
    public void ResolveEffectiveRules_EmptyOrNull_ReturnsFourBuiltInDefaults()
    {
        var defaults = TerminalHighlightRuleDefaults.ResolveEffectiveRules(null);
        Assert.Equal(4, defaults.Count);
        Assert.All(defaults, r => Assert.True(TerminalHighlightRuleDefaults.TryValidateRule(r.Pattern, out _)));
    }

    [Fact]
    public void TryValidateRule_RejectsEmptyOversizedAndInvalidRegex()
    {
        Assert.False(TerminalHighlightRuleDefaults.TryValidateRule("", out _));
        Assert.False(TerminalHighlightRuleDefaults.TryValidateRule("[unterminated", out _));
        Assert.False(TerminalHighlightRuleDefaults.TryValidateRule(new string('a', TerminalHighlightRuleDefaults.MaxPatternLength + 1), out _));
        Assert.True(TerminalHighlightRuleDefaults.TryValidateRule(@"\bERROR\b", out _));
    }

    [Fact]
    public void SerializeForTerminalJs_FiltersDisabledAndInvalidRules()
    {
        var rules = new List<TerminalHighlightRuleDto>
        {
            new() { Name = "Custom", Pattern = @"\bTODO\b", Color = "magenta", Enabled = true },
            new() { Name = "Disabled", Pattern = @"\bINFO\b", Color = "green", Enabled = false },
            new() { Name = "Invalid", Pattern = "([bad", Color = "red", Enabled = true }
        };

        var json = TerminalHighlightRuleDefaults.SerializeForTerminalJs(rules);
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal(@"\bTODO\b", arr[0].GetProperty("pattern").GetString());
        Assert.Equal("\x1b[35m", arr[0].GetProperty("ansi").GetString());
    }
}
