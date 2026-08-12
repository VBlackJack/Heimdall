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
}
