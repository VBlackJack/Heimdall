<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - FAQ des réglages

*Also available in English: [../SETTINGS-FAQ.md](../SETTINGS-FAQ.md).*

Settings est un grand écran, et certaines de ses options sont opaques tant qu'on ne sait pas déjà
à quoi elles renvoient. Cette page traite celles-là : les ambiguës, celles qui portent un vrai
compromis, celle dont le libellé induit en erreur, et celles dont la formulation suppose un savoir
que l'interface ne donne jamais. Les options qui font ce que leur nom annonce, comme Thème ou
Taille de police, ne sont pas répétées ici.

Ctrl+, ouvre les Paramètres depuis n'importe où dans la fenêtre, comme un clic sur leur onglet.

Quand une réponse dit qu'un réglage ne fait pas quelque chose, c'est une affirmation mesurée ou
vérifiée dans le code, pas une supposition.

## Comment l'écran Paramètres enregistre

**Rien de ce que vous changez n'est écrit avant d'appuyer sur Enregistrer**, sauf les exceptions
que chaque carte indique. **Enregistrer** n'est actif que tant que des modifications attendent,
et Ctrl+S dans l'onglet Paramètres fait de même. Après un enregistrement, "Paramètres enregistrés"
s'affiche à côté des boutons jusqu'à votre modification suivante. **Annuler les modifications**
rend à chaque modification en attente sa valeur enregistrée. En attente veut dire différent de ce
qui est enregistré : changez une valeur puis remettez-la, et plus rien n'attend. Les modifications
des passerelles SSH et des outils externes restent en attente jusqu'à l'enregistrement ou
l'annulation.

Une carte dont les réglages n'attendent pas simplement l'enregistrement le dit sous son titre :

- **S'applique aussitôt, conservé à l'enregistrement** (Apparence) : la langue, le thème et
  l'accent s'affichent dès que vous les choisissez ; Annuler les modifications revient à
  l'apparence enregistrée.
- **S'applique aux sessions ouvertes après l'enregistrement** (Terminal) : un terminal déjà ouvert
  garde sa police et ses couleurs.
- **S'applique aux connexions ouvertes après l'enregistrement** (les valeurs par défaut SSH et RDP,
  et Sécurité de la connexion) : une connexion déjà ouverte garde ce avec quoi elle a été ouverte.
- **Enregistré aussitôt, sans passer par Enregistrer** (PIN d'application, Mot de passe maître,
  clés d'hôtes de confiance, certificats RDP de confiance) : ils s'écrivent sur-le-champ, et
  Annuler les modifications ne peut pas les reprendre.

Le jeton d'accès de la synchronisation Git est écrit quand vous quittez son champ : cliquer
ailleurs, appuyer sur Ctrl+S, passer à un autre onglet ou fermer Heimdall l'écrivent. Il n'est pas
écrit à chaque frappe, et Annuler les modifications ne peut pas le reprendre. Les autres champs de
la synchronisation Git attendent l'enregistrement.

**Deux réglages demandent confirmation avant de s'activer.** Activer le partage TFTP et
Enregistrer la transcription des sessions demandent chacun une confirmation quand vous appuyez sur
Enregistrer. Refuser n'écrit rien et laisse vos modifications en attente.

**Quand l'enregistrement est refusé**, le message nomme le réglage et le problème, la case en
erreur passe en rouge et son infobulle donne la raison, le bandeau dit combien de réglages
demandent votre attention, et le focus va au premier d'entre eux en ouvrant son onglet.

**Valeurs par défaut** ramène chaque onglet à sa valeur d'usine, sous forme de modifications en
attente : rien n'est écrit avant l'enregistrement. Il garde votre langue, votre thème et votre
accent, vos sessions et vos passerelles SSH, ainsi que votre mot de passe maître, votre PIN et
votre enrôlement Windows Hello. Il désactive en revanche le fournisseur d'identifiants externe,
les exigences Credential Guard et Windows Hello et le partage TFTP, et réinitialise le délai de
grâce Windows Hello, le délai de verrouillage automatique et la déconnexion au verrouillage ; la
confirmation les énumère. **Réinitialiser les valeurs RDP** ne touche que l'onglet RDP : le
délai de surveillance de connexion RDP, dans Avancé > Diagnostics, garde sa valeur.

**Recherche** - le champ au-dessus des onglets trouve un réglage par son libellé ou son aide,
infobulles comprises, sans tenir compte de la casse ni des accents. Il ne compte que ce qui est
affiché pour vous ("Résultat 2 sur 5") ; Entrée passe au résultat suivant, Maj+Entrée au
précédent, en ouvrant au passage l'onglet et toute section repliée. Ctrl+F dans l'onglet
Paramètres y place le curseur.

**Modifié** - un réglage dont la valeur diffère de sa valeur d'usine affiche un petit badge
"Modifié" après son champ, avec un bouton **Rétablir** à côté. Un lecteur d'écran lit le badge
"Modifié, valeur par défaut :" suivi de la valeur par défaut, et le bouton "Rétablir la valeur par
défaut :" avec la même valeur. Rétablir remet cette seule valeur par défaut, comme une
modification en attente tapée à la main : rien n'est écrit avant Enregistrer, et Annuler les
modifications la reprend. Les marques comparent ce que l'écran contient maintenant, modifications
non enregistrées comprises, au même fichier d'usine que celui de Valeurs par défaut. La langue, le
thème et l'accent sont marqués aussi, et Rétablir est le seul moyen de remettre leur valeur par
défaut, puisque Valeurs par défaut les garde. Les résolutions prédéfinies sont marquées comme une
liste entière. Trois choses n'ont pas de marque : le secret de déverrouillage du fournisseur
d'identifiants (un secret), la liste des outils externes (vos propres entrées, qu'une
réinitialisation supprimerait) et tout ce qui est enregistré aussitôt (PIN, mot de passe maître,
Windows Hello, listes d'approbation).

**Trouver les paramètres modifiés**, à côté du champ de recherche, recherche ce badge : le compte
est le nombre de réglages que vous avez changés par rapport à leur valeur par défaut, et Entrée
les parcourt. Ce n'est pas un filtre : les autres réglages restent affichés.

## Fichier de paramètres : exporter et importer

La carte **Fichier de paramètres** de l'onglet Général copie vos préférences vers un autre
ordinateur, ou en garde une sauvegarde.

**Exporter les paramètres...** écrit les réglages tels qu'ils ont été enregistrés en dernier, pas
les modifications en attente. Seuls les réglages que cet écran modifie sont exportés, si bien que
le fichier ne contient jamais de secret : ni mot de passe maître, ni PIN, ni jeton d'accès Git, ni
secret de déverrouillage du fournisseur d'identifiants, ni passerelle SSH, ni position de
fenêtre. Les réglages qui désignent un dossier de votre profil utilisateur
(chemins d'outils, dossier des journaux) appartiennent à cet ordinateur : Heimdall dit combien il
y en a et demande s'il faut les inclure.

**Importer des paramètres...** lit un fichier écrit par l'export. Tout autre fichier, ou une
version que ce Heimdall ne sait pas lire, est refusé et rien ne change. Sinon, il énumère les
réglages qui changeraient, chacun sous le libellé que lui donne cet écran, avec son onglet,
sa section, sa valeur actuelle et sa nouvelle valeur (par exemple "Terminal > Apparence du
terminal > Taille de police : 14 -> 18"), et demande. Si vous acceptez, ils sont chargés comme modifications en
attente et vérifiés comme des valeurs saisies : rien n'est écrit avant d'appuyer sur Enregistrer,
et Annuler les modifications rétablit tout. Un secret ajouté à la main dans le fichier est ignoré.

## Verrouiller Heimdall lui-même

Trois contrôles, dans le même écran, qui ne protègent pas la même chose.

**PIN d'application** est un verrou d'écran. Heimdall enregistre une empreinte de votre PIN et
compare ce que vous tapez au démarrage. Cela empêche quelqu'un qui s'assoit devant votre machine
déverrouillée de parcourir votre liste de serveurs. **Cela ne chiffre rien.** Vos mots de passe
enregistrés sont protégés par DPAPI que vous posiez un PIN ou non, et qui détient votre fichier
de configuration peut en retirer le PIN.

**Mot de passe maître** est du chiffrement. Ce que vous tapez passe par Argon2id pour dériver
une clé, et cette clé chiffre le trousseau. Sans lui, les secrets enregistrés sont illisibles, y
compris pour un programme tournant sous votre propre compte Windows. C'est celui-là qu'il faut
poser pour protéger vos identifiants au repos.

**Déverrouillage par Windows Hello** ne remplace ni l'un ni l'autre. Il se pose par-dessus le
mot de passe maître, pour déverrouiller d'une empreinte au lieu de le saisir. **Redemander le mot
de passe maître après**, juste en dessous, fixe pendant combien de jours Windows Hello peut
déverrouiller depuis la dernière saisie du mot de passe maître ; ensuite, le mot de passe maître
est demandé une fois. 0 ne le redemande jamais.

**Lequel vous faut-il ?** Si la crainte est un collègue devant votre poste laissé sans
surveillance, le PIN suffit. Si elle porte sur le fichier d'identifiants lui-même, seul le mot
de passe maître y répond. Les deux se cumulent, et poser un PIN en pensant obtenir le second
protège beaucoup moins qu'il n'y paraît.

**Verrouillage auto après inactivité** et **Déconnecter les sessions au verrouillage** demandent le mot de passe
maître : sans lui, il n'y a rien derrière quoi verrouiller l'espace de travail, et les deux restent
grisés, avec une ligne qui dit pourquoi, tant que le mot de passe maître n'est pas activé. 0
désactive le verrouillage automatique.

## Sécurité

**Vue d'ensemble de la sécurité** - la première carte de l'onglet Sécurité liste, une ligne
chacun, les choix de cet écran qui touchent à la sécurité : NLA et authentification stricte du
serveur en RDP, partage TFTP, transcription des sessions, politique d'exécution PowerShell, mot de
passe maître, verrouillage automatique, déconnexion au verrouillage, Credential Guard, Windows
Hello avant la connexion, vérification automatique des mises à jour et import de known_hosts au
démarrage. Elle montre ce que l'écran contient maintenant, modifications non enregistrées
comprises, et marque une ligne "Non enregistré" tant qu'elle diffère de ce qui est enregistré.
Une ligne précédée d'une icône d'avertissement est un choix documenté comme non sûr : NLA
désactivée, TFTP activé, transcription activée, politique d'exécution Bypass ou Unrestricted,
pas de verrouillage automatique avec le mot de passe maître actif, vérification des mises à jour
désactivée, ou import de known_hosts activé. Elle dit pourquoi, et **Aller au paramètre** ouvre
l'onglet, fait défiler jusqu'au réglage et y place le focus. La ligne au-dessus de la liste dit
combien demandent attention, et un lecteur d'écran l'annonce quand elle change. L'authentification
stricte du serveur désactivée est signalée sans être marquée : c'est le réglage par défaut de
Windows, qui avertit et demande.

**Network Level Authentication (NLA)** et **Authentification stricte du serveur** se trouvent dans
l'onglet RDP, sous Certificats, dans la carte Sécurité de la connexion.

**Network Level Authentication (NLA)** - la machine distante vous authentifie *avant* d'ouvrir
une session de bureau. Laissez-la activée. Ne la désactivez que pour des cibles qui ne savent
pas faire, comme la plupart des serveurs `xrdp` Linux, qui n'implémentent pas CredSSP du tout.
Sans NLA vous arrivez sur l'écran de connexion distant au lieu d'être connecté directement.
En mode externe sans NLA, Heimdall ne transmet pas votre mot de passe enregistré à
`mstsc.exe` : rien ne vérifie l'identité du serveur sur ce chemin, l'invite Bureau à distance
vous le demande donc, et un avis explique pourquoi.

**Authentification stricte du serveur** - refuse de se connecter si l'identité du serveur ne
peut pas être vérifiée. Désactivée par défaut, parce que beaucoup de serveurs RDP internes
utilisent des certificats auto-signés, invérifiables par construction. L'activer est plus sûr
et cassera les connexions vers ces serveurs.

**Exiger Credential Guard** - refuse d'ouvrir une session RDP *embarquée* si Windows Credential
Guard ne tourne pas sur **votre** machine. Cela protège les identifiants que votre poste délègue
au serveur distant. Le contrôle porte sur votre machine locale, pas sur la cible. Les sessions
RDP externes en sont exemptées.

**Exiger Windows Hello avant de se connecter** - demande votre facteur Windows Hello avant
qu'une connexion démarre. **Revérifier après** fixe la durée de validité d'un contrôle réussi,
pour ne pas être sollicité à chaque onglet.

## Identifiants depuis un coffre externe

**Utiliser un fournisseur d'identifiants externe** - permet à Heimdall d'obtenir un mot de passe
en exécutant une commande, typiquement le CLI d'un gestionnaire comme KeePassXC, Bitwarden ou
1Password.

**Commande de nom d'utilisateur (facultatif)** - celle-ci passe facilement inaperçue et elle
répond à une plainte fréquente. Sans elle, seul le *mot de passe* vient du coffre et le nom
d'utilisateur reste celui du profil. Renseignez-la et le nom est récupéré aussi, par une
seconde commande.

**Secret de déverrouillage** - transmis sur l'entrée standard de la commande, pour les coffres
qui doivent d'abord être déverrouillés. Bitwarden et 1Password exigent en plus une session
établie hors de Heimdall (`BW_SESSION`, `op signin`) ; Heimdall ne l'établit pas pour vous.

**N'utiliser que la première ligne de la sortie** - désactivé par défaut. Certains CLI
(KeePass2 KPScript, `pass`) impriment le secret suivi d'autres champs : activez-le pour ceux-là.
Laissez-le désactivé si votre mot de passe contient légitimement un saut de ligne.

**Délai d'expiration de la commande** - combien de temps la commande de mot de passe peut tourner
avant que Heimdall y renonce, de 1000 à 120000 ms. Augmentez-le pour un coffre qui vous demande de
confirmer chaque requête.

L'entrée du coffre est cherchée par le **Nom d'entrée du coffre** du profil si vous en
renseignez un, et par le nom affiché du profil sinon. Renseignez-le quand l'entrée de votre
coffre ne porte pas exactement le même nom que celle de Heimdall.

## RDP et mémoire

**Rendu accéléré par le matériel** - **désactivé par défaut depuis la v2026.082401, et c'est le
réglage qui compte le plus.** Quand il est actif, le contrôle RDP construit un périphérique
Direct3D et son contexte de décodage pour chaque session ouverte. Trois sessions simultanées en
1920x1080 ont mesuré 1146 Mo avec, 763 Mo sans : un tiers de l'empreinte et 840 handles Windows
de moins. La contrepartie est que le décodage passe sur le processeur : aucune différence n'a été
mesurable sur des bureaux immobiles ni sur du texte qui défile, et une session affichant de la
vidéo n'a pas été mesurée. Réactivez-le, globalement ou pour un seul serveur, si une session
paraît moins fluide.

**Conserver le cache bitmap sur disque - à lire.** Cette case s'appelait "Cache bitmap", un nom
qui induisait en erreur, elle a donc été renommée. Elle décide si le cache bitmap est écrit
**sur disque** entre deux sessions, pour qu'une reconnexion le réutilise au lieu de tout
redessiner. **Elle ne pilote pas le cache en mémoire.** La décocher ne libère aucune mémoire et
vous coûte le cache disque. Laissez-la active.

**Profondeur de couleur** - 32 bits par défaut. La baisser à 16 bits n'a économisé aucune
mémoire mesurable. Baissez-la si vous manquez de bande passante, pas si vous manquez de
mémoire. Les valeurs acceptées sont 16, 24 et 32 : ce sont les profondeurs que le contrôle
Bureau à distance et le format `.rdp` connaissent, et une profondeur plus basse dans un fichier
importé (un profil mRemoteNG en 256 couleurs ou 15 bits, un `session bpp` sous 16) est ramenée
à 16, ce que la session recevait déjà avant que la borne ne le dise.

**Largeur, Hauteur** - la taille du bureau d'une nouvelle session, et les autres réglages dont
l'effet sur la mémoire a été mesuré : une session plus petite coûte environ 86 Mo de moins qu'en
1920x1080. Avec la **Résolution dynamique** active, la session suit plutôt la fenêtre de Heimdall.

**Préréglages de résolution** - les tailles qu'une session embarquée propose dans son menu de
résolution, une `LARGEURxHAUTEUR` par ligne. La liste est vérifiée quand vous quittez le champ : la
largeur doit être comprise entre 200 et 7680 et la hauteur entre 200 et 4320. Une ligne hors norme
est citée dans l'erreur et l'enregistrement est refusé jusqu'à sa correction ; rien n'est retiré
dans votre dos.

Les mesures complètes sont dans [Mémoire RDP et réglage des sessions](RDP-PERFORMANCE.md).

**Sessions embarquées maximum** - plafond de sessions embarquées simultanées. Augmentez-le si
vous en gardez beaucoup et que vous avez la mémoire ; chacune coûte environ 194 Mo.

**Résolution dynamique** - laisse la session se redimensionner avec la fenêtre au lieu de se
reconnecter. Laissez actif, sauf si un serveur se comporte mal quand la résolution change en
cours de session.

**Multi-écran** - étale la session sur vos écrans. Cela multiplie la géométrie de session, donc
le coût mémoire.

## Les délais, et pourquoi il y en a tant

Les délais avancés existent parce que des pannes différentes demandent des patiences
différentes. Vous n'avez presque jamais besoin d'y toucher.

**Délai de surveillance de connexion RDP** - combien de temps attendre avant de déclarer la
connexion en échec. Augmentez-le pour des serveurs lents ou lointains. 0 désactive la
surveillance.

**Délai de stabilisation de la résolution après connexion** - une pause avant d'autoriser le
redimensionnement, pour qu'une session encore en train de négocier sa géométrie ne soit pas
redimensionnée aussitôt. 0 supprime la pause.

**Délai de surveillance de l'autofill d'identifiants** - combien de temps Heimdall guette
l'invite d'identifiants d'une session mstsc *externe* pour la remplir. Sans effet sur les
sessions embarquées.

**Délai de nettoyage du fichier .rdp et des identifiants** - le mode externe écrit un fichier
`.rdp` temporaire et un identifiant ; c'est le délai avant suppression, pour laisser à
`mstsc.exe` le temps de les lire. Si Heimdall se ferme avant la fin du délai, les deux sont
supprimés à la fermeture ; après un plantage, ils sont récupérés au démarrage suivant ou au
prochain lancement externe.

**Contrôles Bureau à distance inactifs gardés pour réemploi** et **Expiration d'un contrôle
inactif** - quand un onglet RDP embarqué se ferme, son contrôle est gardé vivant pour que la
connexion suivante le réemploie au lieu de payer environ 66 handles noyau pour un nouveau ;
chaque contrôle inactif retient environ 300 Mo. Le premier réglage dit combien en garder (0 à 8,
0 crée un contrôle par session), le second combien de minutes un contrôle inactif est gardé avant
que sa mémoire soit rendue (0 le garde jusqu'à la fermeture de Heimdall). Les deux s'appliquent
sans redémarrage. Voir [RDP-PERFORMANCE.md](RDP-PERFORMANCE.md).

**Intervalle de maintien de session** et **Intervalle anti-inactivité**, dans l'onglet RDP,
sont deux choses différentes. Le maintien est du trafic protocolaire qui empêche le *serveur* de
couper une session inactive. L'anti-inactivité simule une activité pour que le *bureau* distant
ne se verrouille pas ; 0 la désactive.

**Intervalle de maintien SSH** (SSH & SFTP > Session, de 5 à 600 secondes) est l'équivalent SSH
du maintien : la fréquence à laquelle Heimdall envoie des messages de maintien SSH sur les
sessions terminal, SFTP, les tunnels et les passerelles, pour qu'un pare-feu ou le serveur ne
coupe pas une connexion inactive.

## Sondes en arrière-plan

**Activer les sondes de joignabilité en arrière-plan** - Heimdall ouvre périodiquement une
connexion TCP vers chaque serveur configuré pour colorer la pastille dans l'arbre des sessions.
C'est pourquoi les journaux d'un serveur montrent une connexion courte par intervalle depuis
votre poste, même sans session ouverte. Ce n'est pas une tentative de connexion et cela ne
s'authentifie pas.

**Intervalle de contrôle**, **Délai de sonde** et **Sondes simultanées maximum** règlent la
fréquence, la patience et le parallélisme. Désactivez l'ensemble si vous gérez beaucoup de
serveurs et que le bruit dans leurs journaux compte plus que les pastilles d'état.

## Modes

**Mode RDP par défaut** - `Embedded` rend la session dans un onglet Heimdall. `External` lance
`mstsc.exe` dans sa propre fenêtre, identifiants remplis pour vous (sauf sans NLA, voir Sécurité
plus haut). Le mode externe consomme
plus de mémoire par session mais isole chaque session dans son processus.

**Mode SSH par défaut** - `Embedded` utilise le terminal intégré. `External` utilise PuTTY, ce
qui exige de renseigner **Chemin de PuTTY**.

**Appliquer à toutes les sessions enregistrées** - à côté de chaque mode par défaut, réécrit le
mode de chaque session enregistrée de ce protocole (celui de SSH ne touche que les sessions SSH)
et enregistre en même temps le mode par défaut choisi. La confirmation dit combien de sessions
vont changer, et l'opération ne s'annule pas. Vos autres modifications en attente dans l'écran
Paramètres restent en attente.

## Journalisation

**Écrire le journal de diagnostic de l'application** écrit le journal propre à Heimdall : ses
événements et ses erreurs. **Enregistrer la transcription des sessions** enregistre en plus le
contenu des sessions terminal dans **Répertoire des journaux de session**. La seconde enregistre
ce que vous tapez et ce qui revient, mots de passe ou jetons renvoyés par le terminal compris :
réfléchissez à l'emplacement de ce répertoire ; Heimdall demande confirmation avant de l'activer.

**Réglages qui n'existent plus** - `EnableEventLog`, `EnableSessionPersistence` et
`EmbeddedIdleTimeoutMs` figuraient dans les réglages mais rien ne les lisait : les changer dans
`settings.json` ne faisait rien. Ils sont retirés. Un `settings.json` qui les contient encore se
charge normalement et les garde tels quels.

## Migration depuis l'ancienne version

**Ce que "legacy" désigne ici** - Heimdall a remplacé un outil PowerShell antérieur nommé
**RDPManager**. Cette section ne concerne que ceux qui l'ont utilisé. Si ce nom ne vous dit
rien, rien ici ne vous concerne.

Au premier démarrage, Heimdall cherche dans les dossiers voisins un répertoire `RDPManager`
contenant un `config/servers.json`, et propose de l'importer. Si vous refusez, il prend une
empreinte de ces données pour cesser de vous le demander.

**Proposer la migration au prochain démarrage** annule ce refus. Deux conditions doivent encore
être réunies au démarrage suivant, et c'est la partie qui surprend : l'ancien dossier doit
toujours être là, **et votre liste de serveurs actuelle doit être vide**. Heimdall ne propose
jamais de fondre un import dans un inventaire que vous avez déjà constitué. Cliquez avec des
serveurs déjà configurés et il ne se passera rien au démarrage suivant, sans message pour
l'expliquer.

## Partage de fichiers

**Activer le partage TFTP** (Sécurité > Partage de fichiers) ajoute un petit serveur TFTP au
partage d'un dossier, pour pousser des firmwares et des configurations vers du matériel réseau qui
ne parle rien d'autre. TFTP n'a aucune authentification ni aucun chiffrement : tant qu'un dossier
est partagé, qui atteint le port peut lire chacun de ses fichiers. Le serveur TFTP est en lecture
seule ; il n'accepte aucun envoi. Activez-le sur un réseau de confiance, le temps du transfert,
puis coupez-le.

Cocher la case ne change rien à elle seule : appuyer sur Enregistrer demande une confirmation, et
c'est seulement alors que TFTP est activé et qu'un partage en cours redémarre avec lui. Annuler
les modifications avant l'enregistrement laisse le partage tel qu'il était.

## Passerelles SSH, PuTTY et Plink

**Passerelles SSH** sont des rebonds. Vous déclarez une machine joignable, et Heimdall fait
transiter par elle les sessions vers des machines qui ne le sont pas directement. C'est le
réglage à chercher quand un serveur n'est accessible que depuis l'intérieur d'un réseau que vous
atteignez en SSH.

Renommer une passerelle se répercute aussitôt sur chaque session qui passe par elle : le badge
et l'infobulle dans l'arbre, le panneau de détail et la variable `{Gateway}` des outils
externes. Modifier une passerelle atteint aussi la liste "Via" de chaque outil réseau ouvert,
qui garde sa sélection et compose l'hôte modifié à sa prochaine exécution ; une exécution déjà
en cours garde le tunnel qu'elle a ouvert. Supprimer la passerelle sélectionnée remet l'outil en
connexion directe et le dit sur sa ligne d'erreur. Ce qu'une connexion ouverte affiche sur son
onglet, dans le panneau Tunnels et sur une question de certificat est le nom par lequel la
connexion a été établie, et le reste.

**Chemin de plink.exe** n'est nécessaire que pour les chemins passant par PuTTY : clés Pageant,
serveurs en keyboard-interactive, et le repli sur Plink. Des fichiers de clés seuls n'en ont pas
besoin. **Chemin de PuTTY** n'est nécessaire que si le mode SSH est réglé sur External ; laissé
vide, il est cherché à côté de plink.exe. Un chemin saisi là où il n'y a pas de fichier affiche
"Aucun fichier à cet emplacement." sous le champ ; un champ vide ne dit rien.

**Importer le fichier known_hosts d'OpenSSH au démarrage** (SSH & SFTP > Clés d'hôtes) ajoute les
clés d'hôtes de votre `.ssh/known_hosts` à la liste de confiance de Heimdall à chaque démarrage.
Une clé différente de celle à laquelle Heimdall fait déjà confiance n'est pas remplacée.

## Détection des outils tiers

**Répertoires Sysinternals, NirSoft et NanaRun** - Heimdall n'embarque pas ces suites. Indiquez
un dossier où vous en avez déjà installé une, et les outils qui s'y trouvent apparaissent dans
la boîte à outils. Laissez vide et Heimdall propose simplement ses outils intégrés. Un dossier
saisi là où il n'y en a pas affiche "Aucun dossier à cet emplacement." sous le champ.

## Éditeur externe

L'éditeur ouvert quand vous modifiez un fichier distant en SFTP. Laissé vide, Windows ouvre le
fichier avec ce qu'il associe à cette extension.

## Où vit la plage d'un réglage, et ce qui se passe en dehors

Chaque réglage numérique qui a une plage recommandée la déclare une seule fois, sur le réglage
lui-même, dans `AppSettings`. Le chargeur, l'écran de réglages, le message que l'écran affiche
et les deux traductions lisent cette déclaration unique ; aucun d'eux ne porte de nombre en
propre. Auparavant, la même borne était écrite à quatre endroits à la main, et elles avaient
divergé.

Les deux lecteurs font des choses différentes d'une valeur hors plage. L'écran de réglages
refuse de l'enregistrer et nomme la borne. Le chargeur, qui lit `settings.json`, garde la valeur
exactement telle qu'elle est écrite et journalise un avertissement qui le dit : un fichier écrit
par un Heimdall plus récent doit survivre à un plus ancien, donc le chargeur ne réécrit jamais
ce qu'il ne comprend pas. Qu'une valeur hors plage ait ensuite un effet se décide là où le
réglage est utilisé. Un réglage dont la valeur "désactivé" est zéro le déclare aussi, et zéro est
accepté sans avertissement.

## Voir aussi

- [Mémoire RDP et réglage des sessions](RDP-PERFORMANCE.md)
- [Guide utilisateur](USER-GUIDE.md)
- [Dépannage](TROUBLESHOOTING.md)
