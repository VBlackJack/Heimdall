# UX And Accessibility Checklist

*Also available in French: [fr/UX_ACCESSIBILITY_CHECKLIST.md](fr/UX_ACCESSIBILITY_CHECKLIST.md).*

Use this checklist for every new tool and during significant UX refactors.

## Keyboard

- The primary input receives focus on load.
- The primary action is reachable without a mouse.
- `Enter` or `Ctrl+Enter` is wired when the tool has a clear main action.
- The tab order is explicit when the layout is dense or non-linear.
- Icon-only buttons have a tooltip and an automation name.

## Async Behavior

- Long-running operations show visible progress.
- Inputs that should not change during execution are disabled or read-only.
- A visible `Stop` or `Cancel` action exists when cancellation is supported.
- Repeated clicks cannot start duplicate concurrent runs.

## Feedback States

- Validation errors are shown inline.
- Empty states are explicit and localized.
- Results surfaces stay hidden until there is meaningful content.
- Filtered-no-result states are different from first-use empty states when relevant.

## Layout

- The tool remains usable in split view or narrow panes.
- Dense action bars wrap instead of crushing the main input.
- Large result grids hide secondary columns before becoming unreadable.
- Footers and status areas stay readable on narrow widths.

## Sidebar And Search

- Session sidebar search and inline actions stay on a single row: search takes the remaining width (`MinWidth=120`), the primary action (Add) stays inline as 1-click, and secondary actions (Import submenu, Expand all, Collapse all) collapse into the kebab `⋮` overflow menu.
- The filter result count is a hint `TextBlock` that collapses to 0 height when no filter is active; the toolbar row only grows when there is something to show.
- Icon-only sidebar actions keep localized tooltips and `AutomationProperties.Name`.
- Long session names preserve the leading identifier; tooltips expose the full `DisplayName`.
- The status dot on each row reflects either the active session state or, when disconnected, the reachability verdict from `SessionHealthMonitor` (green=Up, red=Down, orange=Probing, gray=Unknown).

## Contrast And Color

- Buttons, glyphs, and indicators painted on `AccentBrush` use `TextOnAccentBrush` (per-theme dark on light-accent variants, white on dark-accent variants). Direct `#FFFFFF` `Foreground` over an accent fill is a regression - the contrast collapses to ~2:1 on the 7 light-pastel-accent Dracula variants (DraculaPro, Drakula, Blade, Buffy, Bathory, Lincoln, VanHelsing, Morbius).
- Semantic status text (Success / Warning / Error / Info) uses `SuccessTextBrush` / `WarningTextBrush` / `ErrorTextBrush` for `Foreground`. The plain `*Brush` keys remain reserved for borders, badge fills, and icon backgrounds - they are too saturated for text on the 5 light themes (Alucard, Carmilla, Helsing, Nosferatu, Renfield).
- Theme-aware brush converters follow the dual `IValueConverter` + `IMultiValueConverter` pattern with a `ThemeRevision` trigger so a runtime theme swap re-evaluates colors without a rebind.

## Localization

- No user-visible placeholder or default text is hardcoded in XAML.
- Visible demo values are avoided unless they are intentional product presets.
- Watermarks, labels, tooltips, and status messages come from i18n keys.

## Validation Pass

- Run `dotnet build Heimdall.slnx -c Debug`.
- Open the tool in a narrow pane and a normal pane.
- Test first-use empty state, error state, loading state, and success state.
- Test the full flow without using the mouse.
