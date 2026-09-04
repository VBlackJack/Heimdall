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

using System.Collections.Concurrent;
using System.Reflection;

namespace Heimdall.Core.Configuration;

/// <summary>One declared range, read back from a <see cref="SettingRangeAttribute"/>.</summary>
/// <param name="PropertyName">The property the range is declared on.</param>
/// <param name="Min">The smallest recommended value.</param>
/// <param name="Max">The largest recommended value.</param>
/// <param name="DisabledValue">The accepted out-of-range sentinel, or null.</param>
public sealed record SettingRange(string PropertyName, int Min, int Max, int? DisabledValue)
{
    /// <summary>Whether <paramref name="value"/> is inside the range or is the disabled sentinel.</summary>
    public bool Accepts(int value)
        => (value >= Min && value <= Max) || (DisabledValue.HasValue && value == DisabledValue.Value);
}

/// <summary>
/// The declared ranges of a settings type, read once and shared by every reader.
/// </summary>
/// <remarks>
/// <para>The single way to read a bound. The schema validator iterates <see cref="For"/> to
/// diagnose a loaded file; the settings screen's range attribute calls <see cref="Of"/> to refuse
/// a save; the screen's messages format the same numbers into their translations. None of them
/// holds a number of its own.</para>
/// <para>Reflection runs once per type and is cached: the loader reads settings on every refresh,
/// and the screen validates on every keystroke.</para>
/// </remarks>
public static class SettingRanges
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, SettingRange>> Cache = new();

    /// <summary>Every declared range of <paramref name="type"/>, keyed by property name.</summary>
    public static IReadOnlyDictionary<string, SettingRange> For(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Cache.GetOrAdd(type, Read);
    }

    /// <summary>The declared range of one property of <paramref name="type"/>.</summary>
    /// <exception cref="KeyNotFoundException">The property declares no range.</exception>
    public static SettingRange Of(Type type, string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        return For(type).TryGetValue(propertyName, out SettingRange? range)
            ? range
            : throw new KeyNotFoundException(
                $"{type.Name}.{propertyName} declares no [SettingRange]; a reader asked for a bound that does not exist.");
    }

    /// <summary>The declared range of one <see cref="AppSettings"/> property.</summary>
    public static SettingRange Of(string appSettingsPropertyName)
        => Of(typeof(AppSettings), appSettingsPropertyName);

    /// <summary>Reads the current value of a ranged property from an instance.</summary>
    /// <remarks>
    /// For the validator, which walks every declared range of a loaded object. A property that
    /// declares a range is an <see langword="int"/> by construction: the attribute is only applied
    /// to those, and <see cref="Read"/> refuses any other.
    /// </remarks>
    public static int ValueOf(object instance, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(instance);
        PropertyInfo property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new KeyNotFoundException($"{instance.GetType().Name}.{propertyName} does not exist.");
        return (int)(property.GetValue(instance) ?? 0);
    }

    private static IReadOnlyDictionary<string, SettingRange> Read(Type type)
    {
        Dictionary<string, SettingRange> ranges = new(StringComparer.Ordinal);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            SettingRangeAttribute? declared = property.GetCustomAttribute<SettingRangeAttribute>();
            if (declared is null)
            {
                continue;
            }

            if (property.PropertyType != typeof(int))
            {
                throw new InvalidOperationException(
                    $"{type.Name}.{property.Name} declares a [SettingRange] but is not an int; the range would bound nothing.");
            }

            ranges[property.Name] = new SettingRange(
                property.Name,
                declared.Min,
                declared.Max,
                declared.ZeroMeansOff ? 0 : null);
        }

        return ranges;
    }
}
