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
    private const string TempSuffix = ".tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes an OpenSSH public key line (<c>type base64 comment</c>) followed by a single LF,
    /// replacing any existing file in one step: a failure leaves the existing file as it was.
    /// </summary>
    public static void WritePublicKey(string filePath, string publicKeyLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(publicKeyLine);

        string text = NormalizeLineEndings(publicKeyLine.TrimEnd()) + LineFeed;
        string directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        string tempPath = Path.Combine(directory, Path.GetFileName(filePath) + TempSuffix + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(tempPath, text, Utf8NoBom);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Writes a PEM-encoded private key with a restrictive ACL, replacing any existing file in one
    /// step: a failure leaves the existing key as it was and surfaces the error.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void WritePrivateKey(string filePath, string privateKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(privateKeyPem);

        try
        {
            SecureFileWriter.WriteTextAtomic(filePath, NormalizeLineEndings(privateKeyPem));
        }
        catch (SecureFileWriter.AclCreationNotSupportedException ex)
        {
            // Reported as an ordinary write failure so a caller handles one family of errors.
            throw new IOException(ex.Message, ex);
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", LineFeed, StringComparison.Ordinal)
               .Replace("\r", LineFeed, StringComparison.Ordinal);
}
