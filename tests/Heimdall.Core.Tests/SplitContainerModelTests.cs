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

namespace Heimdall.Core.Tests;

public sealed class SplitContainerModelTests
{
    private const double CustomResetRatio = 0.67;
    private const int Precision = 9;
    private const double MirroredCustomRatio = 0.33;
    private const double DraggedRatio = 0.4;
    private const double MirroredDraggedRatio = 0.6;
    private const double OutOfRangeResetRatio = 0.95;

    [Fact]
    public void ResetRatio_DefaultsToTheEvenSplit()
    {
        SplitContainerModel container = new();

        Assert.Equal(SplitContainerModel.DefaultRatio, container.ResetRatio);
    }

    [Fact]
    public void ResetRatio_KeepsItsInitValue_WhenSplitRatioChanges()
    {
        SplitContainerModel container = new() { SplitRatio = CustomResetRatio, ResetRatio = CustomResetRatio };

        container.SplitRatio = SplitContainerModel.MaxRatio;

        Assert.Equal(CustomResetRatio, container.ResetRatio);
    }

    private static SplitContainerModel MakeSwappable(double splitRatio, double resetRatio, out SessionPaneModel a, out SessionPaneModel b)
    {
        a = new SessionPaneModel { PaneId = "A" };
        b = new SessionPaneModel { PaneId = "B" };
        return new SplitContainerModel { First = a, Second = b, SplitRatio = splitRatio, ResetRatio = resetRatio };
    }

    [Fact]
    public void SwapChildren_ExchangesFirstAndSecond()
    {
        var container = MakeSwappable(CustomResetRatio, CustomResetRatio, out var a, out var b);

        container.SwapChildren();

        Assert.Same(b, container.First);
        Assert.Same(a, container.Second);
    }

    [Fact]
    public void SwapChildren_MirrorsSplitRatioAndResetRatio()
    {
        var container = MakeSwappable(CustomResetRatio, CustomResetRatio, out _, out _);

        container.SwapChildren();

        Assert.Equal(MirroredCustomRatio, container.SplitRatio, Precision);
        Assert.Equal(MirroredCustomRatio, container.ResetRatio, Precision);
    }

    [Fact]
    public void SwapChildren_KeepsAnEvenSplitEven()
    {
        var container = MakeSwappable(SplitContainerModel.DefaultRatio, SplitContainerModel.DefaultRatio, out _, out _);

        container.SwapChildren();

        Assert.Equal(SplitContainerModel.DefaultRatio, container.SplitRatio, Precision);
        Assert.Equal(SplitContainerModel.DefaultRatio, container.ResetRatio, Precision);
    }

    [Fact]
    public void SwapChildren_Twice_RestoresTheOriginalValues()
    {
        var container = MakeSwappable(CustomResetRatio, CustomResetRatio, out var a, out var b);

        container.SwapChildren();
        container.SwapChildren();

        Assert.Same(a, container.First);
        Assert.Same(b, container.Second);
        Assert.Equal(CustomResetRatio, container.SplitRatio, Precision);
        Assert.Equal(CustomResetRatio, container.ResetRatio, Precision);
    }

    [Fact]
    public void SwapChildren_RaisesPropertyChangedForFirstSecondAndSplitRatio()
    {
        var container = MakeSwappable(CustomResetRatio, CustomResetRatio, out _, out _);
        List<string?> raised = [];
        container.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        container.SwapChildren();

        Assert.Contains(nameof(SplitContainerModel.First), raised);
        Assert.Contains(nameof(SplitContainerModel.Second), raised);
        Assert.Contains(nameof(SplitContainerModel.SplitRatio), raised);
    }

    [Fact]
    public void SwapChildren_ClampsTheMirroredResetRatio()
    {
        var container = MakeSwappable(SplitContainerModel.MaxRatio, OutOfRangeResetRatio, out _, out _);

        container.SwapChildren();

        Assert.Equal(SplitContainerModel.MinRatio, container.SplitRatio, Precision);
        Assert.Equal(SplitContainerModel.MinRatio, container.ResetRatio, Precision);
    }

    [Fact]
    public void SwapChildren_AfterADrag_DoubleClickRestoresTheMirroredOpeningShare()
    {
        var container = MakeSwappable(DraggedRatio, CustomResetRatio, out _, out _);

        container.SwapChildren();

        Assert.Equal(MirroredDraggedRatio, container.SplitRatio, Precision);
        Assert.Equal(MirroredCustomRatio, container.ResetRatio, Precision);

        container.SplitRatio = container.ResetRatio;

        Assert.Equal(MirroredCustomRatio, container.SplitRatio, Precision);
    }
}
