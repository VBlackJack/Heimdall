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
using Heimdall.App.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Heimdall.App.Tests;

public sealed class WindowBoundsRestorationPolicyTests
{
    [Fact]
    public void Resolve_LShapedTopologyHole_ClampsToNearestWorkingArea()
    {
        Rect savedBounds = new(2500, 100, 800, 600);
        Rect[] workingAreas =
        [
            new Rect(0, 0, 1920, 1080),
            new Rect(1920, 1080, 1920, 1080),
        ];

        Rect? result = WindowBoundsRestorationPolicy.Resolve(savedBounds, workingAreas);

        Assert.Equal(new Rect(2500, 1080, 800, 600), result);
    }

    [Fact]
    public void Resolve_OnePixelVisible_ClampsFullyIntoWorkingArea()
    {
        Rect savedBounds = new(1919, 100, 800, 600);
        Rect[] workingAreas = [new Rect(0, 0, 1920, 1040)];

        Rect? result = WindowBoundsRestorationPolicy.Resolve(savedBounds, workingAreas);

        Assert.Equal(new Rect(1120, 100, 800, 600), result);
    }

    [Fact]
    public void Resolve_IntersectsMultipleWorkingAreas_UsesLargestIntersection()
    {
        Rect savedBounds = new(900, 100, 400, 600);
        Rect[] workingAreas =
        [
            new Rect(0, 0, 1000, 800),
            new Rect(1000, 0, 1000, 800),
        ];

        Rect? result = WindowBoundsRestorationPolicy.Resolve(savedBounds, workingAreas);

        Assert.Equal(new Rect(1000, 100, 400, 600), result);
    }

    [Fact]
    public void Resolve_ValidBoundsInsideWorkingArea_PreservesExactRectangle()
    {
        Rect savedBounds = new(320, 180, 800, 600);
        Rect[] workingAreas = [new Rect(0, 0, 1920, 1040)];

        Rect? result = WindowBoundsRestorationPolicy.Resolve(savedBounds, workingAreas);

        Assert.Equal(savedBounds, result);
    }

    [Fact]
    public void Resolve_OversizedWindow_PreservesSizeAndMaximizesVisibleArea()
    {
        Rect savedBounds = new(200, 150, 1200, 900);
        Rect[] workingAreas = [new Rect(0, 0, 1000, 800)];

        Rect? result = WindowBoundsRestorationPolicy.Resolve(savedBounds, workingAreas);

        Assert.Equal(new Rect(0, 0, 1200, 900), result);
    }

    [Fact]
    public void Resolve_NoWorkingAreas_ReturnsNoPlacement()
    {
        Rect savedBounds = new(100, 100, 800, 600);

        Rect? result = WindowBoundsRestorationPolicy.Resolve(savedBounds, []);

        Assert.Null(result);
    }

    [Fact]
    public void ConvertPixelsToDips_InjectedScale_ConvertsPositionAndSize()
    {
        DrawingRectangle pixelBounds = new(-1920, 120, 1920, 1080);

        Rect result = WindowWorkingAreaProvider.ConvertPixelsToDips(
            pixelBounds,
            dpiScaleX: 1.5,
            dpiScaleY: 1.25);

        Assert.Equal(new Rect(-1280, 96, 1280, 864), result);
    }
}
