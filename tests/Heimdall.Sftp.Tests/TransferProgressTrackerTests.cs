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

using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Unit tests for <see cref="TransferProgressTracker"/>: the batch fraction only moves forward
/// across files, the throughput is smoothed, and the estimate stays absent until it is meaningful.
/// A manual clock drives the arithmetic without waiting.
/// </summary>
public sealed class TransferProgressTrackerTests
{
    private const long Kilobyte = 1024;

    [Fact]
    public void Fraction_AcrossTwoFiles_DoesNotRestartAtTheSecondFile()
    {
        TransferProgressTracker tracker = new(new ManualTimeProvider());
        tracker.Begin(totalBytes: 2000, totalFiles: 2);

        tracker.BeginFile("a", 1000);
        tracker.Report(1000);
        tracker.CompleteFile();
        double afterFirst = tracker.Fraction!.Value;
        tracker.BeginFile("b", 1000);
        tracker.Report(250);

        Assert.Equal(0.5, afterFirst, precision: 6);
        Assert.Equal(0.625, tracker.Fraction!.Value, precision: 6);
        Assert.Equal(2, tracker.CurrentFilePosition);
        Assert.Equal(1250, tracker.DoneBytes);
    }

    [Fact]
    public void Fraction_NoBytesPlannedButFiles_FallsBackToFileCount()
    {
        TransferProgressTracker tracker = new(new ManualTimeProvider());
        tracker.Begin(totalBytes: 0, totalFiles: 4);

        tracker.BeginFile("empty", 0);
        tracker.CompleteFile();

        Assert.Equal(0.25, tracker.Fraction!.Value, precision: 6);
    }

    [Fact]
    public void Fraction_NothingPlanned_IsIndeterminate()
    {
        TransferProgressTracker tracker = new(new ManualTimeProvider());
        tracker.Begin(totalBytes: 0, totalFiles: 0);

        Assert.Null(tracker.Fraction);
        Assert.Null(tracker.Remaining);
    }

    [Fact]
    public void Report_BeyondTheFileSize_IsClampedSoTheBarNeverOvershoots()
    {
        TransferProgressTracker tracker = new(new ManualTimeProvider());
        tracker.Begin(totalBytes: 100, totalFiles: 1);
        tracker.BeginFile("a", 100);

        tracker.Report(5000);

        Assert.Equal(1.0, tracker.Fraction!.Value, precision: 6);
        Assert.Equal(100, tracker.DoneBytes);
    }

    /// <summary>
    /// A file the listing gave as empty but that streamed bytes (a file still being written, a
    /// pseudo-file) keeps those bytes when it completes: the bar does not step back.
    /// </summary>
    [Fact]
    public void CompleteFile_AFileListedSmallerThanItsBytes_KeepsTheBytesItReported()
    {
        TransferProgressTracker tracker = new(new ManualTimeProvider());
        tracker.Begin(totalBytes: 1000, totalFiles: 2);
        tracker.BeginFile("growing.log", 0);
        tracker.Report(300);
        long beforeCompletion = tracker.DoneBytes;

        tracker.CompleteFile();

        Assert.Equal(300, beforeCompletion);
        Assert.Equal(300, tracker.DoneBytes);
        Assert.Equal(0.3, tracker.Fraction!.Value, precision: 6);
    }

    [Fact]
    public void Throughput_FromEvenlySpacedSamples_MatchesTheRateAndGivesTheRemainingTime()
    {
        ManualTimeProvider clock = new();
        TransferProgressTracker tracker = new(clock);
        tracker.Begin(totalBytes: 100 * Kilobyte, totalFiles: 1);
        tracker.BeginFile("a", 100 * Kilobyte);

        tracker.Report(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Report(10 * Kilobyte);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Report(20 * Kilobyte);

        Assert.Equal(10 * Kilobyte, tracker.BytesPerSecond, precision: 3);
        TimeSpan remaining = tracker.Remaining!.Value;
        Assert.Equal(8, remaining.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Throughput_SamplesCloserThanTheMinimumInterval_AreFoldedIntoTheNextOne()
    {
        ManualTimeProvider clock = new();
        TransferProgressTracker tracker = new(clock);
        tracker.Begin(totalBytes: 100 * Kilobyte, totalFiles: 1);
        tracker.BeginFile("a", 100 * Kilobyte);

        tracker.Report(0);
        clock.Advance(TransferProgressTracker.MinimumSampleInterval / 5);
        tracker.Report(50 * Kilobyte);

        Assert.Equal(0, tracker.BytesPerSecond);
        Assert.Null(tracker.Remaining);
    }

    [Fact]
    public void Throughput_ASuddenSlowdown_IsSmoothedRatherThanAdoptedAtOnce()
    {
        ManualTimeProvider clock = new();
        TransferProgressTracker tracker = new(clock);
        tracker.Begin(totalBytes: 1000 * Kilobyte, totalFiles: 1);
        tracker.BeginFile("a", 1000 * Kilobyte);

        tracker.Report(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Report(100 * Kilobyte);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Report(101 * Kilobyte);

        double expected = (TransferProgressTracker.SmoothingFactor * Kilobyte)
            + ((1 - TransferProgressTracker.SmoothingFactor) * 100 * Kilobyte);
        Assert.Equal(expected, tracker.BytesPerSecond, precision: 3);
    }

    [Fact]
    public void Begin_ResetsTheBatch()
    {
        ManualTimeProvider clock = new();
        TransferProgressTracker tracker = new(clock);
        tracker.Begin(totalBytes: 10, totalFiles: 1);
        tracker.BeginFile("a", 10);
        tracker.Report(10);
        tracker.CompleteFile();

        tracker.Begin(totalBytes: 20, totalFiles: 2);

        Assert.Equal(0, tracker.DoneBytes);
        Assert.Equal(0.0, tracker.Fraction!.Value, precision: 6);
        Assert.Equal(1, tracker.CurrentFilePosition);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan span) => _ticks += span.Ticks;
    }
}
