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
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels.Tools;
using Heimdall.App.Views.Tools;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;

namespace Heimdall.App.UiTests;

/// <summary>
/// The Tools tab card of a recipe tool, and the recipe view built from a recipe: the controls
/// each input becomes, with their names, and the card's provenance line. In-process WPF only; no
/// program is started.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class RecipeToolViewTests
{
    private static readonly Size ViewSize = new(900, 700);

    private static Recipe Recipe(string name = "Probe", string author = "Acme", string description = "Probes the thing.")
        => new(
            SchemaVersion: 1,
            Id: "acme.probe",
            Version: "1.0.0",
            Author: author,
            Name: name,
            Description: description,
            Labels: new Dictionary<string, RecipeLabels>(),
            Program: "ping.exe",
            Arguments: [RecipeArgument.Bare("{in:target}"), RecipeArgument.Bare("{in:flag}")],
            Inputs:
            [
                new RecipeInput("target", RecipeInputType.Hostname, "Target host", true, null, null, null, null, null),
                new RecipeInput("flag", RecipeInputType.Bool, "Verbose", false, "false", null, null, null, ["-v"]),
                new RecipeInput("mode", RecipeInputType.Choice, "Mode", true, "fast", null, null, ["fast", "slow"], null),
            ],
            Output: RecipeOutputFormat.Text,
            OutputEncoding: RecipeOutputEncoding.Utf8,
            Stderr: RecipeStderrMode.Separate,
            SuccessExitCodes: [0],
            TimeoutSeconds: 30,
            Icon: null);

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element)
            {
                continue;
            }

            if (element is T match)
            {
                yield return match;
            }

            foreach (T nested in LogicalDescendants<T>(element))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public async Task TheCardOfARecipeToolShowsItsNameItsProvenanceAndItsPlainDescription()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            ToolDescriptor descriptor = ToolRegistry.CreateRecipeDescriptor(Recipe(name: "ToolDescPING", description: "ToolDescPING <b>raw</b>"));
            ToolsTabPopulationService service = new(new ToolRegistry());

            FrameworkElement card = service.CreateToolsTabCard(
                descriptor, WpfTestHost.Localizer.GetString, [], _ => { }, _ => { });

            string[] texts = [.. LogicalDescendants<TextBlock>(card).Select(block => block.Text)];
            Assert.Contains("ToolDescPING", texts);
            Assert.Contains("Unverified recipe by Acme", texts);
            Assert.Contains("ToolDescPING <b>raw</b>", texts);
            Button launch = LogicalDescendants<Button>(card).First(button => button.Tag is ToolDescriptor);
            Assert.Equal("ToolDescPING, Unverified recipe by Acme", AutomationProperties.GetName(launch));
        }).Task;
    }

    [Fact]
    public async Task TheCardOfABuiltInToolHasNoProvenanceLine()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            ToolRegistry registry = new();
            ToolDescriptor ping = registry.GetById("PING")!;
            ToolsTabPopulationService service = new(registry);

            FrameworkElement card = service.CreateToolsTabCard(
                ping, WpfTestHost.Localizer.GetString, [], _ => { }, _ => { });

            Assert.DoesNotContain(LogicalDescendants<TextBlock>(card), block => block.Text.Contains("unverified", StringComparison.Ordinal));
            Button launch = LogicalDescendants<Button>(card).First(button => button.Tag is ToolDescriptor);
            Assert.Equal(ping.ResolveLabel(WpfTestHost.Localizer.GetString), AutomationProperties.GetName(launch));
        }).Task;
    }

    [Fact]
    public async Task TheViewBuildsOneNamedControlPerInputAndTheHeaderSaysWhatItIs()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            RecipeToolViewModel viewModel = new(Recipe(), @"C:\Windows\System32\ping.exe", StoredSha256, Services());
            RecipeToolView view = new(viewModel);
            view.Initialize(new ToolContext(), WpfTestHost.Localizer);
            Layout(view);

            Assert.Equal("Probe", view.FindName("HeaderTitle") is TextBlock title ? title.Text : null);
            Assert.Equal("Unverified recipe by Acme", ((TextBlock)view.FindName("HeaderProvenance")).Text);
            Assert.Equal("Probes the thing.", ((TextBlock)view.FindName("HeaderDescription")).Text);

            TextBox box = Assert.Single(Descendants<TextBox>(view), item => AutomationProperties.GetName(item) == "Target host");
            Assert.False(box.IsReadOnly);
            CheckBox check = Assert.Single(Descendants<CheckBox>(view), item => AutomationProperties.GetName(item) == "Verbose");
            Assert.True(check.IsEnabled);
            ComboBox combo = Assert.Single(Descendants<ComboBox>(view), item => AutomationProperties.GetName(item) == "Mode");
            Assert.Equal("fast", combo.SelectedItem);

            Button run = (Button)view.FindName("BtnRun");
            Button stop = (Button)view.FindName("BtnStop");
            Assert.Equal("Run", AutomationProperties.GetName(run));
            Assert.Equal("Stop", AutomationProperties.GetName(stop));
            Assert.True(run.IsEnabled);
            Assert.False(stop.IsEnabled);
            Assert.Equal(Visibility.Collapsed, ((ProgressBar)view.FindName("RunProgress")).Visibility);

            view.Dispose();
        }).Task;
    }

    [Fact]
    public async Task ARefusedValueIsShownNextToItsFieldAndTheRecipeIsNotStarted()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(async () =>
        {
            CountingRunner runner = new();
            RecipeToolViewModel viewModel = new(Recipe(), @"C:\Windows\System32\ping.exe", StoredSha256, Services(runner));
            RecipeToolView view = new(viewModel);
            view.Initialize(new ToolContext(), WpfTestHost.Localizer);
            Layout(view);

            await viewModel.RunCommand.ExecuteAsync(null);
            Layout(view);

            Assert.Equal(0, runner.Calls);
            Assert.True(viewModel.Fields[0].HasError);
            TextBlock[] errors = [.. Descendants<TextBlock>(view).Where(block => block.Visibility == Visibility.Visible && block.Text == viewModel.Fields[0].Error)];
            Assert.NotEmpty(errors);

            view.Dispose();
        }).Task.Unwrap();
    }

    [Fact]
    public async Task WhileARunIsUnderWayTheFieldsAreLockedAndStopIsEnabled()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(async () =>
        {
            TaskCompletionSource<RecipeRunResult> gate = new();
            CountingRunner runner = new() { Gate = gate.Task };
            RecipeToolViewModel viewModel = new(Recipe(), @"C:\Windows\System32\ping.exe", StoredSha256, Services(runner));
            RecipeToolView view = new(viewModel);
            view.Initialize(new ToolContext(), WpfTestHost.Localizer);
            viewModel.Fields[0].Text = "host.example";
            Layout(view);

            Task run = viewModel.RunCommand.ExecuteAsync(null);
            Layout(view);

            TextBox box = Assert.Single(Descendants<TextBox>(view), item => AutomationProperties.GetName(item) == "Target host");
            Assert.True(box.IsReadOnly);
            Assert.False(Assert.Single(Descendants<CheckBox>(view), item => AutomationProperties.GetName(item) == "Verbose").IsEnabled);
            Assert.False(((Button)view.FindName("BtnRun")).IsEnabled);
            Assert.True(((Button)view.FindName("BtnStop")).IsEnabled);
            Assert.Equal(Visibility.Visible, ((ProgressBar)view.FindName("RunProgress")).Visibility);

            gate.SetResult(new RecipeRunResult(RecipeRunStatus.Succeeded, 0, "done", string.Empty, false, null, RecipeRunRefusal.None));
            await run;
            Layout(view);

            Assert.False(box.IsReadOnly);
            Assert.Equal("done", ((TextBox)view.FindName("TxtOutput")).Text);
            Assert.Equal("Succeeded.", ((TextBlock)view.FindName("TxtStatus")).Text);

            view.Dispose();
        }).Task.Unwrap();
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(ViewSize);
        view.Arrange(new Rect(ViewSize));
        view.UpdateLayout();
    }

    private static RecipeToolServices Services(CountingRunner? runner = null)
        => new(
            runner ?? new CountingRunner(),
            new SameProgramResolver(),
            new FolderlessWorkFolders(),
            new AvailableTools(),
            () => [],
            new SameVersions());

    private static readonly string StoredSha256 = new('a', 64);

    private sealed class SameVersions : IRecipeVersions
    {
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public string? CurrentSha256(string id) => StoredSha256;
    }

    private sealed class CountingRunner : IRecipeRunner
    {
        public int Calls { get; private set; }

        public Task<RecipeRunResult>? Gate { get; init; }

        public Task<RecipeRunResult> RunAsync(
            Recipe recipe,
            string programPath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Gate ?? Task.FromResult(new RecipeRunResult(RecipeRunStatus.Succeeded, 0, string.Empty, string.Empty, false, null, RecipeRunRefusal.None));
        }
    }

    private sealed class SameProgramResolver : IRecipeProgramResolver
    {
        public RecipeProgramComparison CompareWithStored(string? program, IReadOnlyList<string> allowedFolders, string storedPath)
            => RecipeProgramComparison.Same;
    }

    private sealed class FolderlessWorkFolders : IRecipeWorkFolders
    {
        public string Create() => @"C:\Windows\Temp\recipe-ui-test";

        public void Delete(string path)
        {
        }

        public void CleanStale()
        {
        }
    }

    private sealed class AvailableTools : IToolAvailability
    {
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public ToolAvailabilityState GetState(string toolId) => ToolAvailabilityState.Available;

        public bool IsPackEnabled(ToolPack pack) => true;

        public bool IsPackForbiddenByPolicy(ToolPack pack) => false;
    }
}
