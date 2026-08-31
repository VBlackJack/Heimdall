<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

# Politique de signature de code

*Also available in English: [../CODE-SIGNING-POLICY.md](../CODE-SIGNING-POLICY.md).*

**Statut : aucune version de Heimdall n'est signée à ce jour.** Cette politique
énonce la gouvernance de la signature une fois celle-ci en place, et prend effet
avec la première version signée. D'ici là, tous les artefacts publiés sont non
signés et Windows SmartScreen les signalera. Vérifiez vos téléchargements avec le
fichier `SHA256SUMS.txt` publié à chaque version.

## Périmètre

Lorsque la signature sera active, les artefacts suivants seront signés :

- `Heimdall.exe` et `Heimdall.dll`
- les installeurs Inno Setup (`Heimdall_<version>_Standard_Setup.exe` et
  `Heimdall_<version>_SelfContained_Setup.exe`)
- le paquet MSI WiX (`Heimdall_<version>.msi`)

Seuls les artefacts construits depuis ce dépôt sont signés. Les binaires tiers que
Heimdall redistribue conservent la signature de leur propre éditeur et ne sont
jamais resignés ; ils sont listés dans
[THIRD-PARTY-NOTICES.fr.md](../../THIRD-PARTY-NOTICES.fr.md).

## Rôles

Heimdall est maintenu par une seule personne. Le fait est énoncé tel quel plutôt
que présenté comme une équipe, parce qu'il détermine la séparation des
responsabilités réellement disponible.

| Rôle | Qui |
|---|---|
| Committer | Julien Bombled (GitHub `VBlackJack`) |
| Relecteur | Julien Bombled |
| Approbateur de signature | Julien Bombled |

La même personne détient le dépôt source, pilote le processus de publication et
approuve chaque demande de signature. Les commits apparaissent sous deux identités
git (`Julien Bombled` et `VBlackJack`) ; les deux désignent ce mainteneur.

Si d'autres mainteneurs rejoignent le projet, ce tableau est mis à jour dans le
commit même qui leur accorde l'accès.

## Sécurité des comptes

Le mainteneur utilise l'authentification multifacteur sur le compte GitHub
propriétaire de ce dépôt ainsi que sur le compte du service de signature. Les
identifiants de signature ne sont jamais stockés dans le dépôt, dans la
configuration d'intégration continue, ni dans un artefact de construction.

## Construction des versions

Les versions sont construites localement par `Build.ps1 -Mode Release -Publish`,
et non par une chaîne hébergée. Le script compile la solution, publie les variantes
Standard et SelfContained, génère les installeurs Inno Setup et le MSI WiX, calcule
`SHA256SUMS.txt` à partir des sorties de construction réelles, puis crée la version
GitHub.

L'intégration continue compile et teste chaque poussée et chaque pull request, mais
ne produit aucun artefact publié et ne détient aucun identifiant de signature.

## Approbation

Chaque demande de signature est approuvée manuellement par le mainteneur nommé
ci-dessus. Aucun processus automatisé ne signe un artefact sans cette approbation,
et aucun artefact n'est signé depuis une branche non fusionnée dans `master`.

## Confidentialité

Cette politique porte sur la signature des artefacts publiés. Heimdall lui-même
n'envoie aucune télémétrie et ne collecte aucune donnée personnelle ; ce qu'il
stocke localement est décrit dans le [guide utilisateur](USER-GUIDE.md).
Candidater à un service de signature implique de communiquer l'identité du
mainteneur à ce service à des fins de validation. Aucune donnée utilisateur ne lui
est transmise, puisqu'aucune n'est collectée.

## Attribution

Une fois la signature active dans le cadre du programme SignPath Foundation, cette
section indiquera :

> Free code signing provided by [SignPath.io](https://signpath.io), certificate by
> [SignPath Foundation](https://signpath.org).

Elle est rédigée au futur volontairement : l'attribution n'est pas encore vraie, et
la publier avant acceptation serait une fausse déclaration.

## Signaler un problème

Si vous obtenez un artefact Heimdall dont la signature est absente, invalide ou non
conforme à cette politique, ouvrez un ticket sur
https://github.com/VBlackJack/Heimdall/issues. Si vous pensez qu'un artefact signé
a été altéré, dites-le dans le ticket et pas seulement par courriel, afin que le
signalement soit public et daté.
