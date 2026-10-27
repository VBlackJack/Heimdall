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
/// Decides which local file paths a profile or gateway read from someone else's file may keep.
/// </summary>
/// <remarks>
/// <para>A path to a network share is not a local file: merely testing whether it exists makes
/// Windows open an SMB session to the host it names and offer the user's NetNTLMv2 response to
/// it. A file written elsewhere therefore cannot be allowed to choose one, and the import
/// boundary clears such values. A path the user typed in their own dialog is not affected: this
/// predicate is applied to imported values only.</para>
/// <para>Device paths (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>) are refused with them: they bypass
/// the normalization every other check relies on, and <c>\\?\UNC\</c> is a network path
/// spelled differently.</para>
/// </remarks>
public static class ImportedPathPolicy
{
    /// <summary>The NT object-manager prefix that .NET also accepts as a device path.</summary>
    private const string NtObjectPrefix = @"\??\";

    /// <summary>Length of the two leading separators that open a UNC or device path.</summary>
    private const int LeadingSeparatorCount = 2;

    /// <summary>
    /// Whether <paramref name="path"/> is a UNC path (<c>\\server\share</c>, <c>//server/share</c>,
    /// with any mix of the two separators), a Win32 device path (<c>\\?\</c>, <c>\\.\</c>,
    /// <c>\\?\UNC\</c>) or an NT object path (<c>\??\</c>). Blank values are not.
    /// </summary>
    /// <remarks>
    /// Purely textual: it never touches the file system, so calling it on a hostile value is safe.
    /// Leading white space is ignored, since the path APIs would not all ignore it the same way.
    /// </remarks>
    public static bool IsNetworkOrDevicePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string trimmed = path.TrimStart();
        if (trimmed.Length >= LeadingSeparatorCount
            && IsSeparator(trimmed[0])
            && IsSeparator(trimmed[1]))
        {
            return true;
        }

        return trimmed.StartsWith(NtObjectPrefix, StringComparison.Ordinal);
    }

    private static bool IsSeparator(char value) => value is '\\' or '/';
}
