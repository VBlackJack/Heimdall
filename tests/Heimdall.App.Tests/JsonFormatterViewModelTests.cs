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

using System.IO;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Codecs;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class JsonFormatterViewModelTests
{
    [Fact]
    public async Task PrettifyCommand_ValidJson_SetsOutputAndSuccessStatus()
    {
        var vm = new JsonFormatterViewModel(new JsonFormatterToolService());
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.InputText = "{\"a\":1}";

        await vm.PrettifyCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.False(vm.HasError);
        Assert.Contains('\n', vm.OutputText);
        Assert.StartsWith("Prettified", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal("TextSecondaryBrush", vm.StatusForegroundBrushKey);
    }

    [Fact]
    public async Task MinifyCommand_ValidJson_SetsMinifiedStatus()
    {
        var vm = new JsonFormatterViewModel(new JsonFormatterToolService());
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.InputText = "{\n  \"a\": 1\n}";

        await vm.MinifyCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.Equal("{\"a\":1}", vm.OutputText);
        Assert.StartsWith("Minified", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrettifyCommand_InvalidJson_ShowsPositionError()
    {
        var vm = CreateViewModel(new FakeJsonFormatterToolService
        {
            Result = new JsonFormatResult(JsonFormatStatus.ParseError, string.Empty, "bad token", 1, 2),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.InputText = "{invalid";

        await vm.PrettifyCommand.ExecuteAsync(null);

        Assert.False(vm.HasResult);
        Assert.True(vm.HasError);
        Assert.Equal(string.Empty, vm.OutputText);
        Assert.Contains("line 2", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("position 3", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ErrorTextBrush", vm.StatusForegroundBrushKey);
    }

    [Fact]
    public async Task PrettifyCommand_ParseErrorWithoutPosition_ShowsGenericError()
    {
        var vm = CreateViewModel(new FakeJsonFormatterToolService
        {
            Result = new JsonFormatResult(JsonFormatStatus.ParseError, string.Empty, "bad json", null, null),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.InputText = "{invalid";

        await vm.PrettifyCommand.ExecuteAsync(null);

        Assert.Equal("Invalid JSON: bad json", vm.StatusText);
        Assert.Equal("ErrorTextBrush", vm.StatusForegroundBrushKey);
    }

    [Fact]
    public async Task PrettifyCommand_InputTooLarge_ShowsPolicyError()
    {
        var vm = CreateViewModel(new FakeJsonFormatterToolService
        {
            Result = new JsonFormatResult(JsonFormatStatus.InputTooLarge, string.Empty, string.Empty, null, null),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.InputText = "{}";

        await vm.PrettifyCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.False(vm.HasResult);
        Assert.Equal("Input exceeds the 5 MB size limit.", vm.StatusText);
    }

    [Fact]
    public async Task PrettifyCommand_EmptyInput_ClearsState()
    {
        var vm = CreateViewModel();
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.InputText = "   ";
        vm.OutputText = "value";

        await vm.PrettifyCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.False(vm.HasResult);
        Assert.Equal(string.Empty, vm.OutputText);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public async Task IsProcessing_DisablesCommands_DuringExecution()
    {
        var service = new BlockingJsonFormatterToolService();
        var vm = CreateViewModel(service);
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.InputText = "{\"a\":1}";

        var execution = vm.PrettifyCommand.ExecuteAsync(null);
        await service.Started.Task;

        Assert.True(vm.IsProcessing);
        Assert.False(vm.PrettifyCommand.CanExecute(null));
        Assert.False(vm.MinifyCommand.CanExecute(null));

        service.Release.SetResult();
        await execution;

        Assert.False(vm.IsProcessing);
        Assert.True(vm.PrettifyCommand.CanExecute(null));
    }

    [Fact]
    public void InputText_ChangeAfterSuccess_ClearsOutputAndStatus()
    {
        var vm = CreateViewModel();
        vm.Initialize(null);
        vm.OutputText = "value";
        vm.StatusText = "status";
        vm.HasResult = true;
        vm.HasError = true;

        vm.InputText = "{}";

        Assert.Equal(string.Empty, vm.OutputText);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.False(vm.HasResult);
        Assert.False(vm.HasError);
        Assert.True(vm.IsEmptyState);
    }

    [Fact]
    public async Task PrefillInput_Value_SetsInputWithoutFormatting()
    {
        var vm = CreateViewModel();
        vm.Initialize(await CreateLocalizerAsync("en"));

        vm.PrefillInput("{\"a\":1}");

        Assert.Equal("{\"a\":1}", vm.InputText);
        Assert.Equal(string.Empty, vm.OutputText);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public async Task PrefillInput_Null_DoesNothing()
    {
        var vm = CreateViewModel();
        vm.Initialize(await CreateLocalizerAsync("en"));

        vm.PrefillInput(null);

        Assert.Equal(string.Empty, vm.InputText);
    }

    [Fact]
    public async Task LocaleChanged_ReprojectsSuccessStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = CreateViewModel();
        vm.Initialize(localizer);
        vm.InputText = "{\"a\":1}";
        await vm.PrettifyCommand.ExecuteAsync(null);
        var english = vm.StatusText;

        await localizer.SwitchLocaleAsync("fr");

        Assert.NotEqual(english, vm.StatusText);
        Assert.Contains("Embelli", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocaleChanged_ReprojectsErrorStatusWithPosition()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = CreateViewModel(new FakeJsonFormatterToolService
        {
            Result = new JsonFormatResult(JsonFormatStatus.ParseError, string.Empty, "bad token", 1, 2),
        });
        vm.Initialize(localizer);
        vm.InputText = "{invalid";
        await vm.PrettifyCommand.ExecuteAsync(null);

        await localizer.SwitchLocaleAsync("fr");

        Assert.Contains("ligne 2", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("position 3", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        var vm = CreateViewModel();

        vm.Dispose();
        vm.Dispose();

        Assert.True(true);
    }

    private static JsonFormatterViewModel CreateViewModel(IJsonFormatterToolService? service = null)
        => new(service ?? new FakeJsonFormatterToolService());

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private sealed class FakeJsonFormatterToolService : IJsonFormatterToolService
    {
        public JsonFormatResult Result { get; set; } = new(JsonFormatStatus.Success, "{\n  \"a\": 1\n}", string.Empty, null, null);

        public Task<JsonFormatResult> FormatAsync(string input, bool indented, CancellationToken cancellationToken)
            => Task.FromResult(Result);
    }

    private sealed class BlockingJsonFormatterToolService : IJsonFormatterToolService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JsonFormatResult> FormatAsync(string input, bool indented, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new JsonFormatResult(JsonFormatStatus.Success, "{\n  \"a\": 1\n}", string.Empty, null, null);
        }
    }
}
