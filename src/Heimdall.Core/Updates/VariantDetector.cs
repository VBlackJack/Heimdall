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

namespace Heimdall.Core.Updates;

/// <summary>
/// Determines the running build variant by probing for the bundled WebView2
/// runtime marker. The base directory and existence probe are injectable so the
/// detector can be exercised without touching the real filesystem.
/// </summary>
public sealed class VariantDetector : IVariantDetector
{
    private const string MarkerRelativePath = @"runtimes\webview2\msedgewebview2.exe";

    private readonly string _baseDirectory;
    private readonly Func<string, bool> _fileExists;

    public VariantDetector(string? baseDirectory = null, Func<string, bool>? fileExists = null)
    {
        _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
        _fileExists = fileExists ?? File.Exists;
    }

    public BuildVariant Detect()
    {
        var markerPath = Path.Combine(_baseDirectory, MarkerRelativePath);
        return _fileExists(markerPath) ? BuildVariant.SelfContained : BuildVariant.Standard;
    }
}
