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

namespace Heimdall.Core.Models;

/// <summary>
/// A recorded sequence of terminal inputs that can be replayed against an SSH session.
/// </summary>
public sealed class TerminalMacro
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<MacroEntry> Entries { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ExpectTimeoutAction
{
    Abort,
    Continue
}

/// <summary>
/// A single input entry within a terminal macro, capturing the text sent
/// and the delay since the previous entry.
/// </summary>
public sealed class MacroEntry
{
    public const int DefaultExpectTimeoutMs = 30_000;
    public const int MinExpectTimeoutMs = 100;
    public const int MaxExpectTimeoutMs = 600_000;

    public string Input { get; set; } = "";
    public int DelayMs { get; set; }
    public string? ExpectPattern { get; set; }
    public int? ExpectTimeoutMs { get; set; }
    public bool ExpectIsRegex { get; set; }
    public ExpectTimeoutAction ExpectOnTimeout { get; set; } = ExpectTimeoutAction.Abort;

    public int GetEffectiveExpectTimeoutMs()
    {
        return Math.Clamp(
            ExpectTimeoutMs ?? DefaultExpectTimeoutMs,
            MinExpectTimeoutMs,
            MaxExpectTimeoutMs);
    }
}
