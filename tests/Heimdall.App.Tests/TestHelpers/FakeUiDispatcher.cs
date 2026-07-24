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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

internal sealed class FakeUiDispatcher(bool checkAccess = true) : IUiDispatcher
{
    private bool _isExecutingOnUiThread;

    public int InvokeCalls { get; private set; }

    public int InvokeAsyncCalls { get; private set; }

    public int InvokeAsyncFuncCalls { get; private set; }

    public bool CheckAccessResult { get; set; } = checkAccess;

    public Func<Func<Task>, Task>? InvokeAsyncFuncHandler { get; set; }

    public Action<Action>? InvokeAsyncActionHandler { get; set; }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        InvokeCalls++;
        ExecuteOnUiThread(action);
    }

    public T Invoke<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        InvokeCalls++;
        return ExecuteOnUiThread(func);
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        InvokeAsyncCalls++;
        if (InvokeAsyncActionHandler is not null)
        {
            InvokeAsyncActionHandler(action);
        }
        else
        {
            ExecuteOnUiThread(action);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Executes the asynchronous action immediately on the fake UI thread and
    /// returns a task that completes when the inner async work completes.
    /// Tests can override <see cref="InvokeAsyncFuncHandler"/> to delay or wrap
    /// the inner task for sequencing assertions.
    /// </summary>
    public Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        InvokeAsyncFuncCalls++;
        return ExecuteOnUiThreadAsync(() => InvokeAsyncFuncHandler?.Invoke(action) ?? action());
    }

    public bool CheckAccess() => _isExecutingOnUiThread || CheckAccessResult;

    private void ExecuteOnUiThread(Action action)
    {
        var previous = _isExecutingOnUiThread;
        _isExecutingOnUiThread = true;
        try
        {
            action();
        }
        finally
        {
            _isExecutingOnUiThread = previous;
        }
    }

    private T ExecuteOnUiThread<T>(Func<T> func)
    {
        var previous = _isExecutingOnUiThread;
        _isExecutingOnUiThread = true;
        try
        {
            return func();
        }
        finally
        {
            _isExecutingOnUiThread = previous;
        }
    }

    private async Task ExecuteOnUiThreadAsync(Func<Task> func)
    {
        var previous = _isExecutingOnUiThread;
        _isExecutingOnUiThread = true;
        try
        {
            await func();
        }
        finally
        {
            _isExecutingOnUiThread = previous;
        }
    }
}
