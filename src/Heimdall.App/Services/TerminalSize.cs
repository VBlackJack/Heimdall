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

/// <summary>
/// A terminal grid size in character cells, as reported by the embedded terminal page.
/// </summary>
/// <remarks>
/// A reference type on purpose: the view records the last size it heard on the UI thread and the
/// SSH handler reads it from the connect thread just before it creates the PTY. A reference swap
/// is atomic; a struct of two integers is not.
/// </remarks>
internal sealed record TerminalSize
{
    /// <summary>The width a PTY gets when the terminal has not reported one yet.</summary>
    public const int DefaultColumns = 80;

    /// <summary>The height a PTY gets when the terminal has not reported one yet.</summary>
    public const int DefaultRows = 24;

    /// <summary>The size used when the terminal has not reported one yet.</summary>
    public static TerminalSize Default { get; } = new(DefaultColumns, DefaultRows);

    /// <summary>Creates a size; both dimensions must be positive.</summary>
    public TerminalSize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);

        Columns = columns;
        Rows = rows;
    }

    /// <summary>Width in character cells.</summary>
    public int Columns { get; }

    /// <summary>Height in character cells.</summary>
    public int Rows { get; }
}
