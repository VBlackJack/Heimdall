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

namespace Heimdall.App.Services;

/// <inheritdoc/>
public sealed class RemoteClipboardService : IRemoteClipboardService
{
    private readonly object _sync = new();
    private SftpClipboardContent? _current;

    /// <inheritdoc/>
    public event Action? Changed;

    /// <inheritdoc/>
    public SftpClipboardContent? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc/>
    public void Set(SftpClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var snapshot = content with { Entries = [.. content.Entries] };
        lock (_sync)
        {
            _current = snapshot;
        }

        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_sync)
        {
            _current = null;
        }

        Changed?.Invoke();
    }
}
