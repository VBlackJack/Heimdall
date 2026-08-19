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
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

public sealed class EmbeddedEditorViewModelTests
{
    [Fact]
    public async Task SaveAsync_RemoteFile_WithoutPersistenceHandler_RemainsModified()
    {
        EmbeddedEditorViewModel viewModel = new();
        viewModel.LoadContent("remote.txt");
        viewModel.NotifyTextChanged();

        bool result = await viewModel.SaveAsync("changed");

        Assert.False(result);
        Assert.True(viewModel.IsModified);
    }

    [Fact]
    public async Task SaveAsync_RemoteFile_SuccessClearsCurrentRevision()
    {
        EmbeddedEditorViewModel viewModel = new();
        viewModel.LoadContent("remote.txt");
        viewModel.NotifyTextChanged();
        viewModel.SaveRequested += (_, _) => Task.FromResult(true);

        bool result = await viewModel.SaveAsync("changed");

        Assert.True(result);
        Assert.False(viewModel.IsModified);
    }

    [Fact]
    public async Task SaveAsync_RemoteFile_AwaitsPersistenceAndPreservesNewerRevision()
    {
        TaskCompletionSource uploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseUpload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EmbeddedEditorViewModel viewModel = new();
        viewModel.LoadContent("remote.txt");
        viewModel.NotifyTextChanged();
        viewModel.SaveRequested += async (_, _) =>
        {
            uploadStarted.SetResult();
            await releaseUpload.Task;
            return true;
        };

        Task<bool> saveTask = viewModel.SaveAsync("first revision");
        await uploadStarted.Task;
        bool completedBeforePersistence = saveTask.IsCompleted;
        viewModel.NotifyTextChanged();

        releaseUpload.SetResult();
        bool result = await saveTask;

        Assert.True(result);
        Assert.False(completedBeforePersistence);
        Assert.True(viewModel.IsModified);
    }

    [Fact]
    public async Task SaveAsync_ConcurrentRemoteSaves_PersistInRevisionOrder()
    {
        TaskCompletionSource firstUploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirstUpload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondUploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> persistedContents = [];
        EmbeddedEditorViewModel viewModel = new();
        viewModel.LoadContent("remote.txt");
        viewModel.NotifyTextChanged();
        viewModel.SaveRequested += async (_, savedContent) =>
        {
            persistedContents.Add(savedContent);
            if (persistedContents.Count == 1)
            {
                firstUploadStarted.SetResult();
                await releaseFirstUpload.Task;
            }
            else
            {
                secondUploadStarted.SetResult();
            }

            return true;
        };

        Task<bool> firstSave = viewModel.SaveAsync("first revision");
        await firstUploadStarted.Task;
        viewModel.NotifyTextChanged();
        Task<bool> secondSave = viewModel.SaveAsync("second revision");

        Assert.False(secondUploadStarted.Task.IsCompleted);

        releaseFirstUpload.SetResult();
        Assert.True(await firstSave);
        Assert.True(await secondSave);
        await secondUploadStarted.Task;

        Assert.Equal(["first revision", "second revision"], persistedContents);
        Assert.False(viewModel.IsModified);
    }

    // SFTP-006. The editor used to read a local file with File.ReadAllTextAsync and write it back
    // with File.WriteAllTextAsync, which always emits UTF-8 without a byte order mark. Saving a
    // UTF-16 file therefore rewrote every byte, and a UTF-8 file lost its mark, while the visible
    // text was unchanged. These oracles compare BYTES, because that is what the defect changed.
    [Theory]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf8bom")]
    [InlineData("utf32le")]
    public async Task SaveAsync_LocalFile_RewritesTheBytesItRead(string encodingKey)
    {
        Encoding encoding = encodingKey switch
        {
            "utf16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            "utf32le" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        };

        // The payload must contain characters outside ASCII, built from explicit code points so
        // the source file stays ASCII. An ASCII-only payload encodes to the same bytes under
        // UTF-8 with and without the captured encoding, and would let a broken build pass.
        string content = "caf" + (char)0x00E9 + (char)0x000A + "na" + (char)0x00EF + "ve" + (char)0x000A;
        string path = Path.Combine(Path.GetTempPath(), $"heimdall-sftp006-{encodingKey}-{Guid.NewGuid():N}.txt");
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes(content)];
        await File.WriteAllBytesAsync(path, original);

        try
        {
            EmbeddedEditorViewModel viewModel = new();
            string? loaded = await viewModel.LoadFileAsync(path);

            Assert.Null(viewModel.LoadErrorMessage);
            Assert.Equal(content, loaded);

            Assert.True(await viewModel.SaveAsync(loaded!));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The fallback is a named decision now rather than whatever File.WriteAllTextAsync happened
    // to do. A load that failed leaves the path selected with nothing captured, which is the real
    // production route into that state, and the save must still produce UTF-8 without a byte order
    // mark so nothing regresses for a caller that never obtained a document.
    [Fact]
    public async Task SaveAsync_LocalFile_AfterAFailedLoad_WritesUtf8WithoutAByteOrderMark()
    {
        string path = Path.Combine(Path.GetTempPath(), $"heimdall-sftp006-fresh-{Guid.NewGuid():N}.txt");
        EmbeddedEditorViewModel viewModel = new();

        Assert.Null(await viewModel.LoadFileAsync(path));
        Assert.False(string.IsNullOrEmpty(viewModel.LoadErrorMessage));

        try
        {
            string payload = "r" + (char)0x00E9 + "sum" + (char)0x00E9;
            Assert.True(await viewModel.SaveAsync(payload));

            byte[] written = await File.ReadAllBytesAsync(path);
            Assert.Equal(new UTF8Encoding(false).GetBytes(payload), written);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The read is strict now, so a legacy single-byte file that is not valid UTF-8 is reported
    // instead of being transcoded into something else. That is the fail-closed direction for a
    // finding about silent byte corruption, and the file on disk is left untouched.
    [Fact]
    public async Task LoadFileAsync_UndecodableLegacyFile_ReportsInsteadOfTranscoding()
    {
        string path = Path.Combine(Path.GetTempPath(), $"heimdall-sftp006-legacy-{Guid.NewGuid():N}.txt");
        byte[] latin1 = [0x63, 0x61, 0x66, 0xE9];
        await File.WriteAllBytesAsync(path, latin1);

        try
        {
            EmbeddedEditorViewModel viewModel = new();
            string? loaded = await viewModel.LoadFileAsync(path);

            Assert.Null(loaded);
            Assert.False(string.IsNullOrEmpty(viewModel.LoadErrorMessage));
            Assert.Equal(latin1, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
