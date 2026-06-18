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

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Service for displaying modal dialogs to the user.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows an informational message dialog.
    /// </summary>
    /// <param name="message">The message to display</param>
    /// <param name="title">The dialog title</param>
    void ShowInfo(string message, string title);

    /// <summary>
    /// Shows a success message dialog.
    /// </summary>
    /// <param name="message">The message to display</param>
    /// <param name="title">The dialog title</param>
    void ShowSuccess(string message, string title);

    /// <summary>
    /// Shows a warning message dialog.
    /// </summary>
    /// <param name="message">The message to display</param>
    /// <param name="title">The dialog title</param>
    void ShowWarning(string message, string title);

    /// <summary>
    /// Shows an error message dialog.
    /// </summary>
    /// <param name="message">The message to display</param>
    /// <param name="title">The dialog title</param>
    void ShowError(string message, string title);

    /// <summary>
    /// Shows a question dialog with Yes/No buttons.
    /// </summary>
    /// <param name="message">The question to ask</param>
    /// <param name="title">The dialog title</param>
    /// <returns>True if user clicked Yes, false if user clicked No</returns>
    bool ShowQuestion(string message, string title);

    /// <summary>
    /// Shows a file save dialog.
    /// </summary>
    /// <param name="filter">File filter (e.g., "JSON files (*.json)|*.json|All files (*.*)|*.*")</param>
    /// <param name="defaultExtension">Default file extension (e.g., ".json")</param>
    /// <param name="defaultFileName">Default file name</param>
    /// <returns>Selected file path, or null if cancelled</returns>
    string? ShowSaveFileDialog(string filter, string defaultExtension, string defaultFileName);

    /// <summary>
    /// Shows a file open dialog.
    /// </summary>
    /// <param name="filter">File filter (e.g., "JSON files (*.json)|*.json|All files (*.*)|*.*")</param>
    /// <param name="defaultExtension">Default file extension (e.g., ".json")</param>
    /// <returns>Selected file path, or null if cancelled</returns>
    string? ShowOpenFileDialog(string filter, string defaultExtension);

    /// <summary>
    /// Shows a folder browser dialog.
    /// </summary>
    /// <param name="description">Description shown in the dialog</param>
    /// <param name="initialPath">Initial folder path to display</param>
    /// <returns>Selected folder path, or null if cancelled</returns>
    string? ShowFolderBrowserDialog(string description, string? initialPath = null);
}
