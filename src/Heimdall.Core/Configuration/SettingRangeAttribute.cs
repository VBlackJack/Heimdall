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

namespace Heimdall.Core.Configuration;

/// <summary>
/// The recommended range of an integer setting, declared on the setting itself.
/// </summary>
/// <remarks>
/// <para><b>One declaration, every reader derives.</b> Before this attribute a bound was spelled
/// four times by hand: in the schema validator, in the settings screen's own range attribute, in
/// the map from that attribute's message to a locale key, and inside the English and French
/// translations. Nothing tied the four together, and they disagreed on master - the screen
/// refused a tunnel delay the loader accepted, and the loader warned on the anti-idle value that
/// turns the timer off. The bound now lives here, next to the value it bounds, and the loader,
/// the screen and the messages read it through <see cref="SettingRanges"/>.</para>
/// <para><b>Recommended, not enforced.</b> The loader keeps a value outside the range as written
/// and says so; the screen refuses to save one. Whether an out-of-range value bites at its use
/// site is decided there. Declaring the range changes nothing about that policy.</para>
/// <para><b>Why an attribute rather than a bounded value type.</b> A type would change the JSON
/// shape or need a converter, touch every binding and every use site, and buy the same invariant
/// this attribute gives for free: the check and the value cannot drift apart because they are
/// declared in one place.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class SettingRangeAttribute : Attribute
{
    /// <summary>Declares the range <paramref name="min"/>..<paramref name="max"/>, inclusive.</summary>
    /// <param name="min">The smallest recommended value.</param>
    /// <param name="max">The largest recommended value.</param>
    public SettingRangeAttribute(int min, int max)
    {
        if (min > max)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "The range's maximum is below its minimum.");
        }

        Min = min;
        Max = max;
    }

    /// <summary>The smallest recommended value.</summary>
    public int Min { get; }

    /// <summary>The largest recommended value.</summary>
    public int Max { get; }

    /// <summary>
    /// Whether zero, outside the range, is the value that turns the setting off and is accepted
    /// as such.
    /// </summary>
    /// <remarks>
    /// Every setting with an "off" value in this application spells it as zero, so the sentinel
    /// is a flag rather than a number: an attribute argument cannot be nullable, and a numeric
    /// sentinel would invite a second convention.
    /// </remarks>
    public bool ZeroMeansOff { get; init; }
}
