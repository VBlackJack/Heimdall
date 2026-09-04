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

using System.Windows;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Services;

/// <summary>
/// Finds the main view model through the application singleton, if there still is one.
/// </summary>
/// <remarks>
/// <para>Null-safe on the application itself, not only on the window. WPF clears
/// <c>Application.Current</c> inside its shutdown, and code still runs after that point: any
/// continuation of an asynchronous <c>OnExit</c>, and the late Unloaded broadcasts the pane
/// control already guards against. The pane control dereferenced the singleton directly, and
/// it was the one control still loaded when the shutdown log recorded a
/// NullReferenceException on the last session's silent close.</para>
/// <para>One helper for every call site, so the next one cannot pick the unsafe form.</para>
/// </remarks>
internal static class MainViewModelLocator
{
    /// <summary>The main view model reachable from <paramref name="application"/>, or null.</summary>
    public static MainViewModel? Find(Application? application)
        => application?.MainWindow?.DataContext as MainViewModel;

    /// <summary>The main view model reachable from the current application, or null.</summary>
    public static MainViewModel? FindCurrent() => Find(Application.Current);
}
