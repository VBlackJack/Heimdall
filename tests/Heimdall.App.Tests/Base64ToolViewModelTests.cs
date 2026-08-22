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
using System.Text;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class Base64ToolViewModelTests
{
    /// <summary>
    /// PrefillInput reached the codec directly, without the gate the commands go through.
    /// It could therefore start a second run over a first one, and, because it never marked
    /// anything as in flight, let a command start a second run over it.
    /// </summary>
    [Fact]
    public async Task PrefillInput_WhileACommandIsRunning_DoesNotReenterTheBody()
    {
        GatedBase64ToolService service = new();
        Base64ToolViewModel vm = new(service) { InputText = "hello" };

        Task first = vm.EncodeCommand.ExecuteAsync(null);
        await service.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task prefill = vm.PrefillInput("world");

        service.Release();
        await Task.WhenAll(first, prefill).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, service.EncodeEntryCount);
        Assert.False(service.FirstTokenWasCancelled);
    }

    /// <summary>
    /// The other half of the same contract: while PrefillInput is running, a command must
    /// find the tool busy. Fails against a build where PrefillInput never sets that state.
    /// </summary>
    [Fact]
    public async Task PrefillInput_WhileRunning_BlocksACommandFromReenteringTheBody()
    {
        GatedBase64ToolService service = new();
        Base64ToolViewModel vm = new(service);

        Task prefill = vm.PrefillInput("hello");
        await service.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(vm.EncodeCommand.CanExecute(null));
        Task second = vm.EncodeCommand.ExecuteAsync(null);

        service.Release();
        await Task.WhenAll(prefill, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, service.EncodeEntryCount);
    }

    [Fact]
    public async Task DecodeCommand_SecondExecuteAsyncWhileRunning_DoesNotReenterTheBody()
    {
        GatedBase64ToolService service = new();
        Base64ToolViewModel vm = new(service) { InputText = "aGVsbG8=" };

        Task first = vm.DecodeCommand.ExecuteAsync(null);
        await service.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // ExecuteAsync deliberately does not consult CanExecute, and the decode button is wired
        // to a Click handler that calls it directly, so this is exactly what a second click does.
        Assert.False(vm.DecodeCommand.CanExecute(null));
        Task second = vm.DecodeCommand.ExecuteAsync(null);

        service.Release();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        // Entry count, not just the final output: the two runs decode the same input, so
        // comparing results cannot tell one execution from two.
        Assert.Equal(1, service.DecodeEntryCount);
        Assert.False(service.FirstTokenWasCancelled);
        Assert.Equal("hello", vm.OutputText);
    }

    [Fact]
    public async Task EncodeCommand_SecondExecuteAsyncWhileRunning_DoesNotReenterTheBody()
    {
        GatedBase64ToolService service = new();
        Base64ToolViewModel vm = new(service) { InputText = "hello" };

        Task first = vm.EncodeCommand.ExecuteAsync(null);
        await service.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The encode button is gated by IsEnabled, but the Ctrl+Enter shortcut calls
        // ExecuteAsync directly and no binding gates a keyboard accelerator.
        Task second = vm.EncodeCommand.ExecuteAsync(null);

        service.Release();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, service.EncodeEntryCount);
        Assert.False(service.FirstTokenWasCancelled);
    }

    [Fact]
    public async Task IsDecodeEnabled_IsFalseWhileRunningAndTrueAgainAfterwards()
    {
        GatedBase64ToolService service = new();
        Base64ToolViewModel vm = new(service) { InputText = "aGVsbG8=" };

        Assert.True(vm.IsDecodeEnabled);

        Task first = vm.DecodeCommand.ExecuteAsync(null);
        await service.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The button binds this, so a value that stayed true would leave it clickable and
        // announce an action that is not actually available.
        Assert.False(vm.IsDecodeEnabled);

        service.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(vm.IsDecodeEnabled);
    }

    [Fact]
    public async Task PrefillInput_EncodesImmediately()
    {
        var vm = new Base64ToolViewModel(new FakeBase64ToolService());
        vm.Initialize(await CreateLocalizerAsync("en"));

        await vm.PrefillInput("abc");

        Assert.Equal("YWJj", vm.OutputText);
        Assert.True(vm.IsResultsPanelVisible);
        Assert.Equal("Encoded 3 bytes", vm.StatusText);
    }

    [Fact]
    public async Task EncodeCommand_UsesUtf8Input()
    {
        var vm = new Base64ToolViewModel(new FakeBase64ToolService());
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.InputText = "hé";

        await vm.EncodeCommand.ExecuteAsync(null);

        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("hé"), Base64FormattingOptions.InsertLineBreaks), vm.OutputText);
    }

    [Fact]
    public async Task DecodeCommand_DecodesTextOutput()
    {
        var vm = new Base64ToolViewModel(new FakeBase64ToolService());
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.InputText = "YWJj";

        await vm.DecodeCommand.ExecuteAsync(null);

        Assert.Equal("abc", vm.OutputText);
        Assert.Equal("Decoded 3 bytes", vm.StatusText);
        Assert.Equal(new byte[] { 97, 98, 99 }, vm.TryGetLastDecodedBytes());
    }

    [Fact]
    public async Task DecodeCommand_InvalidInput_ShowsLocalizedError()
    {
        var vm = new Base64ToolViewModel(new FakeBase64ToolService { DecodeException = new FormatException() });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.InputText = "%%%";

        await vm.DecodeCommand.ExecuteAsync(null);

        Assert.Equal("Invalid Base64 input", vm.StatusText);
        Assert.Equal("ErrorTextBrush", vm.StatusForegroundBrushKey);
    }

    [Fact]
    public async Task EncodeCommand_FileMode_UsesLoadedBytes()
    {
        var vm = new Base64ToolViewModel(new FakeBase64ToolService
        {
            LoadOutcome = new FileLoadOutcome(true, [251, 255, 255], "data.bin", FileLoadError.None),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.IsFileMode = true;
        vm.InputText = "text";
        await vm.LoadFileAsync("ignored", CancellationToken.None);
        vm.IsUrlSafe = true;

        await vm.EncodeCommand.ExecuteAsync(null);

        Assert.Equal("-___", vm.OutputText);
    }

    [Fact]
    public async Task DecodeCommand_FileMode_CachesBytesWithoutChangingOutput()
    {
        var vm = new Base64ToolViewModel(new FakeBase64ToolService());
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.IsFileMode = true;
        vm.OutputText = "previous";
        vm.InputText = "YWJj";

        await vm.DecodeCommand.ExecuteAsync(null);

        Assert.Equal("previous", vm.OutputText);
        Assert.Equal(new byte[] { 97, 98, 99 }, vm.TryGetLastDecodedBytes());
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public void OnInputTextChangedFromView_AfterInitialization_ClearsOutputAndStatus()
    {
        var vm = new Base64ToolViewModel(new FakeBase64ToolService());
        vm.MarkInitialized();
        vm.OutputText = "value";
        vm.StatusText = "status";
        vm.IsResultsPanelVisible = true;
        vm.IsEmptyStateVisible = false;

        vm.OnInputTextChangedFromView();

        Assert.Equal(string.Empty, vm.OutputText);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.False(vm.IsResultsPanelVisible);
        Assert.True(vm.IsEmptyStateVisible);
    }

    [Fact]
    public async Task LoadFileAsync_Success_SetsReadOnlyInput()
    {
        var vm = new Base64ToolViewModel(new FakeBase64ToolService
        {
            LoadOutcome = new FileLoadOutcome(true, [1, 2, 3], "data.bin", FileLoadError.None),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();

        await vm.LoadFileAsync("ignored", CancellationToken.None);

        Assert.True(vm.IsInputReadOnly);
        Assert.Contains("data.bin", vm.InputText, StringComparison.Ordinal);
        Assert.Null(vm.TryGetLastDecodedBytes());
    }

    [Fact]
    public async Task LoadFileAsync_FileTooLarge_ShowsLocalizedError()
    {
        var vm = new Base64ToolViewModel(new FakeBase64ToolService
        {
            LoadOutcome = new FileLoadOutcome(false, null, null, FileLoadError.FileTooLarge),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();

        await vm.LoadFileAsync("ignored", CancellationToken.None);

        Assert.Equal("File exceeds the 5 MB size limit.", vm.StatusText);
        Assert.Equal("ErrorTextBrush", vm.StatusForegroundBrushKey);
    }

    [Fact]
    public async Task LoadFileAsync_IoFailure_ShowsFormattedError()
    {
        var vm = new Base64ToolViewModel(new FakeBase64ToolService
        {
            LoadOutcome = new FileLoadOutcome(false, null, null, FileLoadError.IoFailure, "disk error"),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();

        await vm.LoadFileAsync("ignored", CancellationToken.None);

        Assert.Equal("Error: disk error", vm.StatusText);
    }

    [Fact]
    public async Task SaveFileAsync_WithDecodedBytes_ReportsSavedStatus()
    {
        var service = new FakeBase64ToolService();
        var vm = new Base64ToolViewModel(service);
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.InputText = "YWJj";
        await vm.DecodeCommand.ExecuteAsync(null);

        await vm.SaveFileAsync("C:\\temp\\saved.bin", CancellationToken.None);

        Assert.Equal("Saved to C:\\temp\\saved.bin", vm.StatusText);
        Assert.Equal(new byte[] { 97, 98, 99 }, service.SavedBytes);
    }

    [Fact]
    public async Task SaveFileAsync_ServiceThrows_ShowsFormattedError()
    {
        var service = new FakeBase64ToolService { SaveException = new IOException("write failed") };
        var vm = new Base64ToolViewModel(service);
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.InputText = "YWJj";
        await vm.DecodeCommand.ExecuteAsync(null);

        await vm.SaveFileAsync("C:\\temp\\saved.bin", CancellationToken.None);

        Assert.Equal("Error: write failed", vm.StatusText);
        Assert.Equal("ErrorTextBrush", vm.StatusForegroundBrushKey);
    }

    [Fact]
    public void IsFileMode_TurnedOff_ResetsState()
    {
        var vm = new Base64ToolViewModel(new FakeBase64ToolService());
        vm.IsFileMode = true;
        vm.IsInputReadOnly = true;
        vm.InputText = "loaded";
        vm.OutputText = "result";
        vm.StatusText = "status";
        vm.IsResultsPanelVisible = true;
        vm.IsEmptyStateVisible = false;

        vm.IsFileMode = false;

        Assert.False(vm.IsBrowseFileButtonVisible);
        Assert.False(vm.IsInputReadOnly);
        Assert.Equal(string.Empty, vm.InputText);
        Assert.Equal(string.Empty, vm.OutputText);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.True(vm.IsEmptyStateVisible);
    }

    [Fact]
    public async Task LocaleChanged_ReprojectsStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new Base64ToolViewModel(new FakeBase64ToolService());
        vm.Initialize(localizer);
        await vm.PrefillInput("abc");
        var english = vm.StatusText;

        await localizer.SwitchLocaleAsync("fr");

        Assert.NotEqual(english, vm.StatusText);
        Assert.Equal("3 octets encodés", vm.StatusText);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    /// <summary>
    /// Holds a codec call open so a second invocation can be attempted while the first is still
    /// in flight, and counts how many times the body was actually entered.
    /// </summary>
    private sealed class GatedBase64ToolService : IBase64ToolService
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private CancellationToken _firstToken;

        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DecodeEntryCount { get; private set; }

        public int EncodeEntryCount { get; private set; }

        public bool FirstTokenWasCancelled => _firstToken.IsCancellationRequested;

        public void Release() => _release.TrySetResult();

        public async Task<byte[]> DecodeAsync(string base64, bool urlSafe, CancellationToken ct)
        {
            if (++DecodeEntryCount == 1)
            {
                _firstToken = ct;
                FirstEntered.TrySetResult();
            }

            await _release.Task.ConfigureAwait(false);
            return Heimdall.Core.Codecs.Base64Codec.Decode(base64, urlSafe);
        }

        public async Task<string> EncodeAsync(byte[] data, bool urlSafe, CancellationToken ct)
        {
            if (++EncodeEntryCount == 1)
            {
                _firstToken = ct;
                FirstEntered.TrySetResult();
            }

            await _release.Task.ConfigureAwait(false);
            return Heimdall.Core.Codecs.Base64Codec.Encode(data, urlSafe);
        }

        public Task<FileLoadOutcome> LoadFileAsync(string path, long maxBytes, CancellationToken ct)
            => Task.FromResult(new FileLoadOutcome(true, [1], "data.bin", FileLoadError.None));

        public Task SaveFileAsync(string path, byte[] data, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeBase64ToolService : IBase64ToolService
    {
        public Exception? DecodeException { get; set; }
        public Exception? SaveException { get; set; }

        public FileLoadOutcome LoadOutcome { get; set; } =
            new(true, [1, 2, 3], "data.bin", FileLoadError.None);

        public byte[]? SavedBytes { get; private set; }

        public Task<string> EncodeAsync(byte[] data, bool urlSafe, CancellationToken ct)
            => Task.FromResult(Heimdall.Core.Codecs.Base64Codec.Encode(data, urlSafe));

        public Task<byte[]> DecodeAsync(string base64, bool urlSafe, CancellationToken ct)
        {
            if (DecodeException is not null)
            {
                return Task.FromException<byte[]>(DecodeException);
            }

            return Task.FromResult(Heimdall.Core.Codecs.Base64Codec.Decode(base64, urlSafe));
        }

        public Task<FileLoadOutcome> LoadFileAsync(string path, long maxBytes, CancellationToken ct)
            => Task.FromResult(LoadOutcome);

        public Task SaveFileAsync(string path, byte[] data, CancellationToken ct)
        {
            if (SaveException is not null)
            {
                return Task.FromException(SaveException);
            }

            SavedBytes = data;
            return Task.CompletedTask;
        }
    }
}
