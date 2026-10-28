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

namespace Heimdall.Core.Configuration;

/// <summary>
/// Flat DTO for scheduled task JSON serialization.
/// Persisted inside <see cref="AppSettings.ScheduledTasks"/>.
/// </summary>
public sealed class ScheduledTaskDto
{
    public string Id { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string ConnectionType { get; set; } = "SSH";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Legacy schedule text written by earlier versions in English (e.g., "Daily 08:00").
    /// No longer written and never shown while <see cref="ScheduleType"/>, <see cref="TimeOfDay"/>
    /// and <see cref="IntervalMinutes"/> describe the schedule: the grid formats those at display
    /// time in the current language. Still read, so a file saved by an earlier version loads.
    /// </summary>
    public string Schedule { get; set; } = string.Empty;

    /// <summary>
    /// Schedule type: Daily (time-of-day) or Interval (every N minutes).
    /// </summary>
    public string ScheduleType { get; set; } = "Daily";

    /// <summary>
    /// Time of day for Daily schedule (HH:mm format, 24-hour).
    /// </summary>
    public string? TimeOfDay { get; set; }

    /// <summary>
    /// Interval in minutes for Interval schedule type.
    /// </summary>
    public int IntervalMinutes { get; set; }

    public DateTime? LastRun { get; set; }
    public DateTime? NextRun { get; set; }
}
