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

namespace Heimdall.Sftp;

/// <summary>
/// Guards remote SFTP paths and child names before file operations.
/// </summary>
public static class SftpPathGuard
{
    public static bool IsProtectedRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        string normalized = path.Trim().TrimEnd('/');
        return normalized.Length == 0;
    }

    public static void ThrowIfProtectedRoot(string? path, string operation)
    {
        if (!IsProtectedRoot(path))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Refused to {operation} protected remote root path.");
    }

    public static bool IsValidChildName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();
        if (trimmed is "." or "..")
        {
            return false;
        }

        // Control characters too, not only NUL: a newline is a legal POSIX filename
        // character and every shell command this client builds refuses it downstream,
        // with an unlocalized exception. Refusing at the boundary keeps such an entry
        // out of the model entirely.
        foreach (char c in trimmed)
        {
            if (c is '/' or '\\' || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }
}
