<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

*Ce document est la version française de [../DEVELOPMENT.md](../DEVELOPMENT.md). / This document is the French version.*

# Guide de développement

Référence de développement commune pour Heimdall. Ce fichier fait partie de la
documentation versionnée du projet : n'y placez ni chemins locaux à une machine,
ni identifiants, ni préférences d'éditeur, ni notes de travail temporaires.

## Vue d'ensemble du projet

Heimdall est un gestionnaire de connexions Windows en .NET 10 / WPF, destiné aux
sessions RDP, SSH, SFTP, VNC, Telnet, FTP, Citrix et shell local. L'application
s'appuie sur MVVM via CommunityToolkit.Mvvm, sur SSH.NET 2025.1.0 pour SSH/SFTP,
sur WebView2 pour les terminaux embarqués et le VNC, et sur DPAPI + HMAC-SHA256
pour la protection locale des identifiants.

Le fichier de solution est `Heimdall.slnx`. Les projets sources se trouvent sous
`src/` et les projets de tests sous `tests/`.

## Compilation, tests et exécution

Commandes courantes :

```powershell
dotnet build
dotnet test
dotnet run --project src/Heimdall.App
powershell -File Build.ps1
powershell -File Build.ps1 -Mode Release
powershell -File Build.ps1 -Mode Release -Publish
powershell -File Build.ps1 -Mode Release -DryRun
powershell -File Build.ps1 -Mode Release -Version 2026.033101
powershell -File Build.ps1 -SkipTests
```

Des raccourcis batch sont également disponibles :

- `Run.bat` - compilation et lancement
- `Test.bat` - exécution des tests
- `Build.bat` - compilation debug
- `Release.bat` - chaîne de release

La référence actuelle pour la suite complète est :

```powershell
dotnet test Heimdall.slnx --no-build
```

Résultat attendu : 5 453 tests réussis et 6 tests WPF `ThemeServiceTests`
volontairement ignorés, car ils nécessitent un contexte `Application` actif.

Les résumés TRX par projet peuvent masquer les tests ignorés ou annoncer des
totaux plus faibles. Utilisez la commande agrégée au niveau de la solution pour
mesurer la vraie référence.

## Pièges du script de build

`Build.ps1 -SkipTests` saute la passe de tests, mais saute aussi la compilation
des assemblies de tests. Enchaîner immédiatement sur `dotnet test --no-build`
peut exécuter des binaires obsolètes, voire ne trouver aucun test.

Lorsque vous itérez sur les tests après une compilation sans tests, lancez :

```powershell
dotnet build Heimdall.slnx -c Debug -p:nodeReuse=false
dotnet test Heimdall.slnx --no-build
```

Le script de build met à jour les métadonnées de version de
`src/Heimdall.App/Heimdall.App.csproj` avant de compiler. Si vous lancez
`Build.ps1` au cours d'une passe de développement et que vous ne souhaitez pas
conserver la version incrémentée automatiquement, restaurez les valeurs du
fichier projet avant de committer.

## Conventions de version

`Heimdall.App.csproj` porte :

- `<Version>1.0.MMDD.xx</Version>`
- `<InformationalVersion>YYYY.MMDDxx</InformationalVersion>`

`Build.ps1` incrémente automatiquement `xx` en fonction des dossiers de
distribution existants, de la version courante du projet et, quand elles sont
disponibles, des tags de release GitHub récents. L'option `-Version` court-circuite
l'incrémentation automatique et attend le format `YYYY.MMDDxx`.

Les sorties de build sont ignorées par Git et écrites sous :

- `Dist/debug/`
- `Dist/release/`
- `Dist/installers/`

## Standards de code

- Licence : Apache 2.0, auteur "Julien Bombled" sur les nouveaux fichiers.
- Le code, les commentaires et la documentation versionnée sont en anglais.
- Les types référence nullables sont activés sur l'ensemble des projets.
- Les avertissements sont traités comme des erreurs via `Directory.Build.props`.
- Privilégier les API asynchrones et ne jamais bloquer le thread UI.
- Garder la logique WPF dans les ViewModels ; le code-behind doit se limiter au
  câblage minimal des événements, sauf si l'intégration plateforme impose
  autre chose.
- Les chaînes visibles par l'utilisateur appartiennent à `locales/en.json` et
  `locales/fr.json`.
- Les arguments de shell doivent passer par `InputValidator.EscapeShellArg()` ou
  par une API d'arguments structurée telle que `ProcessStartInfo.ArgumentList`.
- Préférer les patterns et les API utilitaires déjà présents dans le projet
  plutôt que d'introduire de nouvelles abstractions.

## Conventions de documentation

Trois catégories, et un fichier appartient à exactement l'une d'elles.

**Les documents privés et de travail** vivent dans `local/` à la racine du dépôt, ignoré par git.
Briefs d'agents, audits en cours, notes de reprise, captures de diagnostic, journaux d'opérations.
Rien de ce qui s'y trouve n'est publié.

**Les documents publics** sont versionnés et existent en deux langues. L'anglais reste à son
emplacement habituel (`README.md`, `docs/*.md`) ; le français vit en miroir (`README.fr.md`,
`docs/fr/*.md`). Chaque version pointe vers l'autre sous son titre. Une modification de l'une n'est
pas terminée tant que l'autre ne dit pas la même chose.

**Les notes de version publiées** sont en anglais par défaut, dans `docs/release-notes/v<version>.md`.

`docs/CHANGELOG.md` reste en anglais seul. C'est un journal chronologique d'ingénierie, du même
genre que les notes de version, et le traduire imposerait une double maintenance perpétuelle sans
lecteur.

### Caractères

ASCII uniquement, avec une seule exception : les lettres accentuées françaises produisibles au
clavier AZERTY, dans les documents français.

Proscrits partout, y compris en anglais :

| Au lieu de | Écrire |
|---|---|
| tiret cadratin, tiret demi-cadratin | `-` |
| guillemets et apostrophes courbes | `"` et `'` |
| points de suspension en un caractère | `...` |
| espace insécable ou fine | une espace ordinaire |
| flèches Unicode | `->` et `<->` |
| caractères de tracé de boîte | `+--` et `\--` |
| multiplication, moins, supérieur ou égal Unicode | `x`, `-`, `>=` |

Ces caractères cassent dans les terminaux Windows, les diffs, les pages de code console et les
journaux de CI. C'est la même raison qui impose les échappements `\uXXXX` dans les fichiers
de locale.

`scripts/NotesTypographyGuard.ps1` fait autorité sur le jeu banni et est exécuté par
`Build.ps1 -Mode Release` contre les notes de version, en échec bloquant.

## Localisation et i18n

Les fichiers de locale contiennent actuellement 5 489 clés feuilles chacun, et la
CI impose la parité des clés EN/FR.

Conventions de nommage :

- Clés en CamelCase par contexte, par exemple `ErrorPlinkNotFound` ou
  `BtnConnect`.
- Le nouveau XAML doit privilégier `{loc:Translate Key}` pour une mise à jour de
  la locale en direct.
- Les anciens chemins impératifs `ApplyLocalization()` subsistent ; la migration
  est incrémentale.
- Les descriptions d'outils peuvent utiliser `ToolDescriptor.DescriptionKey` ;
  sinon la convention par défaut est `ToolDesc{Id}`.

## Conventions de namespaces

Avant de créer un nouveau namespace sous `Heimdall.Core.*`, confrontez le nom
choisi aux namespaces .NET de premier niveau tels que `System`, `IO`, `Net`,
`Threading`, `Linq`, `Text`, `Collections`, `Diagnostics`, `Security`, `Runtime`
et `Globalization`.

En cas de collision, choisissez un namespace propre au projet et sans ambiguïté,
puis alignez le chemin du dossier dessus. Exemple : utiliser
`Heimdall.Core.SystemInfo` avec `src/Heimdall.Core/SystemInfo/`, et non
`Heimdall.Core.System`.

## Où trouver quoi

- `src/Heimdall.Core/` - modèles partagés, configuration, sécurité,
  localisation, machine à états, découverte, validation.
- `src/Heimdall.Ssh/` - intégration SSH.NET, Pageant, repli Plink, confiance des
  clés hôtes, gestionnaire de tunnels.
- `src/Heimdall.Sftp/` - implémentations SFTP/FTP de `IRemoteBrowser` et édition
  de fichiers distants.
- `src/Heimdall.Rdp/` - hôte ActiveX RDP et utilitaires de remplissage
  automatique des identifiants.
- `src/Heimdall.Terminal/` - shell local, mode pipe, Telnet, abstractions de
  terminal.
- `src/Heimdall.App/` - racine de composition WPF, services, handlers, view
  models, vues, thèmes, localisation.
- `src/TwinShell.*` - persistance de la bibliothèque de commandes, modèles de
  base et intégration.
- `tests/` - projets de tests xUnit correspondant aux zones du code source.
- `docs/ARCHITECTURE.md` - architecture générale et décisions de conception.
- `docs/SECURITY.md` - modèle de menace, contrôles de sécurité, limitations et
  références des tests de sécurité.
- `docs/TOOLS.md` - catalogue des outils intégrés, registre d'outils, fournisseurs
  externes, SecNumCloud et référence de la Command Library.
- `docs/TROUBLESHOOTING.md` - problèmes connus de développement/exécution et
  leurs correctifs.

## Attentes vis-à-vis de la CI

La CI doit imposer :

- restore/build/test avec les avertissements traités comme des erreurs ;
- la suite de tests complète de la solution ;
- la parité des clés de locale JSON ;
- les contrôles de formatage ;
- une analyse informative des paquets vulnérables.

Commande de revue manuelle des dépendances :

```powershell
dotnet list Heimdall.slnx package --vulnerable --include-transitive
```

Les analyses de vulnérabilités peuvent remonter des avis sans chemin de mise à
niveau immédiat ; examinez les résultats avant d'en faire des points bloquants
pour une release.
