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

using Heimdall.Core.Models;

namespace Heimdall.Rdp;

/// <summary>
/// Calculates display dimensions for embedded RDP sessions based on the selected
/// aspect ratio mode. Applies letterboxing or pillarboxing as needed to maintain
/// the target ratio within the available container space.
/// </summary>
public static class AspectRatioManager
{
    /// <summary>
    /// Aspect ratio values as width:height fractions.
    /// </summary>
    private static readonly Dictionary<AspectRatio, (double Width, double Height)> Ratios = new()
    {
        [AspectRatio.Ratio16x9] = (16.0, 9.0),
        [AspectRatio.Ratio4x3] = (4.0, 3.0),
        [AspectRatio.Ratio21x9] = (21.0, 9.0),
    };

    /// <summary>
    /// Calculate display dimensions for the given aspect ratio within the container bounds.
    /// </summary>
    /// <param name="containerWidth">Available container width in pixels.</param>
    /// <param name="containerHeight">Available container height in pixels.</param>
    /// <param name="ratio">Target aspect ratio mode.</param>
    /// <returns>Calculated (width, height) that fits within the container while maintaining the ratio.</returns>
    public static (int Width, int Height) Calculate(
        int containerWidth,
        int containerHeight,
        AspectRatio ratio)
    {
        if (containerWidth <= 0 || containerHeight <= 0)
        {
            return (0, 0);
        }

        switch (ratio)
        {
            case AspectRatio.Stretch:
                // Use full container - no letterboxing
                return (containerWidth, containerHeight);

            case AspectRatio.Auto:
                // Match the container's natural aspect ratio (effectively same as Stretch,
                // but signals the caller to use the container dimensions for resolution updates)
                return (containerWidth, containerHeight);

            default:
                if (!Ratios.TryGetValue(ratio, out var target))
                {
                    return (containerWidth, containerHeight);
                }
                return FitToContainer(containerWidth, containerHeight, target.Width, target.Height);
        }
    }

    /// <summary>
    /// Fits the target aspect ratio within the container using letterboxing (horizontal bars)
    /// or pillarboxing (vertical bars) as needed.
    /// </summary>
    private static (int Width, int Height) FitToContainer(
        int containerWidth,
        int containerHeight,
        double ratioWidth,
        double ratioHeight)
    {
        double targetRatio = ratioWidth / ratioHeight;
        double containerRatio = (double)containerWidth / containerHeight;

        int width, height;

        if (containerRatio > targetRatio)
        {
            // Container is wider than target - pillarbox (constrain by height)
            height = containerHeight;
            width = (int)Math.Round(containerHeight * targetRatio);
        }
        else
        {
            // Container is taller than target - letterbox (constrain by width)
            width = containerWidth;
            height = (int)Math.Round(containerWidth / targetRatio);
        }

        // Ensure dimensions don't exceed container bounds (rounding edge case)
        width = Math.Min(width, containerWidth);
        height = Math.Min(height, containerHeight);

        // Ensure even dimensions (required by some RDP hosts)
        width = width & ~1;
        height = height & ~1;

        return (Math.Max(width, 2), Math.Max(height, 2));
    }
}
