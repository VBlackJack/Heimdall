<!--
Copyright 2026 Julien Bombled
Licensed under the Apache License, Version 2.0
-->
*Ce document est la version française de [../SMOKE-TESTS.md](../SMOKE-TESTS.md). / This document is the French version.*

# Tests de fumée

Le dépôt embarque désormais un petit harnais de tests de fumée UIAutomation, dédié aux vérifications bureau à fort signal qui étaient jusqu'ici reconstruites au coup par coup à chaque refactorisation.

## Fichiers

- `scripts/smoke/uia-common.ps1`
  Fonctions partagées pour lancer Heimdall, sauvegarder `settings.json`, attendre des éléments UIA, cliquer sur des contrôles, lire le contenu des combos et des listes, et restaurer l'état.
- `scripts/smoke/settings-smoke.ps1`
  Test de fumée ciblé sur la page Paramètres.
- `scripts/smoke/navigation-a11y-smoke.ps1`
  Test de fumée ciblé sur l'accessibilité des onglets de navigation et des boutons de passerelle.
- `scripts/smoke/move-to-group-smoke.ps1`
  Test de fumée ciblé sur la parité du déplacement vers un groupe dans l'arbre des sessions (menu contextuel, conservation de l'expansion, validation de la destination, couverture de l'accessibilité de l'entrée sans groupe répartie entre vérifications UIA et vérifications humaines).
- `scripts/smoke/sidebar-favorites-smoke.ps1`
  Test de fumée ciblé sur les Favoris de la barre latérale (présence de la section, tri alphabétique, interaction avec le filtre, aller-retour de persistance ; les flux propres au ContextMenu sont partiellement délégués aux vérifications humaines, l'exposition des popups WPF sous UIA étant inconstante).

## Prérequis

- Une session bureau Windows avec UIAutomation disponible.
- Une build Debug de Heimdall sous `src/Heimdall.App/bin/Debug/...`.
- L'application doit pouvoir démarrer sans qu'une boîte de dialogue de premier lancement bloque la fenêtre principale.

### Environnement de test SSH Heimdall-TestEnv

L'environnement externe `G:\_Projects\Tests\Heimdall-TestEnv` alimente les profils de session
indépendamment des définitions de passerelle :

1. Démarrer les conteneurs TestEnv depuis `G:\_Projects\Tests\Heimdall-TestEnv`.
2. Importer `heimdall-import\servers.testenv.json` via l'importateur de sessions de Heimdall.
3. Injecter la passerelle TestEnv dans la configuration de build exacte utilisée par l'exécutable :

```powershell
& 'G:\_Projects\Tests\Heimdall-TestEnv\scripts\Inject-Gateway.ps1' `
  -SettingsPath 'G:\_dev\SnapConnect\Heimdall\src\Heimdall.App\bin\Debug\net10.0-windows\config\settings.json'
```

Redémarrer Heimdall après cette modification externe des paramètres. Les profils de serveur importés
référencent un `SshGatewayId` stable, mais l'objet passerelle lui-même est stocké dans `settings.json`
sous `AppSettings.SshGateways` ; n'importer que `servers.testenv.json` laisse la liste déroulante des
passerelles vide dans l'édition de session et les sessions TestEnv tunnelisées non résolues.

## Exécution

Depuis la racine du dépôt :

```powershell
pwsh -File .\scripts\smoke\settings-smoke.ps1
pwsh -File .\scripts\smoke\navigation-a11y-smoke.ps1
pwsh -File .\scripts\smoke\move-to-group-smoke.ps1
pwsh -File .\scripts\smoke\sidebar-favorites-smoke.ps1
```

Pour inclure au préalable une passe de build et de tests :

```powershell
pwsh -File .\scripts\smoke\settings-smoke.ps1 -RunBuild -RunTests
pwsh -File .\scripts\smoke\navigation-a11y-smoke.ps1 -RunBuild -RunTests
pwsh -File .\scripts\smoke\move-to-group-smoke.ps1 -RunBuild -RunTests
pwsh -File .\scripts\smoke\sidebar-favorites-smoke.ps1 -RunBuild -RunTests
```

Chaque script affiche un rapport JSON et retourne un code de sortie non nul en cas d'échec.

Les exécuter en séquence. Ils lancent et arrêtent la même application bureau et modifient temporairement le même `settings.json` ; les scripts arbre/favoris sauvegardent et restaurent également `servers.json` lorsqu'ils doivent alimenter ou inspecter un état persisté.

Si vous incluez les passes de build et de tests, gardez-les elles aussi strictement séquentielles : `kill -> build -> test`. Lancer build et test en parallèle peut laisser `testhost.exe` verrouiller les sorties et déclencher `MSB3026`.

## Couverture

`settings-smoke.ps1` couvre :

- l'ouverture de la page Paramètres
- le remplissage des préréglages de fournisseur d'identifiants
- la mise à jour du champ commande lors de la sélection d'un préréglage
- la persistance après enregistrement puis redémarrage de la commande du fournisseur d'identifiants
- le rendu de l'état vide du statut de jeton de la bibliothèque de commandes
- le rendu du statut des fournisseurs d'outils externes
- le remplissage de la légende des espaces réservés d'outils externes
- le rafraîchissement de l'aperçu d'outil externe lors d'un changement de sélection
- les vérifications de rendu localisé EN/FR après redémarrage

Remarques :

- Le changement de langue est conditionné à un enregistrement par le design actuel du produit, mais le test de fumée scripté passe par `settings.json` + redémarrage, plus stable.
- Les libellés des préréglages de fournisseur d'identifiants sont volontairement traités comme un catalogue statique. Le script les enregistre en EN et en FR mais n'échoue pas s'ils restent identiques.
- L'invocation à l'exécution d'outils externes depuis une session vivante ou un menu contextuel reste un test de fumée manuel.

`navigation-a11y-smoke.ps1` couvre :

- les valeurs `AutomationProperties.Name` des onglets de navigation supérieurs en EN
- les valeurs `AutomationProperties.Name` des onglets de navigation supérieurs en FR
- les valeurs `AutomationProperties.Name` des boutons d'action de passerelle en EN
- les valeurs `AutomationProperties.Name` des boutons d'action de passerelle en FR

`move-to-group-smoke.ps1` couvre :

- le déplacement vers un autre groupe via le menu contextuel de l'arbre des sessions
- la conservation en mémoire de l'expansion à travers le chemin de déplacement unifié
- la parité de l'ensemble des destinations face aux groupes cibles du périmètre projet
- la présence de l'entrée sans groupe dans le sous-menu de déplacement
- la vérification, appuyée sur la persistance, que le déplacement a bien atteint le groupe cible attendu

Remarques :

- Le script utilise les fonctions partagées de `uia-common.ps1` plus des fonctions locales d'arbre et de menu.
- Certaines vérifications proches du glisser-déposer restent manuelles par choix : retour visuel du curseur de glissement, annulation par Échap, perception du défilement, et affordance de la zone de dépôt sans groupe.
- Le rapport utilise des statuts par scénario (`Green`, `Red`, `Skipped`) car certaines interactions avec les popups WPF sont volontairement déléguées au test de fumée humain.

`sidebar-favorites-smoke.ps1` couvre :

- la présence de la section Favoris dans l'arbre Outils de la barre latérale
- le tri alphabétique des outils favoris par nom d'affichage localisé
- l'interaction du filtre de la barre latérale avec la section Favoris
- l'aller-retour de persistance de `FavoriteToolIds` à travers un redémarrage

Remarques :

- Le script pré-alimente `FavoriteToolIds` dans `settings.json` pour rendre déterministes les vérifications de tri et de filtre.
- Les ContextMenus WPF créés par programmation ne sont pas exposés de façon fiable dans l'arbre UIA : l'épinglage/désépinglage et la vérification du clic droit sans lancement restent des scénarios de fumée humains.
- Le rapport suit le même modèle de scénarios `Green` / `Red` / `Skipped` que `move-to-group-smoke.ps1`.

## Vérifications manuelles qu'il reste utile de conserver

Celles-ci restent préférables en test de fumée manuel :

- réorganisation, fusion et détachement des onglets de session par glisser-déposer
- annulation du glissement par `Escape`
- confirmation visuelle de la mise en évidence de la cible de dépôt
- divulgation des échecs SSH (`Stage` / `Code` / `Detail`) sur les erreurs d'authentification, de passerelle et de réseau
- divulgation des échecs RDP avant ouverture d'onglet (tunnel / identifiants / écriture du `.rdp` / lancement) lorsqu'une reproduction facile est disponible
- profil RDP RD Gateway : renseigner `RdpGateway` force le mode Externe, les profils de passerelle `.rdp` importés se lancent en externe, et l'onglet mstsc allégé affiche le statut lancement/remplissage automatique/fermeture sans exposer le mot de passe
- préréglages des Paramètres RDP : ajout/suppression/réinitialisation des lignes largeur-hauteur, les lignes invalides restent visibles, l'enregistrement est bloqué tant qu'elles ne sont pas corrigées
- divulgation RDP en cours de session au périmètre du volet et effacement lors de la reconnexion, lorsqu'un serveur de test est disponible
- mode RDP Ajuster à la fenêtre (par défaut) : se connecter à une cible RDP quelconque et vérifier qu'aucune barre de défilement horizontale ou verticale n'apparaît sur la session embarquée, quelle que soit la taille de fenêtre ou après un changement de résolution
- sortie du plein écran RDP via la pastille, F11, Échap et Ctrl+Shift+F11, avec le focus tantôt sur le chrome WPF, tantôt dans la session RDP
- filtre de premier plan du plein écran RDP : lorsque Heimdall est en plein écran mais qu'une autre application est au premier plan, Échap / F11 doivent atteindre cette application et ne pas être absorbés par Heimdall
- les bandes de letterbox RDP en mode résolution fixe s'affichent avec la couleur de fond du thème, et non avec la surface hôte gris clair par défaut
- section Résolution du profil dans ServerDialog : visibilité conditionnelle des quatre modes, localisation EN/FR, badges de validation, alignement sur un multiple de 4, et aller-retour d'enregistrement comme valeur par défaut via le menu contextuel d'onglet
- exactitude du diagnostic par volet dans une vue scindée (déconnecter un seul volet, vérifier que le volet frère reste propre)
- curseur de glissement de l'arbre des sessions et affordance de la zone de dépôt sans groupe
- flux d'épinglage/désépinglage via le ContextMenu des outils de la barre latérale et comportement du clic droit sans lancement
- fermeture de la palette de commandes lors d'un changement de premier plan inter-processus
- passe au Narrateur ou à NVDA sur les contrôles nouvellement ajoutés

## Étendre le harnais

- Sourcer `scripts/smoke/uia-common.ps1` avec l'opérateur point plutôt que réimplémenter la logique de lancement, d'attente et de clic.
- Privilégier un `x:Name` stable ou `AutomationProperties.AutomationId` sur les contrôles qui comptent pour la couverture de fumée.
- Sauvegarder et restaurer `config/settings.json`, ainsi que `config/servers.json` lorsqu'un test de fumée alimente ou modifie l'arbre des sessions.
- Garder des rapports structurés : un `Result` de premier niveau, puis des statuts par scénario `Green` / `Red` / `Skipped` accompagnés de chaînes échantillonnées ou de raisons expliquant ce qui a été observé.
- Garder les tests de fumée durables et versionnés dans le dépôt séparés des sondes de diagnostic ponctuelles.
