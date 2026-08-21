# Checklist UX et accessibilité

*Ce document est la version française de [../UX_ACCESSIBILITY_CHECKLIST.md](../UX_ACCESSIBILITY_CHECKLIST.md). / This document is the French version.*

Appliquez cette checklist à chaque nouvel outil, ainsi qu'à l'occasion des refontes UX importantes.

## Clavier

- Le champ de saisie principal reçoit le focus au chargement.
- L'action principale est accessible sans souris.
- `Enter` ou `Ctrl+Enter` est câblé dès lors que l'outil possède une action principale évidente.
- L'ordre de tabulation est explicite lorsque la mise en page est dense ou non linéaire.
- Les boutons réduits à une icône disposent d'une infobulle et d'un nom d'automatisation.

## Comportement asynchrone

- Les opérations longues affichent une progression visible.
- Les champs qui ne doivent pas changer pendant l'exécution sont désactivés ou en lecture seule.
- Une action `Stop` ou `Cancel` visible existe lorsque l'annulation est prise en charge.
- Des clics répétés ne peuvent pas déclencher plusieurs exécutions concurrentes.

## États de retour

- Les erreurs de validation sont affichées en ligne.
- Les états vides sont explicites et localisés.
- Les zones de résultats restent masquées tant qu'il n'y a pas de contenu pertinent.
- L'état "filtre sans résultat" se distingue de l'état vide de première utilisation lorsque c'est pertinent.

## Mise en page

- L'outil reste utilisable en vue divisée ou dans un panneau étroit.
- Les barres d'actions denses passent à la ligne plutôt que d'écraser le champ principal.
- Les grandes grilles de résultats masquent les colonnes secondaires avant de devenir illisibles.
- Les pieds de page et les zones de statut restent lisibles sur les faibles largeurs.

## Barre latérale et recherche

- La recherche de la barre latérale des sessions et ses actions en ligne tiennent sur une seule ligne : la recherche occupe la largeur restante (`MinWidth=120`), l'action principale (Ajouter) reste en ligne et accessible en 1 clic, et les actions secondaires (sous-menu Importer, Tout déplier, Tout replier) sont regroupées dans le menu de débordement kebab `...`.
- Le compteur de résultats du filtre est un `TextBlock` d'indication qui se replie à une hauteur nulle lorsqu'aucun filtre n'est actif ; la ligne de barre d'outils ne s'agrandit que lorsqu'il y a quelque chose à afficher.
- Les actions de la barre latérale réduites à une icône conservent des infobulles localisées et un `AutomationProperties.Name`.
- Les noms de session longs conservent l'identifiant de tête ; les infobulles exposent le `DisplayName` complet.
- La pastille de statut de chaque ligne reflète soit l'état de la session active, soit, hors connexion, le verdict d'accessibilité réseau issu de `SessionHealthMonitor` (vert=Up, rouge=Down, orange=Probing, gris=Unknown).

## Contraste et couleur

- Les boutons, glyphes et indicateurs peints sur `AccentBrush` utilisent `TextOnAccentBrush` (par thème : sombre sur les variantes à accent clair, blanc sur les variantes à accent sombre). Un `Foreground` `#FFFFFF` appliqué directement sur un aplat d'accentuation constitue une régression - le contraste s'effondre à environ 2:1 sur les 7 variantes Dracula à accent pastel clair (DraculaPro, Drakula, Blade, Buffy, Bathory, Lincoln, VanHelsing, Morbius).
- Le texte de statut sémantique (Success / Warning / Error / Info) utilise `SuccessTextBrush` / `WarningTextBrush` / `ErrorTextBrush` pour son `Foreground`. Les clés `*Brush` simples restent réservées aux bordures, aux remplissages de badges et aux fonds d'icônes - elles sont trop saturées pour du texte sur les 5 thèmes clairs (Alucard, Carmilla, Helsing, Nosferatu, Renfield).
- Les convertisseurs de brosses sensibles au thème suivent le double patron `IValueConverter` + `IMultiValueConverter` avec un déclencheur `ThemeRevision`, de sorte qu'un changement de thème à l'exécution réévalue les couleurs sans nouvelle liaison.

## Localisation

- Aucun texte d'invite ni texte par défaut visible par l'utilisateur n'est codé en dur dans le XAML.
- Les valeurs de démonstration visibles sont proscrites, sauf s'il s'agit de préréglages produit intentionnels.
- Les filigranes, libellés, infobulles et messages de statut proviennent de clés i18n.

## Passe de validation

- Exécutez `dotnet build Heimdall.slnx -c Debug`.
- Ouvrez l'outil dans un panneau étroit, puis dans un panneau normal.
- Testez l'état vide de première utilisation, l'état d'erreur, l'état de chargement et l'état de succès.
- Testez le parcours complet sans utiliser la souris.
