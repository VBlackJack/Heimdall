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
- Le texte de statut sémantique (Success / Warning / Error / Info) utilise `SuccessTextBrush` / `WarningTextBrush` / `ErrorTextBrush` / `InfoTextBrush` pour son `Foreground`, et le texte couleur d'accentuation utilise `AccentTextBrush`. Ces clés `*TextBrush` sont dérivées pour chaque thème par `HeimdallThemeService` à chaque changement de thème ou d'accentuation : la couleur du thème est gardée telle quelle quand elle atteint déjà 4,5:1 sur `BackgroundColor`, sur `SurfaceAltColor` (la carte), sur une ligne survolée (`SelectionColor`) et sur une ligne sélectionnée (l'accentuation à 18 % sur la fenêtre), sinon sa luminosité se déplace, teinte conservée, jusqu'à y parvenir. `ThemeSurfaceTokenContrastTests` tient ce rapport sur chaque thème livré. Les clés `*Brush` simples et `AccentBrush` restent réservées aux bordures, aux remplissages de badges, aux soulignements et aux fonds d'icônes - en texte, elles passent sous 4,5:1 sur les cartes (le rouge d'erreur de Drakul est à 2,91:1 sur la carte).
- Le fond de survol d'un bouton principal est `AccentHoverFillBrush`, pas `AccentHoverBrush` : `HeimdallThemeService` choisit la teinte de survol quand `TextOnAccentBrush` s'y lit à 4,5:1, et la teinte pressée sinon (Parchment et Folio, dont la teinte de survol éclaircie ne laisse aucune couleur de libellé à 4,5:1). Un bouton principal pressé garde le fond d'accentuation et le cerne de `AccentPressedBrush`, pour la même raison sur les thèmes sombres.
- Un trait qui marque une région ou la limite d'un composant (le séparateur de la barre latérale, les filets de la barre d'outils, de la barre d'état et des en-têtes et pieds d'outils, les séparateurs d'en-têtes de colonnes des grilles, les cartes de section) utilise `BorderStrongBrush`, dérivée de la même façon depuis `BorderColor` pour atteindre 3:1 sur le fond et sur la carte (WCAG 1.4.11) ; `BorderBrush` reste décorative, un contour discret dont aucune limite ne dépend.
- Les indications, légendes, textes d'invite et filigranes utilisent `TextSecondaryBrush`. `TextDisabledBrush` (texte secondaire à 55 pour cent, 2,40:1 sur les thèmes clairs) est réservé aux états `IsEnabled=False` et aux glyphes d'état atténués à dessein ; `TextDisabledBrushGuardTests` le refuse sur du texte à lire.
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
