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

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Where the notches and the cursors of a placement bar go.
/// </summary>
/// <remarks>
/// This lives apart from the view so the rule can be asked what it would draw without a window
/// being open. It could not be, and the bar drew five evenly spaced marks whenever the real places
/// got close together: four of them at places no character could go, and none of the five moving
/// when the password got longer.
/// </remarks>
internal static class PlacementBarGeometry
{
    /// <summary>The width of a cursor, which the track has to leave room for at both ends.</summary>
    internal const double CursorWidth = 14;

    /// <summary>How tall a notch is.</summary>
    internal const double TickHeight = 5;

    /// <summary>How dark a notch is against the track.</summary>
    internal const double TickOpacity = 0.35;

    /// <summary>
    /// The closest two notches may be before none are drawn at all. Closer than this they read as
    /// one hatched band, which says less about the bar than an empty track does.
    /// </summary>
    internal const double TickMergeGap = 2.5;

    /// <summary>
    /// How many places there are to put a character on a track, which is one more than the number
    /// of characters it is read against.
    /// </summary>
    /// <remarks>
    /// The digits are placed into the password as the generator built it, and the specials are
    /// then placed into that same string with the digits already in it, which is what the two rows
    /// one above the other mean. So the two scales differ by the number of digits.
    /// </remarks>
    internal static int SlotCount(int passwordLength, int digitCount, int specialCount, bool digits)
    {
        int drawn = passwordLength - digitCount - specialCount;
        if (drawn < 1)
        {
            return 0;
        }

        return (digits ? drawn : drawn + digitCount) + 1;
    }

    /// <summary>
    /// How many notches to draw across a track this wide: one per place, or none at all.
    /// </summary>
    /// <remarks>
    /// One per place is the whole rule. A fixed number of marks is a drawing of a bar rather than
    /// a drawing of this bar, and it stays put while the thing it claims to measure changes.
    /// </remarks>
    internal static int TickCount(int slots, double trackWidth) =>
        slots >= 2 && Usable(trackWidth) / (slots - 1) >= TickMergeGap ? slots : 0;

    /// <summary>Where one notch out of <paramref name="slots"/> sits across the track.</summary>
    internal static double TickX(int slot, int slots, double trackWidth) =>
        CursorWidth / 2 + Usable(trackWidth) * slot / (slots - 1);

    /// <summary>The left edge of a cursor sitting at this percentage of the track.</summary>
    internal static double CursorLeft(double percent, double trackWidth) =>
        Usable(trackWidth) * percent / 100.0;

    /// <summary>How much of the bar one place is worth, which is what one arrow key moves.</summary>
    internal static double StepPercent(int slots) => slots < 2 ? 0 : 100.0 / (slots - 1);

    /// <summary>The nearest place to a percentage, so nothing settles between two of them.</summary>
    internal static double SnapToSlot(int slots, double percent)
    {
        if (slots < 2)
        {
            return percent;
        }

        double step = StepPercent(slots);
        return Math.Clamp(Math.Round(percent / step) * step, 0, 100);
    }

    /// <summary>The width a cursor's left edge travels across.</summary>
    internal static double Usable(double trackWidth) => Math.Max(1, trackWidth - CursorWidth);
}
