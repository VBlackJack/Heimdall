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

using System.ComponentModel;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;

namespace Heimdall.App;

/// <summary>
/// The optional compact spacing of the session tree rows.
/// </summary>
/// <remarks>
/// Which resources apply is decided by <see cref="SessionTreeDensity"/>; this file only listens for
/// the preference and hands the tree's resources to it.
/// </remarks>
public partial class MainWindow
{
    private PropertyChangedEventHandler? _sessionTreeDensityHandler;

    private void WireSessionTreeDensity(MainViewModel viewModel)
    {
        _sessionTreeDensityHandler = (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(ServerListViewModel.CompactSessionTree), StringComparison.Ordinal))
            {
                Dispatcher.BeginInvoke(() => ApplySessionTreeDensity(viewModel));
            }
        };
        viewModel.ServerList.PropertyChanged += _sessionTreeDensityHandler;
        ApplySessionTreeDensity(viewModel);
    }

    private void UnwireSessionTreeDensity(MainViewModel viewModel)
    {
        if (_sessionTreeDensityHandler is not null)
        {
            viewModel.ServerList.PropertyChanged -= _sessionTreeDensityHandler;
        }
    }

    private void ApplySessionTreeDensity(MainViewModel viewModel)
        => SessionTreeDensity.Apply(SessionTreeView.Resources, TryFindResource, viewModel.ServerList.CompactSessionTree);
}
