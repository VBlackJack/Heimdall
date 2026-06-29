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

using System.Diagnostics.CodeAnalysis;

namespace Heimdall.App.Views;

internal sealed class PendingTerminalMessageBuffer
{
    private readonly object _gate = new();
    private readonly Queue<string> _messages = new();
    private readonly int _maxBytes;
    private readonly Action _onDropped;
    private int _bufferedBytes;
    private bool _dropLogged;

    public PendingTerminalMessageBuffer(int maxBytes, Action onDropped)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        ArgumentNullException.ThrowIfNull(onDropped);

        _maxBytes = maxBytes;
        _onDropped = onDropped;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count;
            }
        }
    }

    public int BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bufferedBytes;
            }
        }
    }

    public void Enqueue(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        int messageBytes = GetMessageByteCount(message);
        bool dropped = false;

        lock (_gate)
        {
            if (messageBytes > _maxBytes)
            {
                ClearLocked();
                dropped = true;
            }
            else
            {
                while (_messages.Count > 0 && _bufferedBytes + messageBytes > _maxBytes)
                {
                    string droppedMessage = _messages.Dequeue();
                    _bufferedBytes -= GetMessageByteCount(droppedMessage);
                    dropped = true;
                }

                if (_bufferedBytes + messageBytes <= _maxBytes)
                {
                    _messages.Enqueue(message);
                    _bufferedBytes += messageBytes;
                }
                else
                {
                    dropped = true;
                }
            }
        }

        if (dropped)
        {
            LogDropOnce();
        }
    }

    public bool TryDequeue([NotNullWhen(true)] out string? message)
    {
        lock (_gate)
        {
            if (_messages.Count == 0)
            {
                message = null;
                return false;
            }

            message = _messages.Dequeue();
            _bufferedBytes -= GetMessageByteCount(message);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearLocked();
        }
    }

    private void ClearLocked()
    {
        _messages.Clear();
        _bufferedBytes = 0;
    }

    private void LogDropOnce()
    {
        lock (_gate)
        {
            if (_dropLogged)
            {
                return;
            }

            _dropLogged = true;
        }

        _onDropped();
    }

    private static int GetMessageByteCount(string message)
    {
        long bytes = (long)message.Length * sizeof(char);
        return bytes > int.MaxValue ? int.MaxValue : (int)bytes;
    }
}
