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

using System.Text;
using System.Text.RegularExpressions;
using Heimdall.Core.Matching;
using Heimdall.Terminal.Logging;

namespace Heimdall.Terminal.Macros;

public sealed class ExpectMatcher
{
    public const int DefaultBufferCapacity = 64 * 1024;

    public static readonly TimeSpan DefaultRegexTimeout = RegexEngine.DefaultTimeout;

    private readonly StreamingUtf8Decoder _decoder = new();
    private readonly StreamingAnsiStripper _stripper = new();
    private readonly StringBuilder _buffer = new();
    private readonly int _bufferCapacity;
    private readonly TimeSpan _regexTimeout;

    public ExpectMatcher(
        int bufferCapacity = DefaultBufferCapacity,
        TimeSpan? regexTimeout = null)
    {
        if (bufferCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferCapacity));
        }

        _bufferCapacity = bufferCapacity;
        _regexTimeout = regexTimeout ?? DefaultRegexTimeout;
    }

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        var decoded = _decoder.DecodeChunk(chunk);
        var cleanText = _stripper.Strip(decoded);
        Append(cleanText);
    }

    public bool TryMatch(string pattern, bool isRegex)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length == 0)
        {
            return true;
        }

        var bufferText = _buffer.ToString();
        if (!isRegex)
        {
            return bufferText.Contains(pattern, StringComparison.Ordinal);
        }

        var result = RegexEngine.Test(
            pattern,
            bufferText,
            RegexOptions.None,
            _regexTimeout);

        return result.Status == RegexTestStatus.Success && result.TotalMatchCount > 0;
    }

    private void Append(string cleanText)
    {
        if (cleanText.Length == 0)
        {
            return;
        }

        _buffer.Append(cleanText);
        if (_buffer.Length <= _bufferCapacity)
        {
            return;
        }

        _buffer.Remove(0, _buffer.Length - _bufferCapacity);
    }
}
