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

using System.Windows;

namespace Heimdall.App.Services;

/// <summary>
/// Resolves persisted main-window bounds against the available display geometry.
/// </summary>
internal static class WindowBoundsRestorationPolicy
{
    internal static Rect? Resolve(Rect savedBounds, IReadOnlyList<Rect> workingAreas)
    {
        if (!IsValid(savedBounds))
        {
            return null;
        }

        List<Rect> validWorkingAreas = workingAreas
            .Where(IsValid)
            .ToList();
        if (validWorkingAreas.Count == 0)
        {
            return null;
        }

        Rect selectedWorkingArea = SelectWorkingArea(savedBounds, validWorkingAreas);
        double left = ClampAxis(
            savedBounds.Left,
            savedBounds.Width,
            selectedWorkingArea.Left,
            selectedWorkingArea.Width);
        double top = ClampAxis(
            savedBounds.Top,
            savedBounds.Height,
            selectedWorkingArea.Top,
            selectedWorkingArea.Height);

        return new Rect(left, top, savedBounds.Width, savedBounds.Height);
    }

    private static Rect SelectWorkingArea(Rect savedBounds, IReadOnlyList<Rect> workingAreas)
    {
        Rect selectedWorkingArea = workingAreas[0];
        double largestIntersection = GetIntersectionArea(savedBounds, selectedWorkingArea);
        double shortestDistance = GetSquaredDistance(savedBounds, selectedWorkingArea);

        for (int index = 1; index < workingAreas.Count; index++)
        {
            Rect candidate = workingAreas[index];
            double intersection = GetIntersectionArea(savedBounds, candidate);
            double distance = GetSquaredDistance(savedBounds, candidate);

            if (intersection > largestIntersection ||
                (largestIntersection == 0 && intersection == 0 && distance < shortestDistance))
            {
                selectedWorkingArea = candidate;
                largestIntersection = intersection;
                shortestDistance = distance;
            }
        }

        return selectedWorkingArea;
    }

    private static double GetIntersectionArea(Rect first, Rect second)
    {
        Rect intersection = Rect.Intersect(first, second);
        return intersection.IsEmpty
            ? 0
            : intersection.Width * intersection.Height;
    }

    private static double GetSquaredDistance(Rect first, Rect second)
    {
        double horizontalDistance = GetAxisDistance(first.Left, first.Right, second.Left, second.Right);
        double verticalDistance = GetAxisDistance(first.Top, first.Bottom, second.Top, second.Bottom);
        return (horizontalDistance * horizontalDistance) + (verticalDistance * verticalDistance);
    }

    private static double GetAxisDistance(
        double firstStart,
        double firstEnd,
        double secondStart,
        double secondEnd)
    {
        if (firstEnd < secondStart)
        {
            return secondStart - firstEnd;
        }

        if (secondEnd < firstStart)
        {
            return firstStart - secondEnd;
        }

        return 0;
    }

    private static double ClampAxis(
        double savedStart,
        double savedLength,
        double workingAreaStart,
        double workingAreaLength)
    {
        double minimum = savedLength <= workingAreaLength
            ? workingAreaStart
            : workingAreaStart + workingAreaLength - savedLength;
        double maximum = savedLength <= workingAreaLength
            ? workingAreaStart + workingAreaLength - savedLength
            : workingAreaStart;

        return Math.Clamp(savedStart, minimum, maximum);
    }

    private static bool IsValid(Rect bounds) =>
        !bounds.IsEmpty &&
        double.IsFinite(bounds.Left) &&
        double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Width) &&
        double.IsFinite(bounds.Height) &&
        bounds.Width > 0 &&
        bounds.Height > 0;
}
