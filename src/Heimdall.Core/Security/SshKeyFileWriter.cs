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
using System.Text;

namespace Heimdall.Core.Security;

/// <summary>
/// Writes generated SSH key material to disk in the form OpenSSH and PuTTY read:
/// UTF-8 without a byte order mark (a BOM in front of the key type token or the
/// PEM header makes the file unreadable), LF line endings, and for the private
/// key an ACL restricted to the current user so Win32-OpenSSH does not refuse it
/// as "permissions too open".
/// </summary>
public static class SshKeyFileWriter
{
    private const string LineFeed = "\n";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes an OpenSSH public key line (<c>type base64 comment</c>) followed by a single LF.
    /// </summary>
    public static void WritePublicKey(string filePath, string publicKeyLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(publicKeyLine);

        File.WriteAllText(filePath, NormalizeLineEndings(publicKeyLine.TrimEnd()) + LineFeed, Utf8NoBom);
    }

    /// <summary>
    /// Writes a PEM-encoded private key with a restrictive ACL, replacing any existing file.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void WritePrivateKey(string filePath, string privateKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(privateKeyPem);

        SecureFileWriter.WriteAndProtect(filePath, NormalizeLineEndings(privateKeyPem));
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", LineFeed, StringComparison.Ordinal)
               .Replace("\r", LineFeed, StringComparison.Ordinal);
}
