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

namespace Heimdall.App.Services;

/// <summary>
/// Production <see cref="IApplicationLifecycle"/> that shuts the WPF application down on the
/// UI thread.
/// </summary>
internal sealed class ApplicationLifecycle : IApplicationLifecycle
{
    public void RequestShutdown() =>
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            System.Windows.Application.Current.Shutdown());
}
