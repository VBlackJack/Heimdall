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

namespace Heimdall.Sftp;

/// <summary>Preserves entry identities in privileged listings using NUL-delimited GNU find records.</summary>
public static class SudoDirectoryListing
{
    private const int FieldCount = 7;
    private const string RecordFormat = "%y\\0%m\\0%U\\0%G\\0%s\\0%T@\\0%f\\0";

    /// <summary>Lists immediate children without following symbolic links.</summary>
    public static string Build(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string operand = path.StartsWith('/') ? path : "./" + path;
        return $"LC_ALL=C find {PathEscaper.EscapeForShell(operand)} -mindepth 1 -maxdepth 1 -printf '{RecordFormat}'";
    }

    /// <summary>Rejects malformed records and excludes names unsupported by remote operations.</summary>
    public static IReadOnlyList<SftpFileInfo> Parse(string output, string parentPath)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        string[] fields = output.Split('\0');
        if (fields[^1].Length != 0 || (fields.Length - 1) % FieldCount != 0)
        {
            throw new InvalidDataException("The privileged directory listing is incomplete or malformed.");
        }

        List<SftpFileInfo> entries = [];
        for (int index = 0; index < fields.Length - 1; index += FieldCount)
        {
            string name = fields[index + 6];
            if (!SftpPathGuard.IsValidChildName(name))
            {
                Core.Logging.FileLogger.Warn("Sudo directory listing skipped an entry with an unsafe name.");
                continue;
            }

            RemoteEntryKind kind = fields[index] switch
            {
                "f" => RemoteEntryKind.File,
                "d" => RemoteEntryKind.Directory,
                "l" => RemoteEntryKind.SymbolicLink,
                "p" => RemoteEntryKind.Fifo,
                "s" => RemoteEntryKind.Socket,
                "b" or "c" => RemoteEntryKind.Device,
                _ => RemoteEntryKind.Unknown,
            };
            int mode = Convert.ToInt32(fields[index + 1], 8);
            long size = long.Parse(fields[index + 4], CultureInfo.InvariantCulture);
            double seconds = double.Parse(fields[index + 5], CultureInfo.InvariantCulture);
            DateTime modified = DateTime.UnixEpoch.AddSeconds(seconds).ToLocalTime();
            string fullPath = parentPath.TrimEnd('/') + "/" + name;
            entries.Add(new SftpFileInfo(name, fullPath, kind, size, modified,
                FormatPermissions(fields[index][0], mode), fields[index + 2], fields[index + 3]));
        }

        return entries;
    }

    private static string FormatPermissions(char kind, int mode)
    {
        char[] text = "----------".ToCharArray();
        text[0] = kind == 'f' ? '-' : kind;
        const string bits = "rwxrwxrwx";
        for (int index = 0; index < bits.Length; index++)
        {
            if ((mode & (1 << (8 - index))) != 0) text[index + 1] = bits[index];
        }

        if ((mode & 0x800) != 0) text[3] = text[3] == 'x' ? 's' : 'S';
        if ((mode & 0x400) != 0) text[6] = text[6] == 'x' ? 's' : 'S';
        if ((mode & 0x200) != 0) text[9] = text[9] == 'x' ? 't' : 'T';
        return new string(text);
    }
}
