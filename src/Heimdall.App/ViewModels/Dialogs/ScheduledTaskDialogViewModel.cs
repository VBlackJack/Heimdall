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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the scheduled task add/edit dialog.
/// Provides structured form fields for server selection, schedule type, and timing.
/// </summary>
public partial class ScheduledTaskDialogViewModel : ObservableValidator
{
    /// <summary>
    /// Localizer for translating validation error messages. Set by the dialog service.
    /// </summary>
    public LocalizationManager? Localizer { get; set; }

    // --- Dialog state ---

    [ObservableProperty]
    private string _dialogTitle = "";

    [ObservableProperty]
    private bool _isEditMode;

    /// <summary>
    /// Preserved task ID when editing an existing task.
    /// </summary>
    public string? ExistingTaskId { get; set; }

    /// <summary>
    /// Preserved last run timestamp when editing an existing task.
    /// </summary>
    public DateTime? ExistingLastRun { get; set; }

    /// <summary>
    /// Preserved next run timestamp when editing an existing task.
    /// </summary>
    public DateTime? ExistingNextRun { get; set; }

    // --- Server selection ---

    /// <summary>
    /// Available servers for selection in the dialog.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ServerOption> _availableServers = [];

    [ObservableProperty]
    private ServerOption? _selectedServer;

    // --- Schedule fields ---

    /// <summary>
    /// Schedule type: "Daily" or "Interval".
    /// </summary>
    [ObservableProperty]
    private string _scheduleType = "Daily";

    /// <summary>
    /// Time of day for Daily schedule (HH:mm format).
    /// </summary>
    [ObservableProperty]
    private string _timeOfDay = "08:00";

    /// <summary>
    /// Interval in minutes for Interval schedule.
    /// </summary>
    [ObservableProperty]
    private string _intervalMinutes = "30";

    /// <summary>
    /// Whether the task is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    // --- Next run preview ---

    /// <summary>
    /// Read-only preview of when the task will next execute, based on current schedule fields.
    /// </summary>
    [ObservableProperty]
    private string? _nextRunPreview;

    // --- Computed visibility ---

    /// <summary>True when the schedule type is Daily.</summary>
    public bool IsDailySchedule => string.Equals(ScheduleType, "Daily", StringComparison.Ordinal);

    /// <summary>True when the schedule type is Interval.</summary>
    public bool IsIntervalSchedule => string.Equals(ScheduleType, "Interval", StringComparison.Ordinal);

    partial void OnScheduleTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsDailySchedule));
        OnPropertyChanged(nameof(IsIntervalSchedule));
        UpdateNextRunPreview();

        if (ValidationError is not null)
        {
            Validate();
        }
    }

    // --- Dirty state tracking ---

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    /// Suppresses dirty tracking during initialization (e.g., FromDto).
    /// </summary>
    private bool _isInitializing;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_isInitializing) return;

        // Mark dirty when any user-editable property changes
        if (e.PropertyName is nameof(SelectedServer) or nameof(ScheduleType)
            or nameof(TimeOfDay) or nameof(IntervalMinutes) or nameof(IsEnabled))
        {
            IsDirty = true;
        }
    }

    // --- Validation ---

    [ObservableProperty]
    private string? _validationError;

    /// <summary>
    /// Triggers full validation of all fields.
    /// </summary>
    [RelayCommand]
    private void Validate()
    {
        ValidationError = GetValidationError();
    }

    // --- Live re-validation ---

    partial void OnSelectedServerChanged(ServerOption? value)
    {
        if (ValidationError is not null)
        {
            Validate();
        }
    }

    partial void OnTimeOfDayChanged(string value)
    {
        UpdateNextRunPreview();

        if (ValidationError is not null)
        {
            Validate();
        }
    }

    partial void OnIntervalMinutesChanged(string value)
    {
        UpdateNextRunPreview();

        if (ValidationError is not null)
        {
            Validate();
        }
    }

    /// <summary>
    /// Public entry point for refreshing the next-run preview after the Localizer is assigned.
    /// Called from the dialog code-behind once localization is ready.
    /// </summary>
    public void RefreshNextRunPreview() => UpdateNextRunPreview();

    /// <summary>
    /// Recomputes the <see cref="NextRunPreview"/> text based on the current schedule fields.
    /// </summary>
    private void UpdateNextRunPreview()
    {
        if (IsDailySchedule)
        {
            if (!string.IsNullOrWhiteSpace(TimeOfDay)
                && TimeSpan.TryParse(TimeOfDay, System.Globalization.CultureInfo.InvariantCulture, out var ts)
                && ts >= TimeSpan.Zero
                && ts.TotalHours < 24)
            {
                var now = DateTime.Now;
                var candidate = now.Date.Add(ts);
                if (candidate <= now.AddMinutes(1))
                {
                    candidate = candidate.AddDays(1);
                }

                var dayLabel = candidate.Date == now.Date
                    ? (Localizer?["ScheduledTaskNextRunToday"] ?? "today")
                    : (Localizer?["ScheduledTaskNextRunTomorrow"] ?? "tomorrow");

                var timeLabel = ts.ToString(@"hh\:mm");

                NextRunPreview = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Localizer?["ScheduledTaskNextRunDaily"] ?? "Next execution: {0} at {1}",
                    dayLabel,
                    timeLabel);
            }
            else
            {
                NextRunPreview = null;
            }
        }
        else if (IsIntervalSchedule)
        {
            if (!string.IsNullOrWhiteSpace(IntervalMinutes)
                && int.TryParse(IntervalMinutes, out var minutes)
                && minutes > 0)
            {
                NextRunPreview = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Localizer?["ScheduledTaskNextRunInterval"] ?? "Next execution: in {0} minutes (every {1} min)",
                    minutes,
                    minutes);
            }
            else
            {
                NextRunPreview = null;
            }
        }
        else
        {
            NextRunPreview = null;
        }
    }

    /// <summary>
    /// Custom validation logic for the structured form fields.
    /// </summary>
    private string? GetValidationError()
    {
        // Server is required
        if (SelectedServer is null)
        {
            return Localizer?["ValidationScheduledTaskServerRequired"]
                ?? "Please select a server.";
        }

        if (IsDailySchedule)
        {
            // Validate HH:mm time format
            if (string.IsNullOrWhiteSpace(TimeOfDay)
                || !TimeSpan.TryParse(TimeOfDay, System.Globalization.CultureInfo.InvariantCulture, out var ts)
                || ts.TotalHours >= 24
                || ts < TimeSpan.Zero)
            {
                return Localizer?["ValidationScheduledTaskTimeInvalid"]
                    ?? "Invalid time format. Use HH:mm (24-hour).";
            }
        }
        else if (IsIntervalSchedule)
        {
            // Validate interval is a positive integer
            if (string.IsNullOrWhiteSpace(IntervalMinutes)
                || !int.TryParse(IntervalMinutes, out var minutes)
                || minutes <= 0)
            {
                return Localizer?["ValidationScheduledTaskIntervalInvalid"]
                    ?? "Interval must be a positive number of minutes.";
            }
        }

        return null;
    }

    /// <summary>
    /// Maps the current ViewModel state to a flat DTO for persistence.
    /// </summary>
    public ScheduledTaskDto ToDto()
    {
        var dto = new ScheduledTaskDto
        {
            Id = ExistingTaskId ?? Guid.NewGuid().ToString(),
            ServerId = SelectedServer?.Id ?? string.Empty,
            ServerName = SelectedServer?.Name ?? string.Empty,
            ConnectionType = SelectedServer?.ConnectionType ?? "SSH",
            Enabled = IsEnabled,
            LastRun = ExistingLastRun,
            NextRun = ExistingNextRun
        };

        if (IsDailySchedule)
        {
            dto.ScheduleType = nameof(Core.Models.ScheduleType.Daily);
            dto.TimeOfDay = TimeOfDay;
        }
        else
        {
            var minutes = int.TryParse(IntervalMinutes, out var m) ? m : 30;
            dto.ScheduleType = nameof(Core.Models.ScheduleType.Interval);
            dto.IntervalMinutes = minutes;
        }

        return dto;
    }

    /// <summary>
    /// Creates a ViewModel pre-populated from an existing DTO (for edit mode).
    /// </summary>
    /// <param name="dto">The scheduled task DTO to load values from.</param>
    /// <returns>A populated ScheduledTaskDialogViewModel in edit mode.</returns>
    public static ScheduledTaskDialogViewModel FromDto(ScheduledTaskDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var vm = new ScheduledTaskDialogViewModel { _isInitializing = true };
        vm.IsEditMode = true;
        vm.ExistingTaskId = dto.Id;
        vm.ExistingLastRun = dto.LastRun;
        vm.ExistingNextRun = dto.NextRun;
        vm.IsEnabled = dto.Enabled;

        if (string.Equals(dto.ScheduleType, nameof(Core.Models.ScheduleType.Interval), StringComparison.OrdinalIgnoreCase))
        {
            vm.ScheduleType = "Interval";
            vm.IntervalMinutes = dto.IntervalMinutes > 0 ? dto.IntervalMinutes.ToString() : "30";
        }
        else
        {
            vm.ScheduleType = "Daily";
            vm.TimeOfDay = dto.TimeOfDay ?? "08:00";
        }

        vm._isInitializing = false;
        return vm;
    }

    /// <summary>
    /// Sets the selected server by matching on server ID after the available servers list is populated.
    /// Called during edit mode initialization.
    /// </summary>
    /// <param name="serverId">The server ID to select.</param>
    public void SelectServerById(string serverId)
    {
        _isInitializing = true;
        SelectedServer = AvailableServers.FirstOrDefault(
            s => string.Equals(s.Id, serverId, StringComparison.Ordinal));
        _isInitializing = false;
    }
}

/// <summary>
/// Represents a server option in the scheduled task dialog's server dropdown.
/// </summary>
/// <param name="Id">Server inventory ID.</param>
/// <param name="Name">Raw server display name (for persistence).</param>
/// <param name="DisplayText">User-visible server name with protocol badge.</param>
/// <param name="ConnectionType">Protocol type (RDP, SSH, etc.).</param>
public sealed record ServerOption(string Id, string Name, string DisplayText, string ConnectionType);

/// <summary>
/// Immutable result returned by the scheduled task dialog on close.
/// </summary>
/// <param name="Task">The scheduled task DTO with user-entered values.</param>
/// <param name="Saved">True if the user clicked Save, false if cancelled.</param>
public sealed record ScheduledTaskDialogResult(ScheduledTaskDto Task, bool Saved);
