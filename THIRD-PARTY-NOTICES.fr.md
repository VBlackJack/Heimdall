<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

# Mentions relatives aux composants tiers

*Also available in English: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).*

Heimdall est distribué sous licence Apache 2.0 (voir [LICENSE](LICENSE)). Il
redistribue les composants tiers listés ci-dessous, chacun sous sa propre licence.
Ce fichier couvre ce qui est livré à l'utilisateur. Les composants servant
uniquement à construire ou à tester Heimdall figurent à part, en fin de fichier,
et ne sont pas redistribués.

Chaque licence enregistrée ici a été lue dans le composant lui-même, jamais
déduite : les licences NuGet proviennent de l'élément `<license>` du `.nuspec` de
chaque paquet, et les composants vendorisés de leur texte de licence amont. Voir
la section "Comment ce fichier est vérifié" en fin de document.

## Composants vendorisés

Ils sont versionnés dans le dépôt et livrés dans l'installeur.

| Composant | Version | Éditeur | Licence | Amont |
|---|---|---|---|---|
| PuTTY `plink.exe` | Release 0.83 | Simon Tatham | MIT | https://www.chiark.greenend.org.uk/~sgtatham/putty/ |
| gsudo `gsudo.exe` | 2.5.1 | Gerardo Grignoli | MIT | https://github.com/gerardog/gsudo |
| draw.io embed | 26.0.9 | JGraph Ltd | Apache-2.0 | https://github.com/jgraph/drawio |
| Microsoft Edge WebView2 SDK | 1.0.2903.40 | Microsoft Corporation | Propriétaire, redistribuable | https://developer.microsoft.com/microsoft-edge/webview2/ |

PuTTY est sous copyright 1997-2026 Simon Tatham. Seul `plink.exe` est redistribué,
pas la suite PuTTY complète.

L'arborescence draw.io sous `src/Heimdall.App/Assets/drawio/` est un sous-ensemble
élagué de la distribution amont ; ce qui a été retiré, et pourquoi, est consigné
dans [VENDORED.md](src/Heimdall.App/Assets/drawio/VENDORED.md).

### Le seul composant non OSI

Les trois assemblages WebView2 de `src/Heimdall.App/lib/webview2/`
(`Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.Wpf.dll`,
`WebView2Loader.dll`) sont des redistribuables Microsoft. Ils sont librement
redistribuables selon les termes de licence du SDK Microsoft Edge WebView2, mais
cette licence est propriétaire et non approuvée OSI. Tous les autres composants
livrés par Heimdall portent une licence approuvée OSI.

Le point est signalé volontairement : les programmes de signature de code destinés
à l'open source demandent si le projet contient des composants propriétaires, et
WebView2 est la seule réponse honnête pour Heimdall.

## Paquets NuGet redistribués avec l'application

Références directes des projets livrés, sous `src/`.

| Paquet | Version | Licence |
|---|---|---|
| AvalonEdit | 6.3.1.120 | MIT |
| CommunityToolkit.Mvvm | 8.4.0 | MIT |
| FluentFTP | 54.2.0 | MIT |
| JsonSchema.Net | 7.0.4 | MIT |
| Konscious.Security.Cryptography.Argon2 | 1.3.1 | MIT |
| LibGit2Sharp | 0.31.0 | MIT |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.9 | MIT |
| Microsoft.Extensions.Caching.Memory | 10.0.9 | MIT |
| Microsoft.Extensions.DependencyInjection | 10.0.9 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.9 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.9 | MIT |
| Polly | 8.2.1 | BSD-3-Clause |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Apache-2.0 |
| SSH.NET | 2026.0.0 | MIT |
| Serilog | 3.1.1 | Apache-2.0 |
| Serilog.Sinks.Console | 5.0.1 | Apache-2.0 |
| Serilog.Sinks.File | 5.0.0 | Apache-2.0 |
| System.Management | 10.0.9 | MIT |
| System.Security.Cryptography.ProtectedData | 10.0.11 | MIT |
| ThemeForge.Theme | 2.1.0 | Apache-2.0 |
| YamlDotNet | 16.3.0 | MIT |

`ThemeForge.Theme` est publié par l'auteur de Heimdall et est lui-même sous
Apache-2.0.

Dépendances transitives comprises, 44 paquets distincts atteignent l'application
livrée. Le tableau ci-dessus liste les références directes ; la fermeture
complète, avec les versions résolues à la construction, s'obtient par :

```bash
dotnet list src/Heimdall.App/Heimdall.App.csproj package --include-transitive
```

## Non redistribués : construction et tests uniquement

Ces paquets sont référencés par les projets sous `tests/` et n'atteignent jamais
la machine d'un utilisateur. Ils sont listés par souci d'exhaustivité, non au
titre d'une redistribution.

| Paquet | Version | Licence |
|---|---|---|
| FlaUI.Core | 5.0.0 | MIT |
| FlaUI.UIA3 | 5.0.0 | MIT |
| FluentAssertions | 6.12.2 | Apache-2.0 |
| Microsoft.Extensions.TimeProvider.Testing | 10.8.0 | MIT |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| Xunit.StaFact | 1.1.11 | MS-PL |
| coverlet.collector | 6.0.4, 10.0.1 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |

## Comment ce fichier est vérifié

Les licences sont lues dans les composants, jamais supposées d'après leur
notoriété :

- Paquets NuGet : l'élément `<license type="expression">` du `.nuspec` présent
  dans le cache local. `LibGit2Sharp` est antérieur à cet élément et déclare
  `<license type="file">` ; sa licence a donc été lue dans le `LICENSE.md` livré
  à l'intérieur du paquet.
- `plink.exe` et `gsudo.exe` : la page de licence de l'éditeur, recoupée avec les
  métadonnées de version embarquées dans le binaire.
- draw.io : la version et la licence consignées dans `VENDORED.md`, confrontées à
  l'amont.

Revérifier ce fichier dès qu'une dépendance est ajoutée, retirée ou montée de
version majeure, et dès qu'un binaire sous `Assets/` ou `lib/` est rafraîchi.

Dernière vérification : 2026-08-31, sur le commit `9c4241d6`.
