/*
 * Copyright 2025 Julien Bombled
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

using TwinShell.Core.Enums;

namespace TwinShell.Core.Models;

/// <summary>
/// Represents user preferences and application settings.
/// Persisted in JSON format at %APPDATA%/TwinShell/settings.json
/// </summary>
public sealed class UserSettings
{
    /// <summary>
    /// The selected application theme (Light, Dark, or System).
    /// </summary>
    public Theme Theme { get; set; } = Theme.Light;

    /// <summary>
    /// Number of days to retain command history before automatic cleanup.
    /// Minimum value: 1 day, Default: 90 days.
    /// </summary>
    public int AutoCleanupDays { get; set; } = 90;

    /// <summary>
    /// Maximum number of items to display in history views.
    /// Default: 1000 items.
    /// </summary>
    public int MaxHistoryItems { get; set; } = 1000;

    /// <summary>
    /// Default platform filter to apply when the application starts.
    /// Null means no default filter (show all platforms).
    /// </summary>
    public Platform? DefaultPlatformFilter { get; set; } = null;

    /// <summary>
    /// Whether to show confirmation dialog before executing dangerous commands.
    /// Default: true.
    /// </summary>
    public bool ConfirmDangerousActions { get; set; } = true;

    /// <summary>
    /// Command execution timeout in seconds.
    /// Default: 30 seconds, Maximum: 300 seconds (5 minutes).
    /// </summary>
    public int ExecutionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Application language/culture code (e.g., "fr", "en", "es").
    /// Default: "fr" (French).
    /// </summary>
    public string CultureCode { get; set; } = "fr";

    /// <summary>
    /// Path to the local Git repository folder for GitOps synchronization.
    /// Used for exporting/importing YAML files.
    /// </summary>
    public string? GitRepositoryPath { get; set; }

    /// <summary>
    /// URL of the remote Git repository for team synchronization.
    /// Example: https://github.com/team/twinshell-data.git
    /// </summary>
    public string? GitRemoteUrl { get; set; }

    /// <summary>
    /// Git branch to use for synchronization (default: main).
    /// </summary>
    public string GitBranch { get; set; } = "main";

    /// <summary>
    /// Authentication method for Git: "https" (token) or "ssh".
    /// </summary>
    public string GitAuthMethod { get; set; } = "https";

    /// <summary>
    /// Personal access token for HTTPS authentication (stored encrypted).
    /// </summary>
    public string? GitAccessToken { get; set; }

    /// <summary>
    /// Enable automatic synchronization on startup.
    /// </summary>
    public bool GitSyncOnStartup { get; set; } = true;

    /// <summary>
    /// Enable automatic push after local changes (export).
    /// </summary>
    public bool GitAutoPush { get; set; } = true;

    /// <summary>
    /// Git user name for commits.
    /// </summary>
    public string? GitUserName { get; set; }

    /// <summary>
    /// Git user email for commits.
    /// </summary>
    public string? GitUserEmail { get; set; }

    /// <summary>
    /// Accessibility: Reduce motion and animations (WCAG 2.3.3).
    /// When enabled, theme transitions and other animations are disabled.
    /// </summary>
    public bool ReducedMotion { get; set; } = false;

    /// <summary>
    /// Creates a new instance with default values.
    /// </summary>
    public UserSettings()
    {
    }

    /// <summary>
    /// Creates a deep copy of the settings.
    /// </summary>
    public UserSettings Clone()
    {
        return new UserSettings
        {
            Theme = this.Theme,
            AutoCleanupDays = this.AutoCleanupDays,
            MaxHistoryItems = this.MaxHistoryItems,
            DefaultPlatformFilter = this.DefaultPlatformFilter,
            ConfirmDangerousActions = this.ConfirmDangerousActions,
            ExecutionTimeoutSeconds = this.ExecutionTimeoutSeconds,
            CultureCode = this.CultureCode,
            GitRepositoryPath = this.GitRepositoryPath,
            GitRemoteUrl = this.GitRemoteUrl,
            GitBranch = this.GitBranch,
            GitAuthMethod = this.GitAuthMethod,
            GitAccessToken = this.GitAccessToken,
            GitSyncOnStartup = this.GitSyncOnStartup,
            GitAutoPush = this.GitAutoPush,
            GitUserName = this.GitUserName,
            GitUserEmail = this.GitUserEmail,
            ReducedMotion = this.ReducedMotion
        };
    }

    /// <summary>
    /// Gets the default user settings.
    /// </summary>
    public static UserSettings Default => new UserSettings();
}
