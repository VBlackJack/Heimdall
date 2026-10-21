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

namespace Heimdall.App.Tests;

/// <summary>
/// The tour card never covers the control its step spotlights, and on the session-sidebar steps
/// it sits centred in the free band beside the sidebar instead of across the whole window.
/// </summary>
/// <remarks>
/// The first step spotlights the session list, which runs the full height of the window, so the
/// card fit neither above nor below it and fell back to the window centre: across the sidebar at
/// narrow widths, and offset from the content area's empty-state card at wide ones, so a slice of
/// that card stuck out beside the tour card.
/// </remarks>
public sealed class OnboardingCardPlacementTests
{
    private static readonly Size Card = new(560, 300);

    [Theory]
    [InlineData(1280, 800, 300)]
    [InlineData(800, 600, 280)]
    public void ATallSidebarTarget_LeavesTheCardBesideIt(double width, double height, double sidebarRight)
    {
        Rect surface = new(0, 0, width, height);
        Rect sessionList = new(6, 90, sidebarRight - 12, height - 120);
        Size card = new(Math.Min(Card.Width, width - sidebarRight - MainWindow.OnboardingCardGap), Card.Height);

        Rect placed = Placed(sessionList, surface, card);

        Assert.False(placed.IntersectsWith(sessionList), $"the card {placed} covers the spotlighted list {sessionList}");
        Assert.True(placed.Left >= sessionList.Right + MainWindow.OnboardingCardGap);
        Assert.True(placed.Right <= surface.Right);
    }

    [Fact]
    public void BesideTheTarget_TheCardIsCentredInTheFreeBand()
    {
        Rect surface = new(0, 0, 1280, 800);
        Rect sessionList = new(6, 90, 288, 680);

        Rect placed = Placed(sessionList, surface, Card);

        double bandCentre = (sessionList.Right + surface.Right) / 2;
        Assert.Equal(bandCentre, placed.Left + (placed.Width / 2), precision: 6);
        Assert.Equal((surface.Height - Card.Height) / 2, placed.Top, precision: 6);
    }

    [Fact]
    public void ATargetOnTheRight_PutsTheCardOnItsLeft()
    {
        Rect surface = new(0, 0, 1280, 800);
        Rect button = new(1180, 10, 90, 30);

        Rect placed = Placed(button, surface, Card);

        Assert.True(placed.Right <= button.Left - MainWindow.OnboardingCardGap);
        Assert.True(placed.Left >= 0);
    }

    [Fact]
    public void AFullWidthTarget_PutsTheCardBelowWhenItFits()
    {
        Rect surface = new(0, 0, 1280, 800);
        Rect strip = new(0, 40, 1280, 40);

        Rect placed = Placed(strip, surface, Card);

        Assert.Equal(strip.Bottom + MainWindow.OnboardingCardGap, placed.Top);
        Assert.False(placed.IntersectsWith(strip));
    }

    [Fact]
    public void AFullWidthTargetAtTheBottom_PutsTheCardAbove()
    {
        Rect surface = new(0, 0, 1280, 800);
        Rect strip = new(0, 740, 1280, 40);

        Rect placed = Placed(strip, surface, Card);

        Assert.Equal(strip.Top - MainWindow.OnboardingCardGap - Card.Height, placed.Top);
        Assert.False(placed.IntersectsWith(strip));
    }

    private static Rect Placed(Rect target, Rect surface, Size card)
    {
        Point origin = MainWindow.OnboardingCardOrigin(target, surface, card);
        return new Rect(origin, card);
    }
}
