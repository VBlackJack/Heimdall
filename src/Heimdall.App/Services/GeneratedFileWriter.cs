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
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Writes the file a generator tool produced. A file that carries a private key is created with
/// an ACL restricted to the current user; a certificate or a hash list is an ordinary file.
/// </summary>
internal static class GeneratedFileWriter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>True when the file for <paramref name="request"/> must be written with a protected ACL.</summary>
    internal static bool RequiresProtectedWrite(SaveFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.ContainsPrivateKey
            || (request.Content is string text && CertificateGeneratorViewModel.ContainsPrivateKeyMarker(text));
    }

    /// <summary>Writes <paramref name="request"/> to <paramref name="path"/>; returns false when its content has no known form.</summary>
    internal static bool Write(string path, SaveFileRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(request);

        bool protectedWrite = RequiresProtectedWrite(request);
        if (request.IsBinary && request.Content is byte[] bytes)
        {
            if (protectedWrite)
            {
                SecureFileWriter.WriteBytesAtomic(path, bytes);
            }
            else
            {
                File.WriteAllBytes(path, bytes);
            }

            return true;
        }

        if (!request.IsBinary && request.Content is string text)
        {
            if (protectedWrite)
            {
                SecureFileWriter.WriteTextAtomic(path, text);
            }
            else
            {
                File.WriteAllText(path, text, Utf8WithBom);
            }

            return true;
        }

        return false;
    }
}
