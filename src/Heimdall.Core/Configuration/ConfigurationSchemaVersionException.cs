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
/// Raised when this binary is asked to overwrite a configuration document
/// written with a newer schema version.
/// </summary>
public sealed class ConfigurationSchemaVersionException : InvalidOperationException
{
    public ConfigurationSchemaVersionException(
        string documentName,
        string documentPath,
        int foundVersion,
        int supportedVersion)
        : base(
            $"Cannot save {documentName}: '{documentPath}' uses schema version {foundVersion}, " +
            $"but this Heimdall build supports up to version {supportedVersion}. " +
            "The file was left unchanged. Open it with a compatible Heimdall version.")
    {
        DocumentName = documentName;
        DocumentPath = documentPath;
        FoundVersion = foundVersion;
        SupportedVersion = supportedVersion;
    }

    public string DocumentName { get; }

    public string DocumentPath { get; }

    public int FoundVersion { get; }

    public int SupportedVersion { get; }
}
