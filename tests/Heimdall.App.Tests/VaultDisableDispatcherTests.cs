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

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Versioning;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

/// <summary>
/// The exclusive section of a disable raises the settings notification; its subscriber lives on the
/// UI thread. A preset save started on that thread must not leave the two waiting on each other.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class VaultDisableDispatcherTests : IAsyncLifetime
{
    private static readonly Argon2idParameters FastParams = new(MemoryKib: 256, Iterations: 1, Parallelism: 1);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private const string StrongPassword = "StrongMaster1!Pass";

    private string _root = string.Empty;
    private ConfigManager _config = null!;
    private VaultLifecycleService _service = null!;
    private FakeUiThread _ui = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "Heimdall.Dispatcher." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        CredentialProtectorStateScope.Reset(HmacIntegrity.GenerateRawKey());
        _config = new ConfigManager(Path.Combine(_root, "config"));
        await _config.InitializeAsync();
        _service = new VaultLifecycleService(_config);
        await _service.EnableAsync(StrongPassword.ToCharArray(), FastParams);
        _ui = new FakeUiThread();
    }

    public Task DisposeAsync()
    {
        _ui.Dispose();
        _service.Dispose();
        CredentialProtectorStateScope.Reset();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AsyncSave_WhileTheSubscriberWaitsForTheUiThread_DoesNotDeadlock()
    {
        // The worst subscriber: it waits for the UI thread, as Dispatcher.Invoke does.
        _config.SettingsChanged += _ => _ui.Send(() => { });
        var storage = new PasswordPresetStorage(_root);
        Task? save = null;

        IDisposable writer = await Task.Run(() => VaultSealGate.Shared.EnterAsync().AsTask());
        Task disable = Task.Run(() => _service.DisableAsync(StrongPassword.ToCharArray()));
        await Task.Delay(300);
        _ui.Post(() => save = storage.SaveAsync(new PasswordGeneratorStore()));
        await Task.Delay(100);
        writer.Dispose();

        Assert.Same(disable, await Task.WhenAny(disable, Task.Delay(Limit)));
        await disable;
        await _ui.Idle();
        Assert.NotNull(save);
        Assert.Same(save, await Task.WhenAny(save!, Task.Delay(Limit)));
    }

    [Fact]
    public async Task SyncSave_BlockingTheUiThread_DoesNotDeadlockAgainstAPostingSubscriber()
    {
        // The production subscriber posts and never waits.
        _config.SettingsChanged += _ => _ui.Post(() => { });
        var storage = new PasswordPresetStorage(_root);
        var saved = new TaskCompletionSource();

        IDisposable writer = await Task.Run(() => VaultSealGate.Shared.EnterAsync().AsTask());
        Task disable = Task.Run(() => _service.DisableAsync(StrongPassword.ToCharArray()));
        await Task.Delay(300);
        _ui.Post(() =>
        {
            storage.Save(new PasswordGeneratorStore());
            saved.SetResult();
        });
        await Task.Delay(100);
        writer.Dispose();

        Assert.Same(disable, await Task.WhenAny(disable, Task.Delay(Limit)));
        await disable;
        Assert.Same(saved.Task, await Task.WhenAny(saved.Task, Task.Delay(Limit)));
    }

    /// <summary>A thread that runs posted work one item at a time, like a dispatcher.</summary>
    private sealed class FakeUiThread : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public FakeUiThread()
        {
            _thread = new Thread(() =>
            {
                foreach (Action action in _queue.GetConsumingEnumerable())
                {
                    action();
                }
            })
            { IsBackground = true };
            _thread.Start();
        }

        public void Post(Action action) => _queue.Add(action);

        public void Send(Action action)
        {
            using var done = new ManualResetEventSlim();
            _queue.Add(() =>
            {
                try
                {
                    action();
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait();
        }

        public Task Idle()
        {
            var reached = new TaskCompletionSource();
            Post(() => reached.SetResult());
            return reached.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
