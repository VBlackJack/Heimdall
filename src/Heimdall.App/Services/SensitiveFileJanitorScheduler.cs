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

using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

internal readonly record struct SensitiveFileJanitorSweepResult(int Removed, DateTime? NextEligibleUtc);

internal sealed class SensitiveFileJanitorScheduler : IDisposable
{
    private readonly string _janitorName;
    private readonly Func<SensitiveFileJanitorSweepResult> _sweep;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _utcNow;
    private readonly CancellationTokenSource _cancellationSource = new CancellationTokenSource();

    private Task? _loopTask;
    private bool _disposed;

    public SensitiveFileJanitorScheduler(
        string janitorName,
        Func<SensitiveFileJanitorSweepResult> sweep,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTime>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(janitorName);

        _janitorName = janitorName;
        _sweep = sweep ?? throw new ArgumentNullException(nameof(sweep));
        _delay = delay ?? ((duration, cancellationToken) => Task.Delay(duration, cancellationToken));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loopTask is not null)
        {
            throw new InvalidOperationException($"{_janitorName} scheduler has already started.");
        }

        CancellationToken cancellationToken = _cancellationSource.Token;
        _loopTask = Task.Run(() => RunAsync(cancellationToken));
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        DateTime? triggeringDeadlineUtc = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SensitiveFileJanitorSweepResult result = _sweep();
                if (result.NextEligibleUtc is not DateTime nextEligibleUtc)
                {
                    return;
                }

                if (triggeringDeadlineUtc is DateTime previousDeadlineUtc
                    && nextEligibleUtc <= previousDeadlineUtc)
                {
                    FileLogger.Warn(
                        $"[SensitiveFileJanitorScheduler] {_janitorName} stopped because its next eligible time did not advance: previous={previousDeadlineUtc:O} next={nextEligibleUtc:O}");
                    return;
                }

                TimeSpan delay = nextEligibleUtc - _utcNow();
                triggeringDeadlineUtc = nextEligibleUtc;
                if (delay > TimeSpan.Zero)
                {
                    await _delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                FileLogger.Warn(
                    $"[SensitiveFileJanitorScheduler] {_janitorName} sweep loop failed: {ex.Message}");
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellationSource.Cancel();
        _cancellationSource.Dispose();
    }
}
