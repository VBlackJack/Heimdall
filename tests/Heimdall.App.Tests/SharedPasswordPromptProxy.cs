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

using System.Reflection;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// A dialog service that answers the password question with the remember choice from a queue,
/// tolerates the error notice of a failed connection, and refuses every other dialog.
/// </summary>
/// <remarks>
/// The plain password question is refused too: the default implementation of the remember
/// question falls back to it, so a proxy that did not intercept the remember question itself
/// would fail here instead of answering without the choice.
/// </remarks>
internal class SharedPasswordPromptProxy : DispatchProxy
{
    /// <summary>The answers, in order; an empty queue answers as a cancelled dialog.</summary>
    public Queue<PasswordPromptResult?> Answers { get; } = new();

    /// <summary>The message of every question asked.</summary>
    public List<string> Prompts { get; } = [];

    /// <summary>The label of the remember choice of every question asked.</summary>
    public List<string> RememberLabels { get; } = [];

    public static (IDialogService Service, SharedPasswordPromptProxy Proxy) Create()
    {
        IDialogService service = DispatchProxy.Create<IDialogService, SharedPasswordPromptProxy>();
        return (service, (SharedPasswordPromptProxy)(object)service);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(IDialogService.ShowPasswordInputWithRememberAsync):
                Prompts.Add((string)args![1]!);
                RememberLabels.Add((string)args[2]!);
                return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);

            case nameof(IDialogService.ShowError):
            case nameof(IDialogService.ShowWarning):
                return null;

            default:
                throw new NotSupportedException($"unexpected dialog call: {targetMethod?.Name}");
        }
    }
}
