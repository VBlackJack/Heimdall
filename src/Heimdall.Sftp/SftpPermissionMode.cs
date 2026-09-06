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

using System.Text;

namespace Heimdall.Sftp;

/// <summary>
/// The POSIX permission mode as the browser and the view model exchange it: a 12-bit mask
/// carrying the set-user-ID, set-group-ID and sticky bits above the nine rwx bits.
/// </summary>
/// <remarks>
/// The nine-character display dropped the three special bits, so a 4755 binary read as 755,
/// the chmod dialog offered 755 as the current value, and the validator parsed the octal text
/// as a decimal number and refused anything above 777: those bits could be neither seen, nor
/// written, nor removed from this browser.
/// </remarks>
public static class SftpPermissionMode
{
    /// <summary>The set-user-ID bit.</summary>
    public const int SetUid = 0x800;

    /// <summary>The set-group-ID bit.</summary>
    public const int SetGid = 0x400;

    /// <summary>The sticky bit.</summary>
    public const int Sticky = 0x200;

    /// <summary>Every bit a mode may carry.</summary>
    public const int MaxMode = 0xFFF;

    private const int SymbolicLength = 9;
    private const int MaxOctalDigits = 4;

    /// <summary>Renders the nine-character symbolic form, with s/S and t/T for the special bits.</summary>
    public static string FormatSymbolic(int mode)
    {
        StringBuilder text = new(SymbolicLength);
        text.Append((mode & 0x100) != 0 ? 'r' : '-');
        text.Append((mode & 0x080) != 0 ? 'w' : '-');
        text.Append(SpecialOrExecute(mode, 0x040, SetUid, 's', 'S'));
        text.Append((mode & 0x020) != 0 ? 'r' : '-');
        text.Append((mode & 0x010) != 0 ? 'w' : '-');
        text.Append(SpecialOrExecute(mode, 0x008, SetGid, 's', 'S'));
        text.Append((mode & 0x004) != 0 ? 'r' : '-');
        text.Append((mode & 0x002) != 0 ? 'w' : '-');
        text.Append(SpecialOrExecute(mode, 0x001, Sticky, 't', 'T'));
        return text.ToString();
    }

    /// <summary>Reads the nine-character symbolic form back into a mode, special bits included.</summary>
    public static bool TryParseSymbolic(string? symbolic, out int mode)
    {
        mode = 0;
        if (symbolic is null || symbolic.Length != SymbolicLength)
        {
            return false;
        }

        int[] bits = [0x100, 0x080, 0x040, 0x020, 0x010, 0x008, 0x004, 0x002, 0x001];
        for (int index = 0; index < SymbolicLength; index++)
        {
            char c = symbolic[index];
            if (c == '-')
            {
                continue;
            }

            bool execute = index is 2 or 5 or 8;
            if (execute && c is 's' or 'S' or 't' or 'T')
            {
                mode |= index switch
                {
                    2 => SetUid,
                    5 => SetGid,
                    _ => Sticky,
                };
                if (c is 's' or 't')
                {
                    mode |= bits[index];
                }

                continue;
            }

            mode |= bits[index];
        }

        return true;
    }

    /// <summary>Renders three octal digits, or four when a special bit is set.</summary>
    public static string ToOctalString(int mode)
    {
        int special = (mode >> 9) & 0x7;
        string low = Convert.ToString(mode & 0x1FF, 8).PadLeft(3, '0');
        return special == 0 ? low : $"{special}{low}";
    }

    /// <summary>
    /// The decimal-coded octal number SSH.NET's SetPermissions takes: the mask 0o4755 becomes 4755.
    /// </summary>
    public static short ToOctalCoded(int mode)
    {
        if (mode < 0 || mode > MaxMode)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "A permission mode has at most twelve bits.");
        }

        return short.Parse(Convert.ToString(mode, 8), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Parses one to four octal digits typed by the user into a mode.</summary>
    public static bool TryParseOctal(string? text, out short mode)
    {
        mode = 0;
        if (string.IsNullOrEmpty(text) || text.Length > MaxOctalDigits)
        {
            return false;
        }

        int value = 0;
        foreach (char c in text)
        {
            if (c < '0' || c > '7')
            {
                return false;
            }

            value = (value << 3) | (c - '0');
        }

        mode = (short)value;
        return true;
    }

    private static char SpecialOrExecute(int mode, int executeBit, int specialBit, char withExecute, char withoutExecute)
    {
        bool execute = (mode & executeBit) != 0;
        if ((mode & specialBit) != 0)
        {
            return execute ? withExecute : withoutExecute;
        }

        return execute ? 'x' : '-';
    }
}
