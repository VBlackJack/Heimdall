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

namespace Heimdall.App.ViewModels.Shell;

/// <summary>
/// The recently used tools, most recent first, bounded.
/// </summary>
/// <remarks>
/// Identity is <see cref="StringComparison.OrdinalIgnoreCase"/> because that is how
/// <c>ToolRegistry</c> defines it: both of its indexes are built with
/// <see cref="StringComparer.OrdinalIgnoreCase"/>, so two spellings that differ only in case are
/// the same tool everywhere else in the shell.
/// <para>
/// The list previously deduplicated with <c>List&lt;string&gt;.Remove</c>, which is ordinal and
/// case-SENSITIVE, while its seven call sites disagreed about who upper-cases: four normalise the
/// id, three pass <c>descriptor.Id</c> through untouched. That is invisible for built-in tools,
/// whose ids are upper-case literals, but external ones are registered as
/// <c>EXT:{PROVIDER}:{tool.Id}</c> with the trailing segment left exactly as the provider spelled
/// it. So one external tool could hold two of the five slots and appear twice in the command
/// palette's recent section, depending on which surface had launched it. Matching the registry's
/// own notion of identity is what removes that.
/// </para>
/// </remarks>
public sealed class RecentToolList
{
    /// <summary>How many tools are remembered.</summary>
    public const int MaxEntries = 5;

    private readonly List<string> _ids = [];

    /// <summary>The remembered tool identifiers, most recent first.</summary>
    public IReadOnlyList<string> Ids => _ids;

    /// <summary>
    /// Records a tool as the most recently used one, evicting the oldest past the cap.
    /// </summary>
    /// <remarks>
    /// Re-recording a tool moves it to the front without growing the list, and the spelling kept
    /// is the one just used.
    /// </remarks>
    public void Track(string toolId)
    {
        _ids.RemoveAll(id => string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase));
        _ids.Insert(0, toolId);

        while (_ids.Count > MaxEntries)
        {
            _ids.RemoveAt(_ids.Count - 1);
        }
    }
}
