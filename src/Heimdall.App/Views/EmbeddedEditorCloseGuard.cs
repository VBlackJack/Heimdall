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

using Heimdall.App.Services;

namespace Heimdall.App.Views;

/// <summary>Locale keys the local editor's close guard surfaces.</summary>
public static class EditorCloseGuardLocaleKeys
{
    public const string Title = "EditorUnsavedTitle";

    public const string UnsavedMessage = "EditorUnsavedMessage";
}

/// <summary>
/// What a local editor pane has to protect when someone tries to close it: unsaved text.
/// </summary>
/// <remarks>
/// The inline SFTP editor is covered by its pane's guard. The LOCAL editor pane, the one the
/// local file browser swaps in, implemented no guard at all, and the arbiter skips every host
/// that is not one: the tab, the split pane and the floating window closed without a question
/// and the unsaved text was gone. Only the overlay's own Close button asked. WPF-free, like
/// <see cref="EmbeddedSftpCloseGuard"/>, so the decision is pinned by a test.
/// </remarks>
public sealed class EmbeddedEditorCloseGuard : ICloseGuard
{
    private readonly Func<EditorCloseGuardSnapshot> _sample;
    private readonly Func<string, string, Task<bool>> _confirmAsync;

    public EmbeddedEditorCloseGuard(
        Func<EditorCloseGuardSnapshot> sample,
        Func<string, string, Task<bool>> confirmAsync)
    {
        _sample = sample ?? throw new ArgumentNullException(nameof(sample));
        _confirmAsync = confirmAsync ?? throw new ArgumentNullException(nameof(confirmAsync));
    }

    public CloseGuardState SampleCloseGuardState()
    {
        EditorCloseGuardSnapshot snapshot = _sample();
        return new CloseGuardState(snapshot.IsBusy, snapshot.Epoch);
    }

    public CloseDecision PollClose(CloseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        EditorCloseGuardSnapshot snapshot = _sample();
        return snapshot.HasUnsavedChanges
            ? CloseDecision.Defer(EditorCloseGuardLocaleKeys.UnsavedMessage, snapshot.Epoch)
            : CloseDecision.Allow(snapshot.Epoch);
    }

    public async Task<bool> ResolveCloseAsync(CloseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        EditorCloseGuardSnapshot snapshot = _sample();
        if (!snapshot.IsBusy)
        {
            return true;
        }

        return await _confirmAsync(
            EditorCloseGuardLocaleKeys.Title,
            EditorCloseGuardLocaleKeys.UnsavedMessage).ConfigureAwait(true);
    }
}

/// <summary>One atomic read of what a local editor pane protects.</summary>
/// <param name="HasUnsavedChanges">The editor holds text that was never saved.</param>
/// <param name="Epoch">Change stamp over the flag; only equality across the two phases matters.</param>
public readonly record struct EditorCloseGuardSnapshot(bool HasUnsavedChanges, long Epoch)
{
    public bool IsBusy => HasUnsavedChanges;
}
