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
using System.Text;
using Heimdall.Core.Localization;

namespace Heimdall.Core.Tests;

/// <summary>
/// Where <see cref="LocalizationManager.LocaleChanged"/> subscribers run, and what a failing one costs.
/// </summary>
public class LocalizationManagerLocaleChangedTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PumpJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly string _tempDir;
    private readonly string _localesPath;

    public LocalizationManagerLocaleChangedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.Tests." + Guid.NewGuid().ToString("N"));
        _localesPath = Path.Combine(_tempDir, "locales");
        Directory.CreateDirectory(_localesPath);
        File.WriteAllText(Path.Combine(_localesPath, "en.json"), "{\"StatusReady\":\"Ready\"}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_localesPath, "fr.json"), "{\"StatusReady\":\"Pr\u00eat\"}", new UTF8Encoding(false));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public void Handlers_RunOnTheContextTheSwitchWasCalledFrom()
    {
        using var pump = new PumpingContext();
        LocalizationManager manager = new();
        SynchronizationContext? seenContext = null;
        int seenThread = -1;

        pump.Run(async () =>
        {
            await manager.LoadAsync(_localesPath, "en");
            manager.LocaleChanged += _ =>
            {
                seenContext = SynchronizationContext.Current;
                seenThread = Environment.CurrentManagedThreadId;
            };

            await manager.SwitchLocaleAsync("fr");
        });

        Assert.Same(pump, seenContext);
        Assert.Equal(pump.ThreadId, seenThread);
        Assert.Equal("fr", manager.CurrentLocale);
    }

    [Fact]
    public async Task AThrowingHandler_DoesNotStopTheNextOneNorFailTheSwitch()
    {
        // Task.Run starts from a clean context whatever xUnit installed on the test thread.
        await Task.Run(async () =>
        {
            LocalizationManager manager = new();
            await manager.LoadAsync(_localesPath, "en");
            string? received = null;
            manager.LocaleChanged += _ => throw new InvalidOperationException("subscriber failure");
            manager.LocaleChanged += locale => received = locale;

            await manager.SwitchLocaleAsync("fr");

            Assert.Equal("fr", received);
            Assert.Equal("fr", manager.CurrentLocale);
        });
    }

    [Fact]
    public async Task WithoutAContext_HandlersStillRunAndTheCallCompletes()
    {
        await Task.Run(async () =>
        {
            Assert.Null(SynchronizationContext.Current);
            LocalizationManager manager = new();
            await manager.LoadAsync(_localesPath, "en");
            int calls = 0;
            manager.LocaleChanged += _ => calls++;

            await manager.SwitchLocaleAsync("fr");

            Assert.Equal(1, calls);
            Assert.Equal("fr", manager.CurrentLocale);
        });
    }

    [Fact]
    public async Task ALoadFailure_StillThrowsAndRaisesNothing()
    {
        await Task.Run(async () =>
        {
            LocalizationManager manager = new();
            await manager.LoadAsync(_localesPath, "en");
            int calls = 0;
            manager.LocaleChanged += _ => calls++;

            await Assert.ThrowsAsync<FileNotFoundException>(() => manager.SwitchLocaleAsync("de"));

            Assert.Equal(0, calls);
            Assert.Equal("en", manager.CurrentLocale);
        });
    }

    /// <summary>
    /// A single-thread message pump installed as the current context on its own thread.
    /// </summary>
    private sealed class PumpingContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public PumpingContext()
        {
            _thread = new Thread(Pump) { IsBackground = true };
            _thread.Start();
            ThreadId = _thread.ManagedThreadId;
        }

        public int ThreadId { get; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            // A continuation arriving after Dispose (for example once the wait timed out) is dropped.
            _queue.TryAdd((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public void Run(Func<Task> work)
        {
            TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                async Task Wrapper()
                {
                    try
                    {
                        await work();
                        done.SetResult();
                    }
                    catch (Exception ex)
                    {
                        done.SetException(ex);
                    }
                }

                _ = Wrapper();
            }, null);

            Assert.True(done.Task.Wait(Timeout), "The pumped work did not complete in time.");
            done.Task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (_thread.Join(PumpJoinTimeout))
            {
                _queue.Dispose();
            }
        }

        private void Pump()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }
}
