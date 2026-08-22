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
using Heimdall.Core.Hashing;
using Heimdall.Core.Localization;
using Heimdall.Core.Utilities;

namespace Heimdall.App.Tests;

public sealed class HashGeneratorViewModelTests
{
    /// <summary>
    /// The browse button, the drop-zone border and the drag cursor all announce one thing:
    /// whether the tool will take a file. They must never disagree, whatever the state.
    /// </summary>
    /// <remarks>
    /// The button used to be gated on IsFileHashing alone while the two drag surfaces went
    /// through the command's predicate, which also refuses once disposed. Two predicates for
    /// one decision, with nothing holding them together.
    /// </remarks>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CanAcceptFile_AndTheCommandPredicate_AgreeForAValidPath(
        bool hashing,
        bool expected)
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService())
        {
            IsFileHashing = hashing
        };

        Assert.Equal(expected, vm.CanAcceptFile);
        Assert.Equal(vm.CanAcceptFile, vm.HashFileCommand.CanExecute("C:/some/file.bin"));
    }

    /// <summary>
    /// Disposal is the state the button used to miss entirely: it stayed enabled while both
    /// drag surfaces refused.
    /// </summary>
    [Fact]
    public void CanAcceptFile_AfterDispose_AgreesWithTheCommandPredicate()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());
        Assert.True(vm.CanAcceptFile);

        vm.Dispose();

        Assert.False(vm.CanAcceptFile);
        Assert.Equal(vm.CanAcceptFile, vm.HashFileCommand.CanExecute("C:/some/file.bin"));
    }

    /// <summary>
    /// A path is still required, so the shared predicate must not make the command accept
    /// an empty one.
    /// </summary>
    [Fact]
    public void CommandPredicate_StillRequiresAPath_WhenTheToolWouldAcceptAFile()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());

        Assert.True(vm.CanAcceptFile);
        Assert.False(vm.HashFileCommand.CanExecute(null));
        Assert.False(vm.HashFileCommand.CanExecute("   "));
    }

    [Fact]
    public async Task Initialize_PopulatesSixRowsInCanonicalOrder()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());
        var localizer = await CreateLocalizerAsync("en");

        vm.Initialize(localizer);

        Assert.Equal(HashAlgorithmCatalog.AllKinds, vm.Results.Select(r => r.Kind));
    }

    [Fact]
    public async Task UpdateInputText_WithText_ComputesHashesAndByteLength()
    {
        var service = new FakeHashGeneratorService();
        var vm = new HashGeneratorViewModel(service);
        vm.Initialize(await CreateLocalizerAsync("en"));

        vm.UpdateInputText("abc");
        await WaitUntilAsync(() => vm.IsResultsVisible);

        Assert.Equal("3 bytes", vm.ByteLengthText);
        Assert.False(vm.IsEmptyStateVisible);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", vm.Results.First(r => r.Kind == HashAlgorithmKind.Md5).HashValue);
    }

    [Fact]
    public async Task UpdateInputText_Empty_ClearsResults()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.UpdateInputText("abc");
        await WaitUntilAsync(() => vm.IsResultsVisible);

        vm.UpdateInputText(string.Empty);
        await WaitUntilAsync(() => vm.IsEmptyStateVisible);

        Assert.All(vm.Results, row => Assert.Equal(string.Empty, row.HashValue));
        Assert.Equal(string.Empty, vm.ByteLengthText);
    }

    [Fact]
    public async Task VerifyInput_Match_UpdatesResultAndHighlightsRow()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.UpdateInputText("abc");
        await WaitUntilAsync(() => vm.IsResultsVisible);

        vm.VerifyInput = "900150983cd24fb0d6963f7d28e17f72";

        Assert.Contains("MD5", vm.VerifyResultText, StringComparison.Ordinal);
        Assert.Equal("SuccessBrush", vm.VerifyForegroundBrushKey);
        Assert.True(vm.Results.First(r => r.Kind == HashAlgorithmKind.Md5).IsMatched);
    }

    [Fact]
    public async Task VerifyInput_NoMatch_UsesErrorBrush()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.UpdateInputText("abc");
        await WaitUntilAsync(() => vm.IsResultsVisible);

        vm.VerifyInput = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        Assert.Equal("ErrorBrush", vm.VerifyForegroundBrushKey);
        Assert.Contains("No match", vm.VerifyResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HashFileCommand_Success_PopulatesResultsAndFileStatus()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());
        vm.Initialize(await CreateLocalizerAsync("en"));

        vm.HashFileCommand.Execute("example.txt");
        await WaitUntilAsync(() => !vm.IsFileHashing && vm.IsResultsVisible);

        Assert.True(vm.IsFileMode);
        Assert.Contains("example.txt", vm.FileStatusText, StringComparison.Ordinal);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", vm.Results.First(r => r.Kind == HashAlgorithmKind.Md5).HashValue);
    }

    [Fact]
    public async Task HashFileCommand_TooLarge_ShowsLocalizedError()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService
        {
            FileFactory = (_, _, _) => throw new HashFileTooLargeException(HashGeneratorService.MaxFileSizeBytes + 1, HashGeneratorService.MaxFileSizeBytes),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));

        vm.HashFileCommand.Execute("large.bin");
        await WaitUntilAsync(() => !vm.IsFileHashing && vm.IsEmptyStateVisible);

        Assert.False(vm.IsFileMode);
        Assert.Contains(FileSize.Format(HashGeneratorService.MaxFileSizeBytes), vm.FileStatusText, StringComparison.Ordinal);
        Assert.Equal("ErrorBrush", vm.FileStatusBrushKey);
    }

    [Fact]
    public async Task HashFileCommand_FileNotFound_ShowsLocalizedError()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService
        {
            FileFactory = (_, _, _) => throw new FileNotFoundException(),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));

        vm.HashFileCommand.Execute("missing.bin");
        await WaitUntilAsync(() => !vm.IsFileHashing && vm.IsEmptyStateVisible);

        Assert.Equal("File not found.", vm.FileStatusText);
    }

    [Fact]
    public async Task RowCopyCommand_RaisesCopyEvent()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.UpdateInputText("abc");
        await WaitUntilAsync(() => vm.IsResultsVisible);
        var copied = string.Empty;
        vm.CopyTextRequested += (_, text) => copied = text;

        vm.Results.First(r => r.Kind == HashAlgorithmKind.Sha256).CopyCommand.Execute(null);

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", copied);
    }

    [Fact]
    public async Task RowSaveCommand_RaisesSaveEvent()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.UpdateInputText("abc");
        await WaitUntilAsync(() => vm.IsResultsVisible);
        SaveFileRequest? request = null;
        vm.SaveFileRequested += (_, payload) => request = payload;

        vm.Results.First(r => r.Kind == HashAlgorithmKind.Sha1).SaveCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Equal("SHA1.txt", request!.DefaultName);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", Assert.IsType<string>(request.Content));
    }

    [Fact]
    public async Task UpdateLocalizer_ReprojectsByteLengthAndVerifyText()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());
        vm.Initialize(en);
        vm.UpdateInputText("abc");
        await WaitUntilAsync(() => vm.IsResultsVisible);
        vm.VerifyInput = "900150983cd24fb0d6963f7d28e17f72";
        var englishByteLength = vm.ByteLengthText;
        var englishVerify = vm.VerifyResultText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishByteLength, vm.ByteLengthText);
        Assert.NotEqual(englishVerify, vm.VerifyResultText);
    }

    [Fact]
    public async Task LocaleChanged_ReprojectsTooLargeFileStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService
        {
            FileFactory = (_, _, _) => throw new HashFileTooLargeException(HashGeneratorService.MaxFileSizeBytes + 1, HashGeneratorService.MaxFileSizeBytes),
        });
        vm.Initialize(localizer);
        vm.HashFileCommand.Execute("large.bin");
        await WaitUntilAsync(() => !vm.IsFileHashing && vm.IsEmptyStateVisible);
        var englishStatus = vm.FileStatusText;

        await localizer.SwitchLocaleAsync("fr");

        Assert.NotEqual(englishStatus, vm.FileStatusText);
        Assert.Contains(FileSize.Format(HashGeneratorService.MaxFileSizeBytes), vm.FileStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearFileCommand_ResetsState()
    {
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.HashFileCommand.Execute("example.txt");
        await WaitUntilAsync(() => !vm.IsFileHashing && vm.IsResultsVisible);

        vm.ClearFileCommand.Execute(null);

        Assert.False(vm.IsFileMode);
        Assert.True(vm.IsEmptyStateVisible);
        Assert.Equal(string.Empty, vm.FileStatusText);
        Assert.All(vm.Results, row => Assert.Equal(string.Empty, row.HashValue));
    }

    [Fact]
    public async Task Dispose_UnsubscribesLocaleChanged()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new HashGeneratorViewModel(new FakeHashGeneratorService());
        vm.Initialize(localizer);
        vm.UpdateInputText("abc");
        await WaitUntilAsync(() => vm.IsResultsVisible);
        var byteLength = vm.ByteLengthText;

        vm.Dispose();
        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(byteLength, vm.ByteLengthText);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var timeoutAt = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 > timeoutAt)
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeHashGeneratorService : IHashGeneratorService
    {
        public Func<string, CancellationToken, Task<IReadOnlyDictionary<HashAlgorithmKind, string>>> TextFactory { get; set; } =
            (text, _) => Task.FromResult<IReadOnlyDictionary<HashAlgorithmKind, string>>(CreateDefaultHashes());

        public Func<string, IProgress<double>?, CancellationToken, Task<HashFileResult>> FileFactory { get; set; } =
            (path, progress, _) =>
            {
                progress?.Report(100d);
                return Task.FromResult(new HashFileResult(CreateDefaultHashes(), 1234));
            };

        public Task<IReadOnlyDictionary<HashAlgorithmKind, string>> ComputeTextHashesAsync(string text, CancellationToken ct)
            => TextFactory(text, ct);

        public Task<HashFileResult> ComputeFileHashesAsync(string filePath, IProgress<double>? progress, CancellationToken ct)
            => FileFactory(filePath, progress, ct);

        private static IReadOnlyDictionary<HashAlgorithmKind, string> CreateDefaultHashes()
        {
            var hashes = new Dictionary<HashAlgorithmKind, string>
            {
                [HashAlgorithmKind.Md5] = "900150983cd24fb0d6963f7d28e17f72",
                [HashAlgorithmKind.Sha1] = "a9993e364706816aba3e25717850c26c9cd0d89d",
                [HashAlgorithmKind.Sha256] = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                [HashAlgorithmKind.Sha384] = "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7",
                [HashAlgorithmKind.Sha512] = "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
            };

            if (HashAlgorithmCatalog.IsSupported(HashAlgorithmKind.Sha3_256))
            {
                hashes[HashAlgorithmKind.Sha3_256] = "3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532";
            }

            return hashes;
        }
    }
}
