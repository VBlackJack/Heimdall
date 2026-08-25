<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - Mémoire RDP et réglage des sessions

*Also available in English: [../RDP-PERFORMANCE.md](../RDP-PERFORMANCE.md).*

Cette page répond à une seule question : combien coûte une session RDP en mémoire, et quels
réglages changent réellement ce chiffre. Tout ce qui suit a été mesuré, sur des cibles Windows
Server 2022, en août 2026. Quand un réglage ne sert à rien, la page le dit, plutôt que de
répéter un conseil qui sonne juste.

## Ce que coûte une session

Mesure du 2026-08-24, trois sessions simultanées en 1920x1080 et 32 bits, commit privé sur
l'arbre de processus complet, chaque palier laissé converger :

| État | Avant v2026.082401 | À partir de v2026.082401 |
|---|---:|---:|
| Heimdall lancé, aucune session ouverte | environ 197 Mo | environ 197 Mo |
| Trois sessions | **1146 Mo** | **763 Mo** |
| Handles Windows, trois sessions | 3898 | 3058 |

L'écart tient à un seul réglage, décrit plus bas. Il vaut 383 Mo et 840 handles sur trois
sessions, soit un tiers de l'empreinte.

**L'essentiel de cette mémoire n'appartient pas à Heimdall.** Elle appartient à `MsTscAx`, le
contrôle ActiveX RDP de Microsoft, que Heimdall héberge dans son propre processus. C'est le même
contrôle que celui de `mstsc.exe`.

## Le réglage qui compte

**Rendu accéléré par le matériel.** Quand il est actif, le contrôle construit un périphérique
Direct3D et son contexte de décodage **pour chaque session ouverte**. C'est pour cette raison
que la mémoire croît avec le nombre de sessions et non avec le trafic qui y circule.

Il est **désactivé par défaut** depuis la v2026.082401. Il se trouve dans Paramètres, Bureau à
distance, et par serveur dans l'onglet Bureau à distance de ce serveur.

| Trois sessions | Commit privé | Handles |
|---|---:|---:|
| rendu matériel actif | 1145,9 Mo | 3898 |
| **rendu matériel désactivé** | **763,3 Mo** | **3058** |

**La contrepartie, dite franchement.** Le désactiver déporte le décodage de la carte graphique
vers le processeur. Sur des bureaux immobiles et sur du texte qui défile, aucune différence n'a
été mesurable, moins d'un demi pour cent d'un coeur dans les deux cas. **Une session affichant de
la vidéo ou une animation continue n'a pas été mesurée**, faute d'un moyen de piloter une image
en mouvement reproductible dans une session de test. Si une session paraît moins fluide
qu'avant, réactivez le réglage pour ce serveur.

## L'autre réglage qui marche

**La résolution.** Passer de 1920x1080 à une session plus petite a économisé environ 86 Mo par
session.

Dans un profil de serveur, onglet RDP, mettez **Mode de résolution** sur `Fixed` et choisissez
une taille inférieure à celle de votre écran, ou laissez-le sur `Auto` et réduisez la fenêtre
Heimdall. Les deux réduisent la géométrie négociée de la session.

C'est un vrai compromis : une session plus petite, c'est un bureau distant plus petit pour
travailler.

## Les réglages qui ne servent à rien

**La profondeur de couleur.** Passer de 32 bits à 16 bits n'a rien économisé de mesurable.

**Conserver le cache bitmap sur disque.** Cette case s'appelait "Cache bitmap" jusqu'à ce que
le nom soit jugé trompeur. Elle décide si le cache bitmap est écrit **sur disque** entre deux
sessions. Elle ne pilote pas le cache en mémoire. La décocher ne libère rien, et vous prive du
cache disque qui épargnerait quelques redessins à la reconnexion.

**L'étirement de l'image (smart sizing).** Le désactiver coûte environ 16 Mo de plus sur trois
sessions, pas de moins. Le redimensionnement se joue dans la couche de dessin de la fenêtre, pas
dans les tampons du contrôle.

**La compression.** C'est un réglage de bande passante. Aucun effet mémoire n'a été mesuré et
aucun mécanisme n'en produirait.

## La mémoire qui n'est pas rendue quand vous fermez un onglet

Heimdall garde jusqu'à deux contrôles RDP vivants après la fermeture de leurs onglets, pour que
la connexion suivante soit rapide et ne repaie pas une fuite interne à `mstscax.dll` qui coûte
environ 66 handles noyau par contrôle fraîchement créé. Chaque contrôle ainsi conservé retient
environ 300 Mo.

Mesure : la fermeture des trois sessions ramène le processus à 799 Mo et non à son socle de
197 Mo, et il y était encore vingt-cinq minutes plus tard. **La quantité est bornée, ce n'est pas
un emballement** : un second cycle d'ouverture et de fermeture n'a ajouté que 9,7 Mo, pas 600 de
plus.

En pratique, un Heimdall qui a servi se tient plus haut qu'un Heimdall fraîchement démarré.
Donner une expiration à ces contrôles inactifs est un travail prévu.

## Face aux autres clients

Face à `mstsc.exe`, lancé par Heimdall en mode externe sur la même cible : une session coûte
moins cher au client natif, parce que Heimdall porte son propre socle applicatif ; à partir de
trois sessions Heimdall coûte moins cher, parce que chaque `mstsc.exe` séparé repaie son propre
socle de processus alors que Heimdall l'amortit sur ses onglets. Le croisement se situe à deux
sessions.

Face à Devolutions Remote Desktop Manager Free 2026.2, mesuré sur les mêmes cibles dans la même
demi-heure, avant le changement de rendu matériel :

| Sessions | Heimdall | RDM |
|---:|---:|---:|
| 0 | 197 Mo | 347 Mo |
| 1 | 463 Mo | 463 Mo |
| 3 | 929 Mo | 682 Mo |

Les deux clients chargent le même `mstscax.dll`. L'écart tenait entièrement à la propriété de
rendu matériel, que RDM désactive sur son chemin par défaut et que Heimdall laissait au défaut du
contrôle. C'est cette comparaison qui a mené au changement ci-dessus.

## Lire un chiffre de mémoire sans se tromper soi-même

Quatre pièges ont coûté du temps réel pendant la production des chiffres de cette page. Ils vous
coûteront le même si vous vérifiez ce travail.

**Ce qui est affiché sur le bureau distant domine tout le reste.** Le même binaire, le même
protocole et les mêmes cibles ont mesuré 929 Mo à un moment de la journée et 1253 Mo cinq heures
plus tard, parce que Gestionnaire de serveur, Gestion de l'ordinateur, un éditeur de texte et une
console avaient été ouverts dans les sessions entre-temps. Plus de contenu peint, plus de
mémoire. **Ne comparez jamais deux mesures prises à des heures d'écart** ; comparez des bras
séparés de quelques minutes sur des bureaux identiques, sinon vous diagnostiquerez une régression
de code qui n'existe pas.

**La colonne "Mémoire" du Gestionnaire des tâches n'est pas une quantité stable.** Windows
rogne le working set d'une fenêtre qui n'est pas au premier plan. Le même processus inchangé a
rapporté 104 Mo puis 38 Mo, alors que la mémoire qu'il détenait n'avait pas bougé. Comparez le
commit privé, visible dans l'onglet Détails sous "Taille de la validation". À trois sessions le
working set ne sépare même pas Heimdall de RDM (355 Mo contre 353 Mo) alors que le commit privé
diffère de 248 Mo.

**Le Gestionnaire des tâches agrège les processus enfants dans la ligne de l'application.**
Heimdall lance des processus WebView2 pour le terminal et l'explorateur de fichiers. Un chiffre
lu sur la ligne groupée est l'arbre entier, pas l'application.

**Une session connectée n'est pas une session ouverte.** Un client resté sur l'écran de connexion
coûte une fraction d'un vrai bureau. Si vous comparez deux mesures, assurez-vous qu'elles sont
dans le même état.

## Le mesurer soi-même

Deux harnais sont livrés dans le dépôt. `local/scripts/Measure-RdpMemory.ps1` échantillonne une
famille de processus ; `local/scripts/Measure-RdpMemoryPair.ps1` en échantillonne deux sur le
même tick, ce qui est la condition pour qu'une comparaison avec un autre client ait un sens. Les
deux enregistrent le commit privé, le working set, les handles et les threads, comptent les
connexions RDP établies pour que les paliers se segmentent d'eux-mêmes, et notent si la fenêtre
était au premier plan pour que les échantillons rognés restent identifiables.

```powershell
pwsh -File local/scripts/Measure-RdpMemory.ps1 -ProcessName Heimdall -DurationMinutes 20
pwsh -File local/scripts/Measure-RdpMemoryPair.ps1 -RdpPort 3389 -DurationMinutes 30
```

Ancrez les conclusions sur le **delta** entre paliers, jamais sur le socle absolu. Sur des
lancements identiques, des socles allant de 189 Mo à 214 Mo ont été mesurés, soit 25 Mo
d'amplitude, alors que le delta entre paliers stabilisés se reproduisait à 3 Mo près.

Laissez chaque palier converger. Des valeurs lues au bout de cent secondes s'écartaient de plus
de cent mégaoctets du même palier lu au bout de quatre minutes, ce qui a suffi à inverser une
conclusion avant qu'elle ne soit rattrapée.

## Voir aussi

- [FAQ des réglages](SETTINGS-FAQ.md) - ce que fait chaque option de Settings.
- [Dépannage](TROUBLESHOOTING.md) - pannes précises.
