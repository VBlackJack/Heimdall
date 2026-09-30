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

using System.IO;
using System.Reflection;
using System.Text.Json;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// What the embedded SSH path does with the keyboard-interactive question dialog: what the user
/// is shown, and what a cancelled dialog is reported as.
/// </summary>
/// <remarks>
/// The dial is replaced by one that drives the production prompt answering
/// (<see cref="SshConnectionFactory.AnswerKeyboardInteractivePrompts"/>) with the responder the
/// handler built, so the dialog, the exception it raises and the classification all run as they
/// ship. Only the network is absent.
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SshHandlerInteractivePromptTests : IDisposable
{
    private const string CancelledSentence = "FIXTURE the connection was cancelled.";
    private const string AuthTimeoutSentence = "FIXTURE authentication timed out, check Pageant.";
    private const string RetryingViaPlinkStatus = "FIXTURE retrying through the interactive client.";
    private const string VerificationCodePrompt = "Verification code: ";
    private const string PromptMessageTemplate = "FIXTURE sent by {0} for {1}: [{2}]";

    private readonly CredentialProtectorStateScope _protectorState = new();
    private readonly string _localesPath;

    public SshHandlerInteractivePromptTests()
    {
        _localesPath = Path.Combine(Path.GetTempPath(), $"heimdall-locales-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_localesPath);
        File.WriteAllText(
            Path.Combine(_localesPath, "en.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["ErrorSshCancelled"] = CancelledSentence,
                ["ErrorSshAuthTimeout"] = AuthTimeoutSentence,
                [SshLocalizationKeys.StatusSshRetryingViaPlink] = RetryingViaPlinkStatus,
                [SshLocalizationKeys.InteractivePromptMessage] = PromptMessageTemplate,
            }));
    }

    public void Dispose()
    {
        _protectorState.Dispose();
        try
        {
            Directory.Delete(_localesPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>
    /// Cancelling the verification-code dialog is reported as a cancellation.
    /// </summary>
    /// <remarks>
    /// Audit 2026-09-30 S-01. The cancel raised a bare OperationCanceledException with the connect
    /// token untouched, so the handler's generic catch classified it as an authentication timeout
    /// and the user read "timed out, check that Pageant is running" after pressing Cancel.
    /// </remarks>
    [Fact]
    public async Task CancellingTheVerificationCodeDialog_IsReportedAsCancelled_NotAsATimeout()
    {
        using Harness harness = await CreateHarnessAsync(dialogAnswer: null);

        ConnectionResult result = await harness.ConnectAsync();

        Assert.False(result.Success);
        Assert.Equal(1, harness.Dialog.Calls);
        Assert.Equal(CancelledSentence, result.ErrorMessage);
        Assert.DoesNotContain(RetryingViaPlinkStatus, harness.Statuses);
    }

    /// <summary>
    /// The server's prompt reaches the dialog stripped of what could make it pose as Heimdall.
    /// </summary>
    /// <remarks>
    /// Audit 2026-09-30 S-08. The request went into the Heimdall-branded modal verbatim, so a
    /// server could send line breaks and a right-to-left override to lay out a paragraph that
    /// reads as Heimdall asking for the vault master password.
    /// </remarks>
    [Fact]
    public async Task TheServersPrompt_IsSanitisedBeforeItReachesTheDialog()
    {
        string rightToLeftOverride = char.ConvertFromUtf32(0x202E);
        string hostile = "Code:\r\n\r\nHeimdall vault locked." + rightToLeftOverride + " Master password:";
        using Harness harness = await CreateHarnessAsync(dialogAnswer: "123456", prompt: hostile);

        await harness.ConnectAsync();

        Assert.Equal(
            string.Format(
                PromptMessageTemplate,
                "host.example.test",
                "ssh-user",
                "Code: Heimdall vault locked. Master password:"),
            harness.Dialog.LastMessage);
    }

    private async Task<Harness> CreateHarnessAsync(string? dialogAnswer, string prompt = VerificationCodePrompt)
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(_localesPath, "en");
        return new Harness(localizer, dialogAnswer, prompt);
    }

    private sealed class Harness : IDisposable
    {
        private const string ServerId = "5d0c9e61-2f3b-4c55-9a51-7b3e2c8d1f40";
        private readonly SshHandler _handler;

        public Harness(LocalizationManager localizer, string? dialogAnswer, string promptText)
        {
            Dialog = RecordingDialog.Create(dialogAnswer);
            _handler = new SshHandler(
                new NoTunnelService(),
                new ConnectionStateMachine(),
                localizer,
                new HostKeyStore(),
                hostKeyTrustService: null!,
                RejectingHostKeyVerifier.Instance,
                x11ServerManager: null!,
                dialogService: (IDialogService)Dialog,
                plinkHostKeyProbe: null,
                agentRegistryFactory: _ => new SshAgentRegistry([]),
                connectShellSession: (_, connectionParams, _, _, _, _, _) =>
                {
                    // What SSH.NET does with the prompt handler: it runs it and rethrows what it
                    // raised from Authenticate, unwrapped.
                    AuthenticationPrompt prompt = new(0, false, promptText);
                    SshConnectionFactory.AnswerKeyboardInteractivePrompts(
                        [prompt],
                        connectionParams.Password ?? string.Empty,
                        connectionParams.KeyboardInteractive,
                        connectionParams.KeyboardInteractiveResponder);
                    throw new SshAuthenticationException("Permission denied (keyboard-interactive).");
                },
                startPipeModeSession: (_, _, _, _, _, _) =>
                    throw new InvalidOperationException("The Plink launch must not be reached."),
                puttySessionRegistry: new InMemoryPuttySessionRegistry());
            _handler.SetStatusText = text => Statuses.Add(text);
        }

        public RecordingDialog Dialog { get; }

        public List<string> Statuses { get; } = [];

        public Task<ConnectionResult> ConnectAsync()
        {
            ServerProfileDto server = new ServerProfileDto
            {
                Id = ServerId,
                DisplayName = "shell",
                RemoteServer = "host.example.test",
                SshPort = 22,
                ConnectionType = "SSH",
                SshMode = "Embedded",
                SshUsername = "ssh-user",
                UseDirectConnection = true
            };

            return _handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);
        }

        public void Dispose() => _handler.Dispose();
    }

    /// <summary>
    /// A dialog service that answers the password dialog with a fixed value and records what it
    /// was shown. Every other member throws, so an unexpected dialog fails the test.
    /// </summary>
    internal class RecordingDialog : DispatchProxy
    {
        private string? _answer;

        public int Calls { get; private set; }

        public string? LastTitle { get; private set; }

        public string? LastMessage { get; private set; }

        public static RecordingDialog Create(string? answer)
        {
            IDialogService proxy = DispatchProxy.Create<IDialogService, RecordingDialog>();
            RecordingDialog dialog = (RecordingDialog)(object)proxy;
            dialog._answer = answer;
            return dialog;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IDialogService.ShowPasswordInputAsync) || args is null)
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            Calls++;
            LastTitle = (string?)args[0];
            LastMessage = (string?)args[1];
            return Task.FromResult(_answer);
        }
    }

    private sealed class NoTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false) =>
            Task.FromResult(
                new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, null, null));

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }
}
