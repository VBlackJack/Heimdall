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

using System.Globalization;

namespace Heimdall.Core.Updates;

/// <summary>
/// Immutable Heimdall version of the canonical "YYYY.MMDDxx" form. Equality and
/// ordering are computed numerically from the integer components, while the
/// original cleaned string is preserved so <see cref="ToString"/> round-trips the
/// zero-padded representation exactly (e.g. "2026.061501" stays "2026.061501").
/// </summary>
public readonly struct HeimdallVersion : IComparable<HeimdallVersion>, IEquatable<HeimdallVersion>
{
    private readonly int[]? _components;
    private readonly string? _cleaned;

    private HeimdallVersion(int[] components, string cleaned)
    {
        _components = components;
        _cleaned = cleaned;
    }

    /// <summary>
    /// Parses a version string. Accepts an optional leading 'v', and strips any
    /// build-metadata suffix starting at the first '+'. Every dot-separated
    /// segment must be a non-negative integer.
    /// </summary>
    public static bool TryParse(string? input, out HeimdallVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        var plusIndex = text.IndexOf('+');
        if (plusIndex >= 0)
        {
            text = text[..plusIndex];
        }

        if (text.Length == 0)
        {
            return false;
        }

        var segments = text.Split('.');
        var components = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            components[i] = value;
        }

        version = new HeimdallVersion(components, text);
        return true;
    }

    /// <summary>
    /// Parses a version string, throwing <see cref="FormatException"/> on malformed input.
    /// </summary>
    public static HeimdallVersion Parse(string input)
    {
        if (!TryParse(input, out var version))
        {
            throw new FormatException($"Invalid Heimdall version: '{input}'.");
        }

        return version;
    }

    public int CompareTo(HeimdallVersion other)
    {
        var left = _components ?? [];
        var right = other._components ?? [];
        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var x = i < left.Length ? left[i] : 0;
            var y = i < right.Length ? right[i] : 0;
            var comparison = x.CompareTo(y);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    public bool Equals(HeimdallVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is HeimdallVersion other && Equals(other);

    public override int GetHashCode()
    {
        var components = _components ?? [];

        // Trailing zeros do not affect numeric equality, so they must not affect
        // the hash code either (keeps GetHashCode consistent with Equals).
        var length = components.Length;
        while (length > 0 && components[length - 1] == 0)
        {
            length--;
        }

        var hash = new HashCode();
        for (var i = 0; i < length; i++)
        {
            hash.Add(components[i]);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => _cleaned ?? string.Empty;

    public static bool operator ==(HeimdallVersion left, HeimdallVersion right) => left.Equals(right);

    public static bool operator !=(HeimdallVersion left, HeimdallVersion right) => !left.Equals(right);

    public static bool operator <(HeimdallVersion left, HeimdallVersion right) => left.CompareTo(right) < 0;

    public static bool operator <=(HeimdallVersion left, HeimdallVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >(HeimdallVersion left, HeimdallVersion right) => left.CompareTo(right) > 0;

    public static bool operator >=(HeimdallVersion left, HeimdallVersion right) => left.CompareTo(right) >= 0;
}
