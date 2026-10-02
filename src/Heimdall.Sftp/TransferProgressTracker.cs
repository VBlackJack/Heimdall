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

namespace Heimdall.Sftp;

/// <summary>
/// Accumulates the progress of a whole batch: bytes and files done against the planned totals, a
/// smoothed throughput and the time remaining.
/// </summary>
/// <remarks>
/// <para>
/// The progress events a browser raises describe one file at a time, so a bar bound to them ran
/// from zero to a hundred percent once per file and said nothing about the batch. This tracker
/// keeps the finished files' bytes and adds the file in flight, so the fraction only moves forward.
/// </para>
/// <para>
/// The throughput is an exponential moving average over samples at least
/// <see cref="MinimumSampleInterval"/> apart: an instantaneous rate over two back-to-back buffers
/// swings wildly and makes the estimate unreadable. The clock is injected so the arithmetic is
/// testable without waiting.
/// </para>
/// </remarks>
public sealed class TransferProgressTracker
{
    /// <summary>Samples closer together than this are folded into the next one.</summary>
    public static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Weight of the newest sample in the moving average.</summary>
    public const double SmoothingFactor = 0.3;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;

    private long _totalBytes;
    private int _totalFiles;
    private long _completedBytes;
    private int _completedFiles;
    private long _currentFileBytes;
    private long _currentFileSize;
    private string _currentFileName = string.Empty;
    private double _bytesPerSecond;
    private long _lastSampleBytes;
    private long _lastSampleTimestamp;
    private bool _hasSample;

    /// <summary>Initializes a new tracker reading time from <paramref name="clock"/>.</summary>
    public TransferProgressTracker(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Starts a batch of <paramref name="totalFiles"/> files and <paramref name="totalBytes"/> bytes.</summary>
    public void Begin(long totalBytes, int totalFiles)
    {
        lock (_gate)
        {
            _totalBytes = Math.Max(0, totalBytes);
            _totalFiles = Math.Max(0, totalFiles);
            _completedBytes = 0;
            _completedFiles = 0;
            _currentFileBytes = 0;
            _currentFileSize = 0;
            _currentFileName = string.Empty;
            _bytesPerSecond = 0;
            _lastSampleBytes = 0;
            _hasSample = false;
        }
    }

    /// <summary>Marks the start of the next file of the batch.</summary>
    public void BeginFile(string fileName, long fileSize)
    {
        lock (_gate)
        {
            _currentFileName = fileName;
            _currentFileSize = Math.Max(0, fileSize);
            _currentFileBytes = 0;
        }
    }

    /// <summary>Records the bytes moved so far of the file in flight.</summary>
    public void Report(long bytesTransferredInCurrentFile)
    {
        lock (_gate)
        {
            long ceiling = _currentFileSize > 0 ? _currentFileSize : long.MaxValue;
            _currentFileBytes = Math.Clamp(bytesTransferredInCurrentFile, 0, ceiling);
            Sample(_completedBytes + _currentFileBytes);
        }
    }

    /// <summary>Marks the file in flight as finished.</summary>
    public void CompleteFile()
    {
        lock (_gate)
        {
            _completedBytes += _currentFileSize;
            _completedFiles++;
            _currentFileBytes = 0;
            Sample(_completedBytes);
        }
    }

    /// <summary>Gets the planned number of files.</summary>
    public int TotalFiles
    {
        get
        {
            lock (_gate)
            {
                return _totalFiles;
            }
        }
    }

    /// <summary>Gets the 1-based position of the file in flight, capped at the planned total.</summary>
    public int CurrentFilePosition
    {
        get
        {
            lock (_gate)
            {
                return Math.Min(_completedFiles + 1, Math.Max(_totalFiles, 1));
            }
        }
    }

    /// <summary>Gets the name of the file in flight.</summary>
    public string CurrentFileName
    {
        get
        {
            lock (_gate)
            {
                return _currentFileName;
            }
        }
    }

    /// <summary>Gets the bytes done across the whole batch.</summary>
    public long DoneBytes
    {
        get
        {
            lock (_gate)
            {
                return _completedBytes + _currentFileBytes;
            }
        }
    }

    /// <summary>Gets the planned bytes of the whole batch.</summary>
    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    /// <summary>
    /// Gets the fraction of the batch done, from zero to one, or <see langword="null"/> when nothing
    /// measurable was planned (the bar then runs indeterminate).
    /// </summary>
    public double? Fraction
    {
        get
        {
            lock (_gate)
            {
                if (_totalBytes > 0)
                {
                    return Math.Min(1.0, (double)(_completedBytes + _currentFileBytes) / _totalBytes);
                }

                return _totalFiles > 0 ? Math.Min(1.0, (double)_completedFiles / _totalFiles) : null;
            }
        }
    }

    /// <summary>Gets the smoothed throughput, or zero before the first usable sample.</summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _bytesPerSecond;
            }
        }
    }

    /// <summary>Gets the estimated time remaining, or <see langword="null"/> while it cannot be estimated.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            lock (_gate)
            {
                if (_bytesPerSecond <= 0 || _totalBytes <= 0)
                {
                    return null;
                }

                long remainingBytes = Math.Max(0, _totalBytes - (_completedBytes + _currentFileBytes));
                return TimeSpan.FromSeconds(remainingBytes / _bytesPerSecond);
            }
        }
    }

    private void Sample(long doneBytes)
    {
        long now = _clock.GetTimestamp();
        if (!_hasSample)
        {
            _hasSample = true;
            _lastSampleBytes = doneBytes;
            _lastSampleTimestamp = now;
            return;
        }

        TimeSpan elapsed = _clock.GetElapsedTime(_lastSampleTimestamp, now);
        if (elapsed < MinimumSampleInterval)
        {
            return;
        }

        double instantaneous = Math.Max(0, doneBytes - _lastSampleBytes) / elapsed.TotalSeconds;
        _bytesPerSecond = _bytesPerSecond <= 0
            ? instantaneous
            : (SmoothingFactor * instantaneous) + ((1 - SmoothingFactor) * _bytesPerSecond);
        _lastSampleBytes = doneBytes;
        _lastSampleTimestamp = now;
    }
}
