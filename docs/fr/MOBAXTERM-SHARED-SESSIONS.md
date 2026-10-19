<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - Sources de sessions MobaXterm partagées

*Également disponible en anglais : [../MOBAXTERM-SHARED-SESSIONS.md](../MOBAXTERM-SHARED-SESSIONS.md).*

Pour les équipes qui gardent leurs sessions dans un fichier MobaXterm `.mxtsessions` sur un dossier partagé. Heimdall lit ce fichier et affiche ses sessions, en lecture seule, à côté des vôtres. La référence complète est dans [FEATURES.md](FEATURES.md#import-et-migration).

---

## 1. Ajouter une source partagée

1. Ouvrez **Paramètres > Général > Sources partagées** et cliquez sur **Ajouter**.
2. **Nom** : le nom que l'arborescence affiche pour cette source, par exemple `Equipe`.
3. **Fichier** : le chemin du fichier `.mxtsessions`. **Parcourir...** le sélectionne, ou saisissez un chemin avec des variables d'environnement pour qu'il fonctionne pour tout le monde, par exemple `%OneDriveCommercial%\Equipe\sessions.mxtsessions`.
4. Laissez **Activée** cochée, puis **Enregistrer**.

La colonne **État** indique ce que la dernière lecture a trouvé : le nombre de sessions, ou pourquoi le fichier n'a pas pu être lu. **Recharger** relit les sources enregistrées. La source apparaît dans l'arborescence des sessions comme une racine à part, après vos dossiers, avec ses dossiers MobaXterm dessous.

## 2. Vérifier la variable de connexion

Les fichiers MobaXterm écrivent souvent l'identifiant sous forme de variable, comme `[DAS]`. Heimdall la remplace par la valeur définie dans **Paramètres > Général > Fichier et import > Variables d'import MobaXterm**. `DAS` vaut par défaut `%USERNAME%`, votre identifiant Windows. Si votre identifiant sur les serveurs est différent, changez la valeur à cet endroit ; la colonne **Devient** montre le résultat. Une variable sans valeur apparaît dans le rapport d'import (section 7).

## 3. Ce qui est en lecture seule, et pourquoi

Les sessions partagées appartiennent au fichier, que d'autres personnes maintiennent. Dans Heimdall, elles ne peuvent être ni modifiées, ni renommées, ni dupliquées, ni déplacées, ni réordonnées, ni supprimées, ni mises en favori, et rien ne se glisse dans un dossier partagé ni hors de lui. Un cadenas sur la ligne nomme la source.

Pour changer une session partagée, changez-la là où l'équipe la tient : dans MobaXterm, ou dans le fichier lui-même. Heimdall prend la modification en compte tout seul (section 6).

La connexion, l'ouverture en vue scindée, la copie de l'hôte et l'indicateur d'état fonctionnent comme pour vos propres sessions.

## 4. Première connexion : la commande de connexion

Une session partagée peut porter une commande de connexion, comme `sudo su - <compte>`, que MobaXterm saisit après la connexion. La première fois, Heimdall affiche la commande et demande avant de la saisir. Votre réponse est conservée sur votre poste, pour cette session et ces commandes exactes.

La question revient quand la commande change dans le fichier. Les approbations sont personnelles : elles ne sont jamais exportées avec les paramètres, et **Valeurs par défaut** les efface.

## 5. Mots de passe et clés SSH restent personnels

Un fichier partagé ne fournit jamais de mot de passe ni de clé : Heimdall ne les y lit pas, même quand MobaXterm en a enregistré. Les clés viennent de votre agent SSH (Pageant ou l'agent OpenSSH de Windows).

Quand votre agent ne propose aucune clé, Heimdall demande le mot de passe du bastion, puis celui du serveur, avant de se connecter. La question comporte une case **Mémoriser ce mot de passe sur cet ordinateur**, et chacun choisit, comme dans MobaXterm :

- **Cochée** : le mot de passe est conservé une fois que le serveur l'a accepté, et les connexions suivantes l'utilisent sans rien demander.
- **Non cochée** : le mot de passe sert pour cette connexion seulement.

Un mot de passe mémorisé n'appartient qu'à vous. Il reste sur cet ordinateur, chiffré pour votre compte Windows, dans `shared-session-secrets.json` dans le dossier de données de Heimdall. Il n'est jamais écrit dans le fichier partagé, dans la copie hors ligne, dans `servers.json` ni dans les paramètres, un export des paramètres ou des profils ne l'emporte jamais, et **Valeurs par défaut** ne le supprime pas.

Un mot de passe mémorisé appartient à un bastion (son hôte, son port et son identifiant) ou à une session (la session, son hôte, son port et son identifiant). Si le fichier partagé envoie une session vers un autre hôte ou un autre identifiant, Heimdall redemande au lieu d'envoyer l'ancien mot de passe. Quand un serveur refuse un mot de passe mémorisé, Heimdall l'oublie et redemande la fois suivante.

- Si vous utilisez un fournisseur d'identifiants externe, son mot de passe passe en premier : un mot de passe mémorisé ne complète que ce qu'il laisse vide. Pour une session partagée, le fournisseur reçoit l'entrée nommée d'après l'**hôte** de la session, pas d'après son nom : rangez vos entrées de coffre sous le nom d'hôte (par exemple `srv-app-01.corp.example`).
- Quand **Windows Hello** est exigé pour se connecter, il est demandé avant qu'aucun mot de passe mémorisé ne soit utilisé.
- La réouverture des sessions au démarrage et **Tout connecter** utilisent les mots de passe mémorisés mais ne demandent jamais rien : une session qui n'en a pas le demande quand vous l'ouvrez seule.
- Une session RDP demande son mot de passe dans la fenêtre RDP, que Heimdall ne mémorise pas. Le mot de passe de son bastion est mémorisé comme les autres.

Dans les paramètres, chaque source qui a des mots de passe mémorisés affiche **Oublier les mots de passe enregistrés**. Supprimer une source oublie aussi ses mots de passe. Rien de ce que vous saisissez n'est jamais écrit dans une session partagée, qui reste en lecture seule.

## 6. Rechargement automatique et copie hors ligne

Heimdall surveille chaque fichier source. Quand il change, cette source seule est relue un instant plus tard, l'arborescence suit, et vos dossiers dépliés et votre sélection restent. La ligne d'état indique combien de sessions la source contient désormais. Un rechargement ne ferme jamais une session ouverte.

Si le nouveau fichier ne peut pas être lu, ou ne contient soudain plus aucune session, les sessions précédentes sont conservées et la source indique pourquoi.

Après chaque lecture réussie, Heimdall garde une copie du fichier dans son dossier de données. Si le fichier est absent au démarrage (par exemple tant que le dossier partagé n'est pas encore synchronisé), les sessions sont lues depuis cette copie et la source affiche **copie hors ligne du** suivi d'une date. Supprimer la source supprime sa copie.

## 7. Lire le rapport d'import

Chaque lecture vérifie aussi le fichier à la recherche d'erreurs courantes. Quand elle en trouve, la racine de la source affiche un nombre, par exemple `3 problèmes`. Faites un clic droit sur la racine et choisissez **Afficher le rapport d'import**, ou utilisez **Rapport** dans la carte des paramètres. Le rapport liste, par dossier et par session :

- **Sessions ignorées** : les sessions non chargées, et pourquoi.
- **Variables non résolues** : un `[NOM]` sans valeur (section 2).
- **Nom et hôte différents** : une session nommée d'après une machine qui en joint une autre.
- **Passerelles inhabituelles** : une session qui ne passe pas par le bastion que prend la plupart de son dossier.
- **Hôtes en double** : deux sessions d'un même dossier qui joignent le même hôte.

Le rapport ne fait qu'informer : chaque session qu'il liste reste chargée telle que le fichier la décrit. **Copier** et **Exporter en CSV** permettent de l'envoyer à qui maintient le fichier.

## 8. Limites connues

- Seules les sessions SSH et RDP sont importées. Les sessions FTP, SFTP, VNC et Telnet d'un fichier MobaXterm ne sont pas encore importées ; elles apparaissent comme ignorées dans le rapport d'import.
- Les sessions partagées sont en lecture seule dans Heimdall (section 3).
