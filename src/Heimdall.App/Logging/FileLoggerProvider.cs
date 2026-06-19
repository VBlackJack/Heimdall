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

using Microsoft.Extensions.Logging;

namespace Heimdall.App.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _minLevel;
    private readonly Action<LogLevel, string, Exception?> _sink;

    public FileLoggerProvider(LogLevel minLevel, Action<LogLevel, string, Exception?>? sink = null)
    {
        _minLevel = minLevel;
        _sink = sink ?? WriteToFileLogger;
    }

    public ILogger CreateLogger(string categoryName) => new FileLoggerAdapter(categoryName, _minLevel, _sink);

    public void Dispose()
    {
    }

    private static void WriteToFileLogger(LogLevel level, string message, Exception? exception)
    {
        switch (level)
        {
            case LogLevel.Warning:
                if (exception is null)
                {
                    Heimdall.Core.Logging.FileLogger.Warn(message);
                }
                else
                {
                    Heimdall.Core.Logging.FileLogger.Warn($"{message}: {exception.Message}");
                }

                break;

            case LogLevel.Error:
            case LogLevel.Critical:
                if (exception is null)
                {
                    Heimdall.Core.Logging.FileLogger.Error(message);
                }
                else
                {
                    Heimdall.Core.Logging.FileLogger.Error(message, exception);
                }

                break;
        }
    }

    private sealed class FileLoggerAdapter : ILogger
    {
        private readonly string _category;
        private readonly LogLevel _minLevel;
        private readonly Action<LogLevel, string, Exception?> _sink;

        public FileLoggerAdapter(
            string category,
            LogLevel minLevel,
            Action<LogLevel, string, Exception?> sink)
        {
            _category = category;
            _minLevel = minLevel;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

            var message = formatter(state, exception);
            _sink(logLevel, $"[{_category}] {message}", exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
