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

using System.Globalization;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Background timer service that checks every 60 seconds whether any
/// scheduled connection task is due and fires a callback to trigger it.
/// </summary>
public sealed class TaskSchedulerService : IDisposable
{
    private const int TickIntervalMs = 60_000;
    private const string ScheduleIntervalFormat = "Every {0} min";
    private const string ScheduleDailyFormat = "Daily {0}";

    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _tickGuard = new(1, 1);
    private readonly IUiDispatcher _uiDispatcher;
    private bool _disposed;

    /// <summary>
    /// Callback invoked on the UI thread when a task is due.
    /// Parameters: (ScheduledTaskDto task).
    /// </summary>
    public Func<ScheduledTaskDto, Task>? TaskDueCallback { get; set; }

    /// <summary>
    /// Callback invoked after a task has been triggered so the caller can persist state.
    /// </summary>
    public Func<Task>? PersistCallback { get; set; }

    /// <summary>
    /// Provides the current list of scheduled tasks to evaluate.
    /// </summary>
    public Func<IReadOnlyList<ScheduledTaskDto>>? TasksProvider { get; set; }

    public TaskSchedulerService(IUiDispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _timer = new System.Threading.Timer(OnTick, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    /// <summary>
    /// Starts the scheduler timer. Safe to call multiple times.
    /// </summary>
    public void Start()
    {
        _timer.Change(TickIntervalMs, TickIntervalMs);
        FileLogger.Info("TaskSchedulerService started.");
    }

    /// <summary>
    /// Stops the scheduler timer.
    /// </summary>
    public void Stop()
    {
        _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        FileLogger.Info("TaskSchedulerService stopped.");
    }

    private void OnTick(object? state)
    {
        _ = OnTickAsync();
    }

    private async Task OnTickAsync()
    {
        if (_disposed)
        {
            return;
        }

        // Skip this tick if the previous one is still running
        if (!await _tickGuard.WaitAsync(0))
        {
            return;
        }

        try
        {
            var tasks = TasksProvider?.Invoke();
            if (tasks is null || tasks.Count == 0)
            {
                return;
            }

            var now = DateTime.Now;

            foreach (var task in tasks)
            {
                if (!task.Enabled)
                {
                    continue;
                }

                if (task.NextRun is null)
                {
                    ComputeNextRun(task, now);
                    continue;
                }

                if (now >= task.NextRun)
                {
                    FileLogger.Info($"Scheduled task '{task.ServerName}' (id={task.Id}) is due at {task.NextRun:yyyy-MM-dd HH:mm}.");

                    task.LastRun = now;
                    ComputeNextRun(task, now);

                    try
                    {
                        if (TaskDueCallback is not null)
                        {
                            // Awaited fully - tick guard releases only after persistence completes.
                            await _uiDispatcher.InvokeAsync(async () =>
                            {
                                try
                                {
                                    await TaskDueCallback(task);
                                }
                                catch (Exception ex)
                                {
                                    FileLogger.Error($"Scheduled task callback failed for '{task.ServerName}': {ex.Message}");
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Error($"Scheduled task dispatch failed for '{task.ServerName}': {ex.Message}");
                    }

                    // Persist updated LastRun/NextRun (fully await before releasing tick guard)
                    try
                    {
                        if (PersistCallback is not null)
                        {
                            // Awaited fully - tick guard releases only after persistence completes.
                            await _uiDispatcher.InvokeAsync(async () =>
                            {
                                await PersistCallback();
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Error($"Failed to persist scheduled task state: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"TaskSchedulerService tick error: {ex.Message}");
        }
        finally
        {
            _tickGuard.Release();
        }
    }

    /// <summary>
    /// Computes the next run time based on the schedule type and updates the DTO.
    /// </summary>
    public static void ComputeNextRun(ScheduledTaskDto task, DateTime fromTime)
    {
        if (string.Equals(task.ScheduleType, nameof(ScheduleType.Interval), StringComparison.OrdinalIgnoreCase))
        {
            var interval = task.IntervalMinutes > 0 ? task.IntervalMinutes : 60;
            task.NextRun = fromTime.AddMinutes(interval);
            task.Schedule = string.Format(CultureInfo.CurrentCulture, ScheduleIntervalFormat, interval);
        }
        else
        {
            // Daily schedule
            if (TimeSpan.TryParseExact(task.TimeOfDay, "hh\\:mm", CultureInfo.InvariantCulture, out var timeOfDay)
                || TimeSpan.TryParse(task.TimeOfDay, CultureInfo.InvariantCulture, out timeOfDay))
            {
                var candidate = fromTime.Date.Add(timeOfDay);

                // If the candidate is in the past (or within 1 minute of now), schedule for tomorrow
                if (candidate <= fromTime.AddMinutes(1))
                {
                    candidate = candidate.AddDays(1);
                }

                task.NextRun = candidate;
                task.Schedule = string.Format(CultureInfo.CurrentCulture, ScheduleDailyFormat, task.TimeOfDay);
            }
            else
            {
                // Fallback: schedule for next day at 08:00
                task.NextRun = fromTime.Date.AddDays(1).AddHours(8);
                task.Schedule = string.Format(CultureInfo.CurrentCulture, ScheduleDailyFormat, "08:00");
                FileLogger.Warn($"Invalid TimeOfDay '{task.TimeOfDay}' for task '{task.ServerName}', defaulting to 08:00.");
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
        _timer.Dispose();
        _tickGuard.Dispose();
    }
}
