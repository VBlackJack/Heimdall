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

using System.Diagnostics;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// Default <see cref="IBrowserLauncher"/>. Validates the URL is absolute http/https,
/// then hands it to the shell. Reuses the canonical open-url pattern from the views.
/// </summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
    public void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            FileLogger.Warn("[Updates] refused to open a non-http(s) release URL.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[Updates] opening the release URL failed: {ex.Message}");
        }
    }
}
