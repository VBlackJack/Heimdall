<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

*Ce document est la version française de [../CI_FLAKY_TESTS.md](../CI_FLAKY_TESTS.md). / This document is the French version.*

# Tests instables en CI - `Category=CIUnstable`

Un petit ensemble de tests, stables sur les postes de développement, vire au
rouge de manière intermittente sur le runner Windows de GitHub Actions. Ces
tests portent le marqueur `[Trait("Category", "CIUnstable")]` et sont exclus de
la passe de tests bloquante de la CI via
`dotnet test --filter "Category!=CIUnstable"`.

Une seconde étape non bloquante dans `.github/workflows/ci.yml` exécute les
mêmes tests avec `continue-on-error: true`, de sorte que la chaîne continue de
tourner sans faire échouer le build. Lisez son résultat dans le log brut, pas
dans le statut de l'étape ni du check : voir *Lire le vrai résultat d'une
chaîne informative* ci-dessous.

Le workflow exécute également les tests marqués
`[Trait("Category", "RequiresDesktop")]` dans une étape informative séparée et
non bloquante, avec un timeout. Ces tests parcourent des chemins de smoke tests
UIAutomation sur le bureau et exigent un bureau Windows interactif fiable. Ils
passent dans une session de développement normale, mais peuvent se bloquer ou
devenir instables sur le runner Windows de GitHub Actions, parce que le bureau
du runner et la latence UIAutomation ne sont pas déterministes. Ils restent
hors de la passe de couverture bloquante par choix, et leur résultat se lit de
la même manière.

## Lire le vrai résultat d'une chaîne informative

`continue-on-error: true` permet au job et au rollup du check de commit de
rester en succès alors que la commande de la chaîne sort avec un code non nul.
La conséquence à intégrer :

**Une conclusion d'étape lisible par machine valant `success` ne prouve pas que
la commande sous-jacente s'est terminée avec succès.**

Sur une exécution où la chaîne `RequiresDesktop` avait réellement échoué, tous
ces indicateurs annonçaient un succès : la `conclusion` de l'étape dans
`gh run view --json jobs`, la `conclusion` du job, la `conclusion` de
l'exécution, le check-run du commit, `gh pr checks` et le `statusCheckRollup`
de la pull request.

L'échec n'est pas effacé pour autant. Une annotation d'erreur ou une ligne de
log rouge peut rester visible dans certaines vues GitHub : l'exécution porte
une annotation en `annotation_level: "failure"`, que `gh run view` affiche sans
`--log`. Mais cette annotation dit seulement
`Process completed with exit code 1.`, elle est attribuée au job plutôt qu'à
l'étape, et ses champs `title` et `raw_details` sont vides. Elle ne nomme ni la
chaîne, ni le test en échec, ni les compteurs. Avec trois étapes
`continue-on-error` dans le workflow, elle ne dit pas laquelle a échoué.

Le verdict d'une chaîne informative doit donc être dérivé du log brut, de ses
totaux de tests et du marqueur de sortie du processus :

```bash
gh run view <run-id> --repo VBlackJack/Heimdall --log
```

Marqueurs à rechercher :

- `Test Run Failed.` - l'exécution de tests de la chaîne elle-même, par
  opposition au statut de l'étape.
- Le triplet `Total tests: / Passed: / Failed:` qui la suit, et qui donne les
  compteurs que l'annotation omet.
- `##[error]Process completed with exit code 1.` - la sortie non nulle absorbée
  par `continue-on-error`.

Ne filtrez **pas** ce log sur sa colonne de nom d'étape. `gh` affiche toutes
les lignes de certaines exécutions avec `UNKNOWN STEP` dans cette colonne :
l'exécution `31967767508` le montre sur ses 10533 lignes, alors que les
exécutions `31971172553` et `31972881070` ne le font pas, et le déclencheur
reste non identifié. Les données sous-jacentes de l'API sont intactes
(`--json jobs` renvoie bien les 20 étapes pour la même exécution) : il s'agit
donc d'un défaut de rendu, pas de données manquantes. Délimitez plutôt une
chaîne par sa position, entre son propre en-tête `##[group]Run dotnet test
... --filter "Category=RequiresDesktop"` et l'en-tête `##[group]Run` de l'étape
suivante.

### Un exemple traité, et ce qu'il prouve ou non

Deux exécutions ont été lues de cette manière pendant la livraison de la
PR #140 :

| Exécution | Événement | SHA de tête | Chaîne `RequiresDesktop` |
|---|---|---|---|
| `31967767508` | push | `748508c0` | 105 au total / 104 réussis / 1 en échec |
| `31971172553` | pull_request | `b6e04d32` | 105 au total / 104 réussis / 1 en échec |

Les deux ont échoué sur le même test,
`SessionTreeSelectionAutomationTests.SessionTree_MultiSelection_IsVisibleThroughRealUiAutomation`.
La frame de pile pointe sur `SessionTreeSelectionAutomationTests.cs:297`, qui
est l'assertion en échec ; la méthode de test elle-même est déclarée ligne 272.

`748508c0` est le parent du commit proposé par la PR #140 : la paire établit
donc que cet échec préexiste à cette pull request. C'est tout ce qu'elle
établit. Il s'agit d'une observation bornée sur deux exécutions, pas d'une
garantie permanente : une exécution ultérieure montrant la même chaîne en rouge
doit être remesurée avant d'être qualifiée de préexistante, et cela ne dit rien
sur le caractère intermittent ou permanent de l'échec.

## Pourquoi ces tests ne sont instables que sur le runner

Quatre causes racines distinctes partagent le même symptôme
(`TaskCanceledException`, `OperationCanceledException` ou timeouts de
`WaitUntil`) :

1. **Latence de la poignée de main sur named pipe** - les
   `OpenSshPipeAgentTests` créent un named pipe par test et font courir un
   `WaitForConnectionAsync` côté serveur contre la connexion côté client. Sur
   le runner GitHub Actions, la poignée de main dépasse régulièrement 10
   secondes, même avec un `availabilityTimeoutMs` généreux et un
   `CancellationTokenSource(TimeSpan.FromSeconds(10))` côté serveur. Suspect :
   Defender ou la contention d'E/S du runner qui analyse le pipe.
2. **Latence de propagation des bindings WPF + UIAutomation** - les
   `Pilots/*SmokeTests` attendent qu'une valeur se propage à travers une chaîne
   `Binding` / `INotifyPropertyChanged`. Sur un runner lent, la propagation
   dépasse même un `WaitHelpers.DefaultTimeout` de 10 secondes. Augmenter
   encore le timeout ne fait que décaler la fenêtre d'échec et ralentir les
   tests réellement bloqués.
3. **Course au démarrage du processus ConPTY** - les `ConPtySessionTests`
   démarrent `powershell.exe -NoLogo -NoProfile` dans une pseudo-console et
   vérifient `IsRunning` immédiatement après le premier déclenchement du
   callback `DataReceived`. Sur un runner lent, PowerShell peut afficher sa
   bannière puis sortir (ou l'attachement ConPTY peut lâcher) avant que
   l'assertion ne lise `IsRunning`, ce qui fait échouer la vérification.
   L'assertion `NotEmpty(text)` qui la précède couvre toujours le contrat
   essentiel (ConPTY délivre de la sortie) ; la propriété de cycle de vie est
   exercée indépendamment par `Dispose_TerminatesPseudoConsoleAndProcess`.
4. **Timeout de polling de ViewModel** - les `TcpPingViewModelTests` et les
   tests de ViewModel similaires utilisent un helper local au fichier
   `WaitUntilAsync(condition, timeoutMs)` pour observer des mises à jour de
   propriétés ou de collections produites par des tâches d'arrière-plan. Sur un
   runner Windows GitHub Actions chargé, la condition scrutée peut mettre plus
   de temps à devenir vraie que le timeout du test (la boucle d'attente scrute
   toutes les 10 ms et lève une `TimeoutException` passée l'échéance).
   Augmenter encore le timeout ne fait que décaler la fenêtre d'échec sans
   l'éliminer.

## Tests actuellement marqués `CIUnstable`

La solution comporte 11 emplacements `[Trait("Category", "CIUnstable")]` : 8
sur des méthodes de test individuelles et 3 sur des classes entières. Chaque
emplacement est listé ci-dessous.

| Test | Fichier | Catégories |
|---|---|---|
| `OpenSshPipeAgentTests.GetIdentities_ReadsResponseFromNamedPipe` | `tests/Heimdall.Ssh.Tests/OpenSshPipeAgentTests.cs` | `CIUnstable` |
| `OpenSshPipeAgentTests.GetIdentities_WhenPipeClosesAfterConnect_ReturnsEmpty` | `tests/Heimdall.Ssh.Tests/OpenSshPipeAgentTests.cs` | `CIUnstable` |
| `OpenSshPipeAgentTests.AgentKeySign_SendsFlagsAndReturnsSignature` | `tests/Heimdall.Ssh.Tests/OpenSshPipeAgentTests.cs` | `CIUnstable` |
| `ConPtySessionTests.StartAsync_LaunchesShell_DeliversInitialTerminalOutput` | `tests/Heimdall.Terminal.Tests/ConPtySessionTests.cs` | `CIUnstable` |
| `ConPtySessionTests.DataReceived_SubscriberAddedAfterBootstrapOutput_ReplaysBufferedOutput` | `tests/Heimdall.Terminal.Tests/ConPtySessionTests.cs` | `CIUnstable` |
| `DnsLookupViewModelTests.CancelCommand_UserCancellation_ClearsStatusWithoutError` | `tests/Heimdall.App.Tests/DnsLookupViewModelTests.cs` | `CIUnstable` |
| `WhoisLookupViewModelTests.CancelCommand_UserCancellation_ClearsStatusWithoutError` | `tests/Heimdall.App.Tests/WhoisLookupViewModelTests.cs` | `CIUnstable` |
| `TcpPingViewModelTests.StartCommand_MixedResults_PreservesFailedLineAndSummary` | `tests/Heimdall.App.Tests/TcpPingViewModelTests.cs` | `CIUnstable` |
| `HmacGeneratorSmokeTests` (marqueur de classe, 6 tests) | `tests/Heimdall.App.UiTests/Pilots/HmacGeneratorSmokeTests.cs` | `CIUnstable` + `RequiresDesktop` |
| `TextDiffSmokeTests` (marqueur de classe, 7 tests) | `tests/Heimdall.App.UiTests/Pilots/TextDiffSmokeTests.cs` | `CIUnstable` + `RequiresDesktop` |
| `HashGeneratorSmokeTests` (marqueur de classe, 5 tests) | `tests/Heimdall.App.UiTests/Pilots/HashGeneratorSmokeTests.cs` | `CIUnstable` + `RequiresDesktop` |

Les trois classes de smoke tests portent `CIUnstable` au niveau de la classe,
tandis que chacune de leurs méthodes de test porte en plus `RequiresDesktop` :
elles sont donc exclues par l'une comme par l'autre moitié du filtre bloquant.
Les autres classes `Pilots/*SmokeTests` ne portent que `RequiresDesktop` et ne
font pas partie de cet inventaire.

`OpenSshPipeAgentTests.IsAvailable_NoServer_ReturnsFalse` n'est
intentionnellement PAS marqué : c'est un test de chemin négatif qui vérifie
qu'une sonde de disponibilité de 25 ms se déclenche lorsqu'aucun serveur
n'écoute, et ce chemin n'est pas affecté par la latence du runner.

## Instabilités corrigées par réécriture plutôt que par marquage

Tout échec propre au runner ne mérite pas un marqueur. Deux tests SSH faisaient
échouer la chaîne bloquante à cause d'une famine du thread pool sur le runner
bicoeur : ils ont été réparés plutôt qu'exclus.

- `SshShellSessionTeardownTests.Disconnect_StuckReadLoop_DoesNotBlockCallerForFinalWait`
  attendait sur `SpinWait.SpinUntil`, une attente active qui occupait le worker
  du pool exécutant le test alors que la notification attendue était produite
  par une continuation mise en file dans ce même pool. Il attend désormais une
  `TaskCompletionSource`, et `SshShellSession` accepte un `TimeProvider`
  optionnel pour que le test pilote l'attente finale de teardown depuis un
  `FakeTimeProvider` au lieu du temps horloge.
- Les trois tests de contention de verrou des `TunnelManagerTests` faisaient
  courir une sonde `Task.Run` contre un `Task.Delay` de deux secondes. Gagner
  cette course exigeait à la fois qu'aucun verrou ne soit détenu et que le pool
  ordonnance la sonde rapidement : un pool saturé produisait donc des échecs
  qui se lisaient comme de la contention de verrou. Les deux côtés s'exécutent
  désormais sur des threads dédiés et la preuve est un `Thread.Join` avec
  timeout.

Préférez cette voie lorsque la cause tient aux hypothèses d'ordonnancement du
test lui-même plutôt qu'à une latence réelle de l'environnement : un marqueur
masque le test, une réécriture conserve la couverture dans la chaîne bloquante.

## Lire la latence d'attente du terminal dans un log de CI

`tests/Heimdall.Terminal.Tests` borne ses attentes de processus fils par un
garde-fou partagé de 60 secondes. Cette valeur a été portée de 10 secondes à 60
après une exécution partie en timeout alors que le fils était toujours vivant
et que `receivedBytes=0`. L'augmentation a fait cesser les timeouts, et a fait
disparaître la preuve avec eux : une attente qui échouait auparavant à 10
secondes se termine désormais à 45 et l'exécution est verte sans laisser de
trace. Une exécution verte sous la borne élargie ne permet donc pas de
distinguer entre "le blocage a disparu" et "le même blocage tient désormais
dans la borne élargie".

Toute attente bornée par ce garde-fou passe par `TerminalWaitObservation`, qui
publie une ligne sur la sortie standard lorsqu'une attente se **termine** après
avoir dépassé 10 secondes, **y compris lorsqu'elle se termine avec succès** :

```
TERMINAL_WAIT_OVER_LEGACY_BOUND caller=Write_InputReachesProcessStdin awaited=ProcessExited elapsedMs=12500.000 legacyBoundMs=10000.000 outcome=completed
```

La ligne est émise depuis un `finally` : elle marque donc une attente qui s'est
terminée, pas l'instant où le seuil a été franchi. Une attente encore bloquée
au moment où le job est tué ne publie strictement rien.

La sortie console d'un test qui passe atteint le log de
`dotnet test --verbosity normal` : ces lignes s'accumulent donc dans le log du
workflow, sans collecteur ni upload d'artefact. Pour lire une exécution :

```bash
gh run view <run-id> --log | grep TERMINAL_WAIT_OVER_LEGACY_BOUND
```

- Aucune ligne, **dans une exécution dont l'étape de test est allée à son
  terme** : aucune attente n'a dépassé l'ancienne borne. Ne le lisez ainsi que
  sous cette condition. Dans une exécution tuée par le timeout du job,
  l'absence ne prouve rien, car l'attente qui était encore bloquée est
  précisément celle qui n'a jamais pu publier.
- Lignes avec `outcome=completed` : une attente a franchi l'ancienne frontière
  et la borne élargie l'a absorbée. L'exécution est verte, et ce vert n'est pas
  la preuve que la cause a disparu. Ce n'est pas non plus la preuve que la même
  attente aurait échoué sous l'ancienne implémentation.
- Lignes avec `outcome=unfinished` : l'attente s'est terminée sans son
  événement ; la `TimeoutException` associée porte l'instantané complet du
  processus.

### Premières observations, exécution 31896183632

La première exécution de CI a rapporté quatre attentes terminées au-dessus de
l'ancien seuil d'observation de 10 secondes. Cela prouve que le garde-fou de 60
secondes peut encore absorber des attentes qui franchissent l'ancienne
frontière. Cela ne rejoue pas la course entre achèvement et timeout de
l'ancienne `WaitAsync(10 seconds)` : cela ne permet donc pas d'établir que
chaque observation aurait échoué sous cette implémentation.

| caller | awaited | elapsedMs |
|---|---|---|
| `ProcessExited_ProcessEndsWithoutConsoleOutput_RaisesExitCode` | `ProcessExited` | 10040.230 |
| `PipeModeSession_DataReceivedSubscriberException_DoesNotStopReadLoop` | `ProcessExited` | 10390.218 |
| `ProcessExited_SubscriberAddedAfterFastExit_ReplaysExitCode` | `SessionStopped` | 10547.798 |
| `Write_InputReachesProcessStdin` | `ProcessExited` | 10564.036 |

Seules les attentes au-dessus du seuil sont rapportées. Cet échantillon de
quatre observations, filtré par un seuil, est donc insuffisant pour distinguer
entre un timer fixe, un retard d'ordonnancement, une bufférisation de sortie ou
une contention de ressource.

Le seul fait supplémentaire qui mérite d'être consigné est que ces quatre
observations se sont produites avec `System.Threading.ThreadPool.MinThreads`
à 64.

### Ne reconstruisez pas les événements à partir des horodatages de lignes GitHub Actions

L'horodatage d'une ligne de log de workflow date la capture et le multiplexage
de la sortie standard, pas l'appel qui a produit le texte. Dans cette exécution
précise, un marqueur `TERMINAL_WAIT` apparaît concaténé dans une ligne de
sortie de `TwinShell.Infrastructure.Tests`, ce qui montre directement que les
frontières et les instants de ligne ne sont pas ceux de l'appel émetteur.

Rien ne peut être dérivé de ces horodatages : ni des instants de départ, ni des
fins simultanées, ni des épisodes séparés par mécanisme, ni une ressource
contendue, ni un contexte de synchronisation nommé comme cause probable. Une
lecture antérieure de cette page a fait exactement cela, et elle avait tort.

Une passe ultérieure qui voudrait analyser les recouvrements devra d'abord
émettre à la source des instants de début et de fin monotones, avec
l'identifiant de processus et un identifiant de séquence, et lire ceux-là.

`TerminalWaitInstrumentationGuardTests` refuse toute nouvelle attente qui
atteint directement la constante du garde-fou, parce qu'une instrumentation
contournée ne mesure rien alors que la suite reste verte dans les deux cas.
Utilisez `TerminalTestHelpers.AwaitProcessEventAsync`, `SpinUntilProcessEvent`
ou `PollUntilProcessEventAsync`.

Il s'agit de mesure, pas de correctif. Ce dispositif existe pour que la cause
puisse être trouvée à partir de la distribution réelle, au lieu d'être devinée.

## Exécution en local

`Test.bat` et `dotnet test Heimdall.slnx` (sans filtre) exécutent la suite
complète, tests marqués inclus. Ils doivent passer.

Pour reproduire le comportement de la CI en local :

```powershell
dotnet test Heimdall.slnx --filter "Category!=CIUnstable&Category!=RequiresDesktop"
dotnet test Heimdall.slnx --filter "Category=CIUnstable"
dotnet test Heimdall.slnx --filter "Category=RequiresDesktop"
```

## Quand retirer un marqueur

Retirez le trait `CIUnstable` dès que l'une des conditions suivantes est
remplie :

- L'image du runner (ou sa liste d'exclusions Defender) est mise à jour et les
  tests passent trois exécutions de CI d'affilée sans reprise.
- Le test est réécrit pour ne plus dépendre du timing d'E/S inter-processus
  (par exemple en remplaçant le named pipe par un transport en mémoire, ou en
  pilotant la mise à jour du binding WPF de manière synchrone depuis le thread
  de test).
- Le test est supprimé car obsolète.

Retirer le trait sans l'un de ces changements réintroduira la rougeur
intermittente de la CI.
