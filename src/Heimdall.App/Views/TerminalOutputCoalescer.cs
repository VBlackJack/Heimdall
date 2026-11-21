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


using System.Buffers;

namespace Heimdall.App.Views;

/// <summary>
/// Collapses the many small output chunks that producer threads append into a few "data:" web
/// messages posted on the UI thread. The first append after a drain schedules one drain; every
/// chunk that arrives before it runs rides along, so a flood of reads costs one post per slice
/// instead of one post (and one dispatcher hop) per read.
/// </summary>
internal sealed class TerminalOutputCoalescer
{
    /// <summary>Message prefix the page recognises as raw terminal output.</summary>
    internal const string DataPrefix = "data:";

    /// <summary>Largest raw slice turned into one message (256 KB).</summary>
    internal const int DefaultMaxRawBytesPerMessage = 256 * 1024;

    private const int InitialBufferBytes = 16 * 1024;
    private const int Base64BytesPerGroup = 3;
    private const int Base64CharsPerGroup = 4;

    private readonly object _gate = new();
    private readonly Action<Action> _scheduleOnUi;
    private readonly Action<string> _postData;
    private readonly int _maxRawBytesPerMessage;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferBytes);
    private int _length;
    private bool _flushScheduled;
    private bool _closed;

    public TerminalOutputCoalescer(
        Action<Action> scheduleOnUi,
        Action<string> postData,
        int maxRawBytesPerMessage = DefaultMaxRawBytesPerMessage)
    {
        ArgumentNullException.ThrowIfNull(scheduleOnUi);
        ArgumentNullException.ThrowIfNull(postData);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxRawBytesPerMessage, 0);

        _scheduleOnUi = scheduleOnUi;
        _postData = postData;
        _maxRawBytesPerMessage = maxRawBytesPerMessage;
    }

    /// <summary>Queues raw bytes; safe from any thread. Does nothing once closed.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        bool schedule = false;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            EnsureCapacityLocked(_length + data.Length);
            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;

            if (!_flushScheduled)
            {
                _flushScheduled = true;
                schedule = true;
            }
        }

        if (schedule)
        {
            _scheduleOnUi(Drain);
        }
    }

    /// <summary>Posts everything queued so far, in order, as messages of bounded size.</summary>
    public void Drain()
    {
        byte[] taken;
        int length;
        lock (_gate)
        {
            _flushScheduled = false;
            if (_closed || _length == 0)
            {
                return;
            }

            taken = _buffer;
            length = _length;
            _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferBytes);
            _length = 0;
        }

        try
        {
            for (int offset = 0; offset < length; offset += _maxRawBytesPerMessage)
            {
                int sliceLength = Math.Min(_maxRawBytesPerMessage, length - offset);
                _postData(BuildMessage(taken.AsSpan(offset, sliceLength)));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(taken);
        }
    }

    /// <summary>Drops queued bytes and refuses later ones; a scheduled drain then does nothing.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _length = 0;
        }
    }

    internal static string BuildMessage(ReadOnlySpan<byte> raw)
    {
        int base64Length = (raw.Length + Base64BytesPerGroup - 1) / Base64BytesPerGroup * Base64CharsPerGroup;
        return string.Create(
            DataPrefix.Length + base64Length,
            raw,
            static (span, bytes) =>
            {
                DataPrefix.AsSpan().CopyTo(span);
                Convert.TryToBase64Chars(bytes, span[DataPrefix.Length..], out _);
            });
    }

    private void EnsureCapacityLocked(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        int newSize = Math.Max(required, _buffer.Length * 2);
        byte[] larger = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _length).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }
}
