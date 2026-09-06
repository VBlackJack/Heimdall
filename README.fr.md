<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

![Heimdall](docs/readme-banner.png)

# Heimdall

*Also available in English: [README.md](README.md).*

[![CI](https://github.com/VBlackJack/Heimdall/actions/workflows/ci.yml/badge.svg)](https://github.com/VBlackJack/Heimdall/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![Tests](https://img.shields.io/badge/tests-10%2C000%2B%20passing-brightgreen.svg)]()
[![Tools](https://img.shields.io/badge/tools-58%20sysops-blue.svg)]()
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)]()

**Une seule fenêtre pour toutes les machines dont vous vous occupez.**

Heimdall rassemble vos connexions distantes au même endroit : bureaux Windows, shells Linux,
transferts de fichiers, et le reste. Vous enregistrez une machine une fois, puis vous l'atteignez
d'un double-clic. Les mots de passe restent chiffrés sur votre propre ordinateur, et chaque session
s'ouvre dans un onglet de la même fenêtre plutôt que de s'éparpiller dans une demi-douzaine de
programmes.

C'est gratuit, open source, et cela tourne sur Windows 10 et 11.

---

## Ce à quoi vous pouvez vous connecter

| | Pour |
|---|---|
| **RDP** | Bureaux Windows, intégrés dans un onglet ou ouverts en plein écran |
| **SSH** | Serveurs Linux, commutateurs, pare-feu, tout ce qui a un shell |
| **SFTP** et **FTP** | Déplacer des fichiers, avec un navigateur à deux panneaux et le glisser-déposer |
| **VNC** | Écrans sous Linux, macOS, équipements divers |
| **Telnet** | Matériel réseau plus ancien |
| **Citrix** | Applications et bureaux publiés |
| **WinRM** | PowerShell sur des machines Windows distantes |
| **Shell local** | Un terminal sur votre propre machine, dans la même fenêtre |

---

## Ce que cela vous apporte

- **Tout dans une seule fenêtre.** Des onglets, et des vues partagées quand vous voulez deux
  machines côte à côte.
- **Des mots de passe que vous n'avez pas à retenir.** Stockés chiffrés, pour votre seul compte
  Windows. Vous pouvez ajouter un mot de passe maître par-dessus, ou laisser Windows Hello les
  déverrouiller avec votre empreinte.
- **Votre gestionnaire de mots de passe habituel, si vous préférez.** KeePassXC, Bitwarden,
  1Password et d'autres peuvent fournir les mots de passe à la place, et Heimdall ne les stocke
  alors jamais.
- **Des outils que vous iriez chercher ailleurs.** Ping, scanner de ports, inspecteur de
  certificats, générateurs d'empreintes et de mots de passe, et des dizaines d'autres, intégrés.
- **Des sessions rangées comme vous travaillez.** Des dossiers en couleur, du glisser-déposer
  pour déplacer un dossier ou ranger les sessions à la main, des filtres par protocole, favori,
  état de connexion ou passerelle, et une arborescence qui s'ouvre là où vous l'avez laissée.
- **Une confiance que vous pouvez auditer.** Les clés d'hôte SSH sont épinglées à la première
  connexion et affichées pour que vous les compariez ; une clé qui change est refusée par défaut,
  et votre `~/.ssh/known_hosts` s'importe et s'exporte pour que OpenSSH et Heimdall soient
  d'accord sur qui est qui.
- **Rien à installer à côté.** Les deux téléchargements sont autonomes.

Le catalogue complet se trouve dans la [référence des fonctionnalités](docs/fr/FEATURES.md).

---

## Téléchargement

Récupérez la dernière version sur la page [Releases](../../releases). Deux éditions, complètes
toutes les deux :

| Édition | Taille | À choisir quand |
|---|---|---|
| **Standard** | ~106 Mo installeur / ~159 Mo zip | Une machine Windows 10 ou 11 ordinaire. |
| **Self-Contained** | ~267 Mo installeur / ~380 Mo zip | La machine n'a pas Microsoft Edge, ou pas d'accès à Internet. |

> **Dans le doute, prenez Standard.** Elle fonctionne sur toute machine Windows 10 ou 11 disposant
> d'Edge, c'est-à-dire la quasi-totalité.

La page des releases propose aussi un `.msi`, et il arrive en tête de la liste. Il existe pour le
déploiement géré par GPO ou SCCM : il s'installe pour tous les utilisateurs de la machine,
demande des droits d'administrateur, et n'est pas mis à jour par le mécanisme interne de
Heimdall. À moins de le déployer à l'échelle d'une organisation, prenez plutôt un
`_Setup.exe` ou un `.zip`.

Chaque édition existe en **installeur** (raccourcis, mises à jour, désinstallation) ou en **zip**
(décompressez et lancez `Heimdall.exe`, rien n'est installé).

---

## Démarrer

**[Lisez le guide utilisateur](docs/fr/USER-GUIDE.md).** Il vous accompagne sur votre première
connexion, l'endroit où vivent vos mots de passe, le transfert de fichiers, la signification des
erreurs courantes, et la façon d'envoyer un journal si vous avez besoin d'aide.

La version courte : appuyez sur **Ctrl+N** pour ajouter une machine, choisissez le protocole,
renseignez l'adresse et vos identifiants, puis double-cliquez dessus dans la liste. **F1** affiche
à tout moment les raccourcis clavier, et **Ctrl+K** vous amène directement à une machine par son
nom ou son adresse.

---

## Documentation

| | |
|---|---|
| [Guide utilisateur](docs/fr/USER-GUIDE.md) | Commencez ici si vous utilisez Heimdall |
| [Référence des fonctionnalités](docs/fr/FEATURES.md) | Tout ce que fait Heimdall, protocole par protocole |
| [Outils](docs/fr/TOOLS.md) | La boîte à outils sysops intégrée |
| [FAQ des réglages](docs/fr/SETTINGS-FAQ.md) | Les options qui ne parlent pas d'elles-mêmes, et celle qui trompe |
| [Mémoire RDP](docs/fr/RDP-PERFORMANCE.md) | Ce que coûte une session, et le seul réglage qui le change |
| [Dépannage](docs/fr/TROUBLESHOOTING.md) | Pannes précises, rédigées pour un lecteur technique |
| [Sécurité](SECURITY.fr.md) | Comment les identifiants sont protégés, et comment signaler un problème |
| [Politique de signature](docs/fr/CODE-SIGNING-POLICY.md) | Qui approuve une signature, et ce qui est signé |
| [Développement](docs/fr/DEVELOPMENT.md) | Compiler, tester, contribuer |
| [Architecture](docs/fr/ARCHITECTURE.md) | Comment tout cela est agencé |
| [Journal des versions](docs/CHANGELOG.md) | Ce qui a changé, et quand |

Chaque document public existe en anglais et en français.

---

## Prérequis

Windows 10 ou 11. Rien d'autre n'est nécessaire : les deux éditions embarquent le runtime .NET.

Facultatif, et seulement si vous utilisez la fonctionnalité correspondante : PuTTY (pour les clés
SSH via Pageant), un serveur X11 comme VcXsrv (pour le déport X11), et Citrix Workspace App (pour
les sessions Citrix).

---

## Compiler soi-même

Double-cliquez sur `Run.bat` pour compiler et lancer, ou sur `Test.bat` pour exécuter la suite de
tests. Tout le reste, y compris les compilations de release et les installeurs, se trouve dans
[DEVELOPMENT.md](docs/fr/DEVELOPMENT.md).

---

## Licence

Copyright 2026 Julien Bombled

Distribué sous licence Apache 2.0. Voir [LICENSE](LICENSE) pour les détails.

Heimdall redistribue des composants tiers sous leurs propres licences. Voir
[THIRD-PARTY-NOTICES.fr.md](THIRD-PARTY-NOTICES.fr.md).
