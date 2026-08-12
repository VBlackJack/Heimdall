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

using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public sealed class WindowUIStateSidebarLayoutTests
{
    [Fact]
    public void VisibleSidebar_FullscreenRoundTrip_RestoresMeasuredWidth()
    {
        WindowUIState state = new();

        SidebarLayoutProjection fullscreen = state.EnterFullscreen(actualSidebarWidth: 450d);
        Assert.False(fullscreen.IsVisible);
        Assert.False(fullscreen.ShowRestoreButton);
        Assert.Equal(450d, fullscreen.Width);

        SidebarLayoutProjection restored = state.ExitFullscreen();
        Assert.True(restored.IsVisible);
        Assert.False(restored.ShowRestoreButton);
        Assert.Equal(450d, restored.Width);
    }

    [Fact]
    public void HiddenSidebar_FullscreenRoundTrip_RemainsHidden_ThenSingleToggleRestoresWidth()
    {
        WindowUIState state = new()
        {
            IsSidebarHidden = true,
            SavedSidebarWidth = 450d
        };

        state.EnterFullscreen(actualSidebarWidth: 0d);
        SidebarLayoutProjection restored = state.ExitFullscreen();
        Assert.False(restored.IsVisible);
        Assert.True(restored.ShowRestoreButton);
        Assert.Equal(450d, restored.Width);

        SidebarLayoutProjection toggled = state.ToggleSidebar(actualSidebarWidth: 0d);
        Assert.True(toggled.IsVisible);
        Assert.False(toggled.ShowRestoreButton);
        Assert.Equal(450d, toggled.Width);
    }

    [Fact]
    public void TabSuppressedSidebar_FullscreenRoundTrip_DoesNotCaptureTransientZero()
    {
        WindowUIState state = new();

        state.SetSidebarSuppressedByTab(isSuppressed: true, actualSidebarWidth: 450d);
        state.EnterFullscreen(actualSidebarWidth: 0d);
        SidebarLayoutProjection stillSuppressed = state.ExitFullscreen();
        Assert.False(stillSuppressed.IsVisible);
        Assert.False(stillSuppressed.ShowRestoreButton);
        Assert.Equal(450d, stillSuppressed.Width);

        SidebarLayoutProjection restored = state.SetSidebarSuppressedByTab(
            isSuppressed: false,
            actualSidebarWidth: 0d);
        Assert.True(restored.IsVisible);
        Assert.Equal(450d, restored.Width);
    }

    [Fact]
    public void ToggleSidebar_WhileFullscreen_ChangesPreferenceWithoutShowingSidebar()
    {
        WindowUIState state = new();
        state.EnterFullscreen(actualSidebarWidth: 420d);

        SidebarLayoutProjection fullscreen = state.ToggleSidebar(actualSidebarWidth: 0d);
        Assert.True(state.IsSidebarHidden);
        Assert.False(fullscreen.IsVisible);
        Assert.False(fullscreen.ShowRestoreButton);

        SidebarLayoutProjection restored = state.ExitFullscreen();
        Assert.False(restored.IsVisible);
        Assert.True(restored.ShowRestoreButton);
        Assert.Equal(420d, restored.Width);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void EnterFullscreen_InvalidMeasuredWidth_DoesNotOverwriteSavedWidth(double measuredWidth)
    {
        WindowUIState state = new()
        {
            SavedSidebarWidth = 410d
        };

        SidebarLayoutProjection fullscreen = state.EnterFullscreen(measuredWidth);

        Assert.Equal(410d, fullscreen.Width);
    }

    [Theory]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, true, false, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, true, true, false, false)]
    public void SidebarLayout_ProjectsVisibilityAndRestoreButton(
        bool isFullscreen,
        bool isSidebarHidden,
        bool isSuppressedByTab,
        bool expectedVisible,
        bool expectedRestoreButton)
    {
        WindowUIState state = new()
        {
            IsFullscreen = isFullscreen,
            IsSidebarHidden = isSidebarHidden,
            IsSidebarSuppressedByTab = isSuppressedByTab
        };

        SidebarLayoutProjection projection = state.SidebarLayout;

        Assert.Equal(expectedVisible, projection.IsVisible);
        Assert.Equal(expectedRestoreButton, projection.ShowRestoreButton);
    }

    [Fact]
    public void ApplySidebarLayout_VisibleProjection_RestoresColumnSplitterAndButton()
    {
        RunOnStaThread(() =>
        {
            ColumnDefinition sidebarColumn = new();
            ColumnDefinition splitterColumn = new();
            Button restoreButton = new();
            SidebarLayoutProjection projection = new(
                IsVisible: true,
                ShowRestoreButton: false,
                Width: 450d);

            MainWindow.ApplySidebarLayout(
                projection,
                sidebarColumn,
                splitterColumn,
                restoreButton);

            Assert.Equal(WindowUIState.MinSidebarWidth, sidebarColumn.MinWidth);
            Assert.Equal(WindowUIState.MaxSidebarWidth, sidebarColumn.MaxWidth);
            Assert.Equal(450d, sidebarColumn.Width.Value);
            Assert.True(sidebarColumn.Width.IsAbsolute);
            Assert.True(splitterColumn.Width.IsAuto);
            Assert.Equal(Visibility.Collapsed, restoreButton.Visibility);
        });
    }

    [Theory]
    [InlineData(true, Visibility.Visible)]
    [InlineData(false, Visibility.Collapsed)]
    public void ApplySidebarLayout_HiddenProjection_CollapsesColumnsAndMapsRestoreButton(
        bool showRestoreButton,
        Visibility expectedVisibility)
    {
        RunOnStaThread(() =>
        {
            ColumnDefinition sidebarColumn = new()
            {
                MinWidth = WindowUIState.MinSidebarWidth,
                MaxWidth = WindowUIState.MaxSidebarWidth,
                Width = new GridLength(450d)
            };
            ColumnDefinition splitterColumn = new()
            {
                Width = GridLength.Auto
            };
            Button restoreButton = new()
            {
                Visibility = Visibility.Visible
            };
            SidebarLayoutProjection projection = new(
                IsVisible: false,
                ShowRestoreButton: showRestoreButton,
                Width: 450d);

            MainWindow.ApplySidebarLayout(
                projection,
                sidebarColumn,
                splitterColumn,
                restoreButton);

            Assert.Equal(0d, sidebarColumn.MinWidth);
            Assert.Equal(0d, sidebarColumn.MaxWidth);
            Assert.Equal(0d, sidebarColumn.Width.Value);
            Assert.True(sidebarColumn.Width.IsAbsolute);
            Assert.Equal(0d, splitterColumn.Width.Value);
            Assert.True(splitterColumn.Width.IsAbsolute);
            Assert.Equal(expectedVisibility, restoreButton.Visibility);
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        using ManualResetEventSlim completed = new(initialState: false);
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)), "STA test did not complete.");
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
