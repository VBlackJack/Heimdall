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
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>Doubles and builders shared by the masked copy tests.</summary>
internal static class MaskedCopyTestSupport
{
    internal const string Secret = "hunter2";

    internal static async Task<LocalizationManager> LocalizerAsync(string locale = "en")
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return localizer;
    }

    internal static SessionPaneModel Leaf(object? hostControl) => new() { HostControl = hostControl };

    internal static SplitContainerModel Split(ISplitContent first, ISplitContent second) => new()
    {
        First = first,
        Second = second,
        Orientation = SplitOrientation.Vertical
    };

    internal static SessionTabViewModel Tab(ISplitContent root) => new() { RootContent = root };

    internal sealed class FakeTerminal : ITerminalTextSource
    {
        public bool CanReadText { get; set; } = true;

        public TerminalTextReadResult Result { get; set; } =
            new("user@host$ echo password=" + Secret, false, TerminalTextReadStatus.Ok);

        public Exception? ReadFailure { get; set; }

        public int FocusCalls { get; private set; }

        public Task<TerminalTextReadResult> ReadTextAsync(CancellationToken cancellationToken) =>
            ReadFailure is null ? Task.FromResult(Result) : Task.FromException<TerminalTextReadResult>(ReadFailure);

        public void FocusTerminal() => FocusCalls++;
    }

    internal sealed class RecordingClipboard : IClipboardService
    {
        public List<string> Texts { get; } = [];

        public void SetText(string text) => Texts.Add(text);
    }
}
