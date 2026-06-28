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
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

public sealed class TerminalMacroExpectSerializationTests
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Deserialize_LegacyMacroWithoutExpectFields_UsesNoExpectDefaults()
    {
        const string json = """
            {
              "id": "legacy",
              "name": "Legacy macro",
              "entries": [
                {
                  "input": "pwd",
                  "delayMs": 25
                }
              ]
            }
            """;

        var macro = JsonSerializer.Deserialize<TerminalMacro>(json, ReadOptions);

        Assert.NotNull(macro);
        var entry = Assert.Single(macro.Entries);
        Assert.Equal("pwd", entry.Input);
        Assert.Equal(25, entry.DelayMs);
        Assert.Null(entry.ExpectPattern);
        Assert.Null(entry.ExpectTimeoutMs);
        Assert.False(entry.ExpectIsRegex);
        Assert.Equal(ExpectTimeoutAction.Abort, entry.ExpectOnTimeout);
    }

    [Fact]
    public void SerializeDeserialize_MacroWithExpectFields_RoundTrips()
    {
        var macro = new TerminalMacro
        {
            Id = "expect",
            Name = "Expect macro",
            Entries =
            [
                new MacroEntry
                {
                    Input = "show status",
                    DelayMs = 50,
                    ExpectPattern = @"ready\s*>",
                    ExpectTimeoutMs = 1_250,
                    ExpectIsRegex = true,
                    ExpectOnTimeout = ExpectTimeoutAction.Continue
                }
            ]
        };

        var json = JsonSerializer.Serialize(macro, WriteOptions);
        var reloaded = JsonSerializer.Deserialize<TerminalMacro>(json, ReadOptions);

        Assert.NotNull(reloaded);
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal("show status", entry.Input);
        Assert.Equal(50, entry.DelayMs);
        Assert.Equal(@"ready\s*>", entry.ExpectPattern);
        Assert.Equal(1_250, entry.ExpectTimeoutMs);
        Assert.True(entry.ExpectIsRegex);
        Assert.Equal(ExpectTimeoutAction.Continue, entry.ExpectOnTimeout);
    }

    [Fact]
    public void GetEffectiveExpectTimeoutMs_UsesDefaultAndClampsBounds()
    {
        var defaultEntry = new MacroEntry { ExpectPattern = "ready" };
        var lowEntry = new MacroEntry { ExpectPattern = "ready", ExpectTimeoutMs = -1 };
        var highEntry = new MacroEntry { ExpectPattern = "ready", ExpectTimeoutMs = int.MaxValue };

        Assert.Equal(MacroEntry.DefaultExpectTimeoutMs, defaultEntry.GetEffectiveExpectTimeoutMs());
        Assert.Equal(MacroEntry.MinExpectTimeoutMs, lowEntry.GetEffectiveExpectTimeoutMs());
        Assert.Equal(MacroEntry.MaxExpectTimeoutMs, highEntry.GetEffectiveExpectTimeoutMs());
    }
}
