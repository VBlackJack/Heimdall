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

using Microsoft.Web.WebView2.Core;

namespace Heimdall.App.Services;

/// <summary>
/// Which WebView2 process failures take an embedded terminal's page down.
/// </summary>
/// <remarks>
/// Every SSH view shares one browser environment, so a failure is raised on all of them at once.
/// Treating each one as fatal blanked every open terminal on a GPU driver reset or a display
/// change, although WebView2 restarts those helper processes by itself and the page carries on
/// (the renderer falls back from WebGL to canvas). Only a failure of the browser itself, or of the
/// process rendering the page, leaves nothing to draw on.
/// </remarks>
internal static class TerminalSurfaceFailurePolicy
{
    /// <summary>Whether the terminal page is gone after a failure of this kind.</summary>
    internal static bool IsFatal(CoreWebView2ProcessFailedKind kind) => kind switch
    {
        CoreWebView2ProcessFailedKind.BrowserProcessExited => true,
        CoreWebView2ProcessFailedKind.RenderProcessExited => true,
        CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => true,
        _ => false,
    };
}
