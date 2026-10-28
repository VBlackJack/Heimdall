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

using System.IO;
using System.Windows;

namespace Heimdall.App.Services;

/// <summary>
/// Clipboard abstraction for ViewModel tests.
/// </summary>
public interface IClipboardService
{
    void SetText(string text);

    /// <summary>
    /// Puts a secret on the clipboard in a way that keeps it out of the Windows clipboard
    /// history, the cloud clipboard and clipboard-monitoring tools.
    /// </summary>
    /// <remarks>
    /// The default falls back to <see cref="SetText"/> so test doubles that only record text keep
    /// compiling. An implementation that talks to the real clipboard must override it.
    /// </remarks>
    void SetSensitiveText(string text) => SetText(text);

    /// <summary>Returns the text currently on the clipboard, or null when there is none.</summary>
    string? GetText() => null;

    /// <summary>Empties the clipboard.</summary>
    void Clear()
    {
    }
}

/// <summary>
/// Builds the clipboard payload used for secrets.
/// </summary>
/// <remarks>
/// Windows reads three documented formats before it records, syncs or hands the content to
/// monitors: the first excludes the content from monitor processing, the other two are DWORD
/// flags that must be 0 to keep the content out of the history and the cloud clipboard.
/// </remarks>
internal static class SensitiveClipboardData
{
    internal const string ExcludeFromMonitorFormat = "ExcludeClipboardContentFromMonitorProcessing";
    internal const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";
    internal const string CanUploadToCloudFormat = "CanUploadToCloudClipboard";

    /// <summary>The value of the exclusion marker: only its presence matters.</summary>
    internal const int ExcludeMarkerValue = 1;

    /// <summary>The DWORD that switches history and cloud upload off.</summary>
    internal const int FlagDisabled = 0;

    internal static System.Windows.DataObject Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var data = new System.Windows.DataObject();
        data.SetText(text, System.Windows.TextDataFormat.UnicodeText);
        data.SetData(ExcludeFromMonitorFormat, Dword(ExcludeMarkerValue));
        data.SetData(CanIncludeInHistoryFormat, Dword(FlagDisabled));
        data.SetData(CanUploadToCloudFormat, Dword(FlagDisabled));
        return data;
    }

    /// <summary>Reads a DWORD back out of a payload, or null when the format is absent.</summary>
    internal static int? ReadDword(System.Windows.IDataObject data, string format)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.GetData(format) is not MemoryStream stream)
        {
            return null;
        }

        var bytes = stream.ToArray();
        return bytes.Length == sizeof(int) ? BitConverter.ToInt32(bytes, 0) : null;
    }

    private static MemoryStream Dword(int value) => new(BitConverter.GetBytes(value));
}

/// <summary>
/// WPF clipboard adapter.
/// </summary>
public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Clipboard.SetText(text);
    }

    public void SetSensitiveText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Clipboard.SetDataObject(SensitiveClipboardData.Create(text), copy: true);
    }

    public string? GetText() => Clipboard.ContainsText() ? Clipboard.GetText() : null;

    public void Clear() => Clipboard.Clear();
}
