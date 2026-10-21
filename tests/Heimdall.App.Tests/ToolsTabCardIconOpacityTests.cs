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

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Guards the icon tile of the Tools tab cards. The tile used to be a single Border
/// whose Opacity carried the 12 percent category tint and whose Child was the icon,
/// so WPF multiplied the icon down to the same 12 percent and every card rendered
/// without a visible icon. The tint and the icon must be siblings: the tint faint,
/// the icon at full strength.
/// </summary>
[Collection(CredentialDialogPasswordDirtyCollection.Name)]
public sealed class ToolsTabCardIconOpacityTests
{
    private const double ExpectedTintOpacity = 0.12;
    private const double OpacityTolerance = 0.005;

    [Fact]
    public void CreateToolsTabCard_DrawsTheIconOpaqueOverAFaintTint()
    {
        RunOnStaThread(() =>
        {
            App application = CreateApplication();
            try
            {
                var registry = new ToolRegistry();
                var service = new ToolsTabPopulationService(registry);
                MainViewModel vm = CreateViewModel();

                ToolDescriptor[] withIcon = registry.All
                    .Where(d => d.IconResourceKey is not null
                        && Application.Current.TryFindResource(d.IconResourceKey) is Geometry)
                    .ToArray();

                // Anti-vacuity: the sweep must see real cards with a resolvable icon.
                Assert.True(
                    withIcon.Length > 0,
                    "No built-in tool resolves an icon geometry; the sweep would guard nothing.");

                foreach (ToolDescriptor descriptor in withIcon)
                {
                    FrameworkElement card = service.CreateToolsTabCard(descriptor, vm, _ => { }, _ => { });

                    Path icon = Assert.Single(FindLogical<Path>(card));
                    Assert.NotNull(icon.Fill);

                    double effective = 1.0;
                    DependencyObject? node = icon;
                    while (node is not null)
                    {
                        if (node is UIElement element)
                        {
                            effective *= element.Opacity;
                        }

                        if (ReferenceEquals(node, card))
                        {
                            break;
                        }

                        node = LogicalTreeHelper.GetParent(node);
                    }

                    Assert.True(
                        ReferenceEquals(node, card),
                        $"The icon of '{descriptor.Id}' is not reachable from its card through the logical tree.");
                    Assert.True(
                        Math.Abs(effective - 1.0) < OpacityTolerance,
                        $"The icon of '{descriptor.Id}' renders at an effective opacity of {effective:0.###}; "
                        + "an ancestor carries the tint opacity and fades the icon with it.");

                    var tile = (Panel)LogicalTreeHelper.GetParent(icon);
                    Border tint = Assert.Single(tile.Children.OfType<Border>());
                    Assert.True(
                        Math.Abs(tint.Opacity - ExpectedTintOpacity) < OpacityTolerance,
                        $"The tint behind the icon of '{descriptor.Id}' has opacity {tint.Opacity:0.###}, "
                        + $"expected {ExpectedTintOpacity}.");
                    Assert.NotNull(tint.Background);
                }
            }
            finally
            {
                ResetApplicationSingleton(application);
            }
        });
    }

    private static MainViewModel CreateViewModel()
    {
        var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        FieldInfo? localizer = typeof(MainViewModel).GetField(
            "_localizer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(localizer);
        localizer.SetValue(vm, new LocalizationManager());
        return vm;
    }

    private static IEnumerable<T> FindLogical<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependencyChild)
            {
                continue;
            }

            if (dependencyChild is T match)
            {
                yield return match;
            }

            foreach (T nested in FindLogical<T>(dependencyChild))
            {
                yield return nested;
            }
        }
    }

    private static App CreateApplication()
    {
        Assert.Null(Application.Current);
        App application = new App();
        application.InitializeComponent();
        return application;
    }

    private static void ResetApplicationSingleton(App application)
    {
        Assert.Same(application, Application.Current);
        BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        FieldInfo? appInstance = typeof(Application).GetField("_appInstance", flags);
        FieldInfo? appCreated = typeof(Application).GetField("_appCreatedInThisAppDomain", flags);
        FieldInfo? isShuttingDown = typeof(Application).GetField("_isShuttingDown", flags);
        Assert.NotNull(appInstance);
        Assert.NotNull(appCreated);
        Assert.NotNull(isShuttingDown);
        appInstance.SetValue(null, null);
        appCreated.SetValue(null, false);
        isShuttingDown.SetValue(null, false);
        Assert.Null(Application.Current);
    }

    private static void RunOnStaThread(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
