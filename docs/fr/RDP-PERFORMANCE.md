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
réglages changent réellement ce chiffre. Tout ce qui suit a été mesuré, sur une cible Windows
Server 2022, en août 2026. Quand un réglage ne sert à rien, la page le dit, plutôt que de
répéter un conseil qui sonne juste.

## Ce que coûte une session

| Élément | Commit privé |
|---|---:|
| Heimdall lancé, aucune session ouverte | environ 194 Mo |
| Première session : initialisation unique du contrôle | +68 Mo |
| Chaque session, la première comprise | +194 Mo |

Une session se situe donc vers 456 Mo, deux vers 650 Mo, trois vers 844 Mo. Ces deux derniers
chiffres sont extrapolés d'un coût marginal mesuré une seule fois, pas mesurés directement.

En 1920x1080, une session coûte environ 80 Mo de plus qu'en petite fenêtre.

**L'essentiel de cette mémoire n'appartient pas à Heimdall.** Elle appartient à `MsTscAx`, le
contrôle ActiveX RDP de Microsoft, que Heimdall héberge dans son propre processus. C'est le
même contrôle qu'utilise `mstsc.exe`.

## Heimdall face au client natif

Mesuré sur la même cible, le même profil, avec `mstsc.exe` lancé par Heimdall en mode externe :

| Sessions | Heimdall | N mstsc.exe séparés |
|---:|---:|---:|
| 1 | 456 Mo | **328 Mo** |
| 2 | 650 Mo | 656 Mo |
| 3 | **844 Mo** | 983 Mo |

Une session coûte moins cher dans le client natif, parce que Heimdall porte son propre socle
applicatif. À partir de trois sessions, Heimdall coûte moins cher, parce que chaque `mstsc.exe`
séparé repaie intégralement son socle de processus là où Heimdall l'amortit entre les onglets.
Le croisement se situe à deux sessions.

Si vous gardez habituellement une seule session ouverte, le client natif consomme moins. Si
vous en gardez plusieurs, Heimdall consomme moins.

## Le seul réglage qui fonctionne

**La résolution.** C'est le seul réglage dont l'effet a été mesuré au-dessus du bruit. Passer
de 1920x1080 à une session plus petite a économisé environ 86 Mo.

Dans un profil de serveur, onglet RDP, mettez **Mode de résolution** sur `Fixed` et choisissez
une taille inférieure à votre écran, ou laissez `Auto` et réduisez la fenêtre de Heimdall. Les
deux réduisent la géométrie négociée de la session.

C'est un vrai compromis : une session plus petite est un bureau distant plus petit pour
travailler. Rien ici ne donne de la mémoire gratuitement.

## Les réglages qui ne fonctionnent pas

**La profondeur de couleur.** Passer de 32 à 16 bits n'a rien économisé de mesurable. L'écart
tombait sous le bruit de mesure d'un lancement à l'autre, et son signe était défavorable.
Baissez-la si vous manquez de bande passante, pas si vous manquez de mémoire.

**Le cache bitmap.** La case ne fait pas ce que son nom suggère. Elle pilote la propriété
`BitmapPersistence` du contrôle RDP, qui décide si le cache bitmap est écrit **sur disque**
entre deux sessions. Elle ne pilote pas le cache en mémoire, et aucun réglage de Heimdall ne le
fait. La décocher ne libère aucune RAM, et vous perdez le cache disque qui aurait épargné des
redessins à la reconnexion.

**La compression.** C'est un réglage de bande passante. Aucun effet mémoire n'a été mesuré, et
aucun mécanisme n'en prévoit.

## Lire un chiffre de mémoire sans se tromper

Trois pièges nous ont coûté du temps en produisant les chiffres ci-dessus. Ils vous coûteront
la même chose si vous refaites la mesure.

**La colonne "Mémoire" du Gestionnaire des tâches n'est pas une quantité stable.** Windows
rogne le working set d'une fenêtre qui n'est pas au premier plan. Nous avons vu le même
processus inchangé afficher 104 Mo puis 38 Mo, alors que la mémoire réellement détenue n'avait
pas bougé. Comparez plutôt le commit privé, visible dans l'onglet Détails sous "Taille de la
validation".

**Le Gestionnaire des tâches agrège les processus enfants dans la ligne de l'application.**
Heimdall crée des processus WebView2 pour les panneaux terminal et navigateur de fichiers. Un
chiffre lu sur la ligne groupée porte sur l'arbre entier, pas sur l'application.

**Une session connectée n'est pas une session ouverte.** Un client resté sur un écran de
connexion coûte une fraction d'un bureau réel. Si vous comparez deux mesures, assurez-vous que
les deux sont dans le même état.

## Mesurer par vous-même

Le harnais utilisé pour cette page est dans le dépôt, à
`local/scripts/Measure-RdpMemory.ps1`. Il échantillonne le commit privé, le working set, les
handles et les threads, compte les connexions RDP établies pour que les paliers se segmentent
seuls, et enregistre si la fenêtre était au premier plan pour que les échantillons rognés
restent identifiables.

```powershell
pwsh -File local/scripts/Measure-RdpMemory.ps1 -ProcessName Heimdall -DurationMinutes 20
```

Fondez vos conclusions sur le **delta** entre paliers, jamais sur la valeur absolue de la
ligne de base. Sur des lancements identiques, nous avons mesuré des bases allant de 191 à
214 Mo, soit 23 Mo d'amplitude, alors que le delta entre paliers se reproduisait à 3 Mo près.

## Voir aussi

- [FAQ des réglages](SETTINGS-FAQ.md) - ce que fait chaque option de Settings.
- [Dépannage](TROUBLESHOOTING.md) - pannes précises.
