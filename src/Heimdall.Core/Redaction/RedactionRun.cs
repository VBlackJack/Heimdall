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

using System.Collections.ObjectModel;
using System.Globalization;

namespace Heimdall.Core.Redaction;

/// <summary>Mutable state of one <see cref="TextRedactor"/> call: counts and pseudonym tables.</summary>
internal sealed class RedactionRun
{
    internal const string IpPseudonymPrefix = "ip-";

    internal const string HostPseudonymPrefix = "host-";

    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ipPseudonyms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hostPseudonyms = new(StringComparer.OrdinalIgnoreCase);

    internal RedactionRun(IEnumerable<RedactionRule> rules)
    {
        foreach (RedactionRule rule in rules)
        {
            _counts[rule.Id] = 0;
        }
    }

    internal void Count(string ruleId) => _counts[ruleId] = _counts.GetValueOrDefault(ruleId) + 1;

    internal string IpPseudonym(string address) => Pseudonym(_ipPseudonyms, address, IpPseudonymPrefix);

    internal string HostPseudonym(string host) => Pseudonym(_hostPseudonyms, host, HostPseudonymPrefix);

    internal IReadOnlyDictionary<string, int> SnapshotCounts() =>
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(_counts, StringComparer.Ordinal));

    internal IReadOnlyDictionary<string, int> ZeroCounts()
    {
        var zeros = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string id in _counts.Keys)
        {
            zeros[id] = 0;
        }

        return new ReadOnlyDictionary<string, int>(zeros);
    }

    private static string Pseudonym(Dictionary<string, string> table, string value, string prefix)
    {
        if (!table.TryGetValue(value, out string? pseudonym))
        {
            pseudonym = prefix + (table.Count + 1).ToString(CultureInfo.InvariantCulture);
            table[value] = pseudonym;
        }

        return pseudonym;
    }
}
