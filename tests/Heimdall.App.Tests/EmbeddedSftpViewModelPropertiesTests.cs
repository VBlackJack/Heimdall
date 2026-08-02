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
using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class EmbeddedSftpViewModelPropertiesTests
{
    [Theory]
    [InlineData(RemoteEntryKind.SymbolicLink, "Symbolic link")]
    [InlineData(RemoteEntryKind.Fifo, "Named pipe (FIFO)")]
    [InlineData(RemoteEntryKind.Directory, "Directory")]
    public async Task ShowProperties_UsesLocalizedRemoteEntryKind(
        RemoteEntryKind kind,
        string expectedType)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

        IDialogService dialogService = DispatchProxy.Create<IDialogService, RecordingDialogProxy>();
        RecordingDialogProxy dialog = (RecordingDialogProxy)(object)dialogService;
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher());
        viewModel.SetDialogService(dialogService);
        SetLocalizer(viewModel, localizer);

        SftpFileInfo entry = new(
            "entry",
            "/srv/entry",
            kind,
            1,
            DateTime.UnixEpoch,
            "rw-r--r--",
            "1000",
            "1000");

        viewModel.ShowProperties(entry);

        string message = Assert.IsType<string>(dialog.Message);
        Assert.Contains($"Type: {expectedType}\n", message, StringComparison.Ordinal);
    }

    private static void SetLocalizer(
        EmbeddedSftpViewModel viewModel,
        LocalizationManager localizer)
    {
        FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(
            "_localizer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(viewModel, localizer);
    }

    private class RecordingDialogProxy : DispatchProxy
    {
        public string? Message { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowInfo))
            {
                Message = args?[1] as string;
                return null;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
