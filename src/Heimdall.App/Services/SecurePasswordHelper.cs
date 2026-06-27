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

using System.Runtime.InteropServices;
using System.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Marshals a <see cref="SecureString"/> (e.g. <c>PasswordBox.SecurePassword</c>,
/// which keeps the password encrypted in memory) to a pinned <see cref="char"/>
/// array for a one-shot crypto call, without ever materializing the password as
/// a managed immutable <see cref="string"/> (resolves the A4 residual). The
/// intermediate unmanaged buffer is zeroed and freed in <c>finally</c>; the
/// caller owns the returned array and MUST zero it (e.g. <c>Array.Clear</c>)
/// after use.
/// </summary>
internal static class SecurePasswordHelper
{
    /// <summary>
    /// Copy the contents of <paramref name="secure"/> into a freshly pinned char
    /// array. Returns an empty array when the secure string is empty.
    /// </summary>
    /// <param name="secure">The secure string to read.</param>
    /// <returns>A pinned char array the caller must zero after use.</returns>
    internal static char[] ToChars(SecureString secure)
    {
        ArgumentNullException.ThrowIfNull(secure);

        var length = secure.Length;
        if (length == 0)
        {
            return [];
        }

        var unmanaged = IntPtr.Zero;
        try
        {
            unmanaged = Marshal.SecureStringToGlobalAllocUnicode(secure);
            var chars = GC.AllocateArray<char>(length, pinned: true);
            for (var i = 0; i < length; i++)
            {
                chars[i] = (char)Marshal.ReadInt16(unmanaged, i * 2);
            }

            return chars;
        }
        finally
        {
            if (unmanaged != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(unmanaged);
            }
        }
    }
}
