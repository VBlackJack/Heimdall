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

namespace Heimdall.App.ViewModels;

/// <summary>
/// How a session's manual order ranks it among its siblings.
/// </summary>
/// <remarks>
/// A profile is born with a sort order of zero, and zero used to rank first: a folder the user
/// had arranged by hand saw every session created afterwards land at its top, above the order
/// that had been chosen. Zero now means "no manual order" and ranks after every ordered session,
/// alphabetically among the other unordered ones, so a new session joins the end of an arranged
/// folder and an untouched folder keeps its alphabetical order.
/// </remarks>
public static class SessionOrdering
{
    /// <summary>The sort order a profile carries when nobody has arranged it.</summary>
    public const int Unordered = 0;

    /// <summary>The distance between two consecutive manual orders written by a reorder.</summary>
    /// <remarks>
    /// Renumbering by tens rather than by ones leaves room for a value typed in the dialog to
    /// slot between two arranged neighbours without touching them.
    /// </remarks>
    public const int Step = 10;

    /// <summary>The rank <paramref name="sortOrder"/> sorts by: unordered goes last.</summary>
    public static long RankOf(int sortOrder) =>
        sortOrder == Unordered ? long.MaxValue : sortOrder;

    /// <summary>The manual order written for the session at <paramref name="index"/> of a folder.</summary>
    public static int OrderAt(int index) => (index + 1) * Step;
}
