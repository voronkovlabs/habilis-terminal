# Stage 5 — Themes and icons: findings

Date: 2026-11-01.

## What was built

- `Themes/ThemeLoader`: VS Code colour themes — JSONC, `include` chains (the including file wins, token rules
  accumulate base-first), `type` from the files, the built-in manifest type (`uiTheme`) or the background luminance.
  Built-in: Dark/Light Modern, Dark+/Light+, Dark/Light (Visual Studio), Dark/Light High Contrast (MIT, VS Code).
- `Themes/ThemeDefaults`: VS Code colour-registry defaults per theme type for every key Habilis Terminal uses,
  including references (`list.focusOutline` → `focusBorder`) and the 16 terminal ANSI colours.
- `Themes/WindowsAppearance`: light/dark (`AppsUseLightTheme`), high contrast, accent colour (DWM `AccentColor`);
  the accent tints focus outlines, progress and the active selection (not in high contrast).
- `Themes/FileIconTheme` + `LanguageTable`: VS Code file icon theme resolution — file name, extensions longest first,
  language id, default; folder names; `light` overrides.
- `tools/IconBuilder`: WOFF→TTF (zlib tables), Seti glyph outlines at the declared 150 % size, Material SVG →
  path data (own SVG path parser for minified arc flags; path/circle/ellipse/rect/polygon, groups, transforms,
  `use`, gradients → first stop colour), language table from 95 VS Code extension manifests. Output: embedded JSON
  (Seti 853 KB, Material 1.47 MB, languages 7 KB). 383/383 Seti and 1256/1256 Material icons converted;
  41 ignored SVG features (strokes, clip paths).
- App: `ThemeService` (Windows-driven theme + accent, reload on `WM_SETTINGCHANGE` / `WM_DWMCOLORIZATIONCOLORCHANGED`
  and when a theme file chosen by path is saved), `PanelPalette` from workbench keys (code + `Habi.*` dynamic
  resources for XAML), terminal palette from `terminal.*`, title bar follows light/dark. `IconProvider` draws theme
  icons (cached per icon and light/dark), a generic folder for Seti, and system icons for `.exe/.ico/.lnk/.url`.
  Codicons in the quick pick. `ThirdPartyNotices.txt` (copied next to `habi.exe`).
- Development switches until settings exist (stage 10): `HABI_THEME` (built-in name or theme file path),
  `HABI_USE_ACCENT=0`, `HABI_ICON_THEME=seti|material`.

## Verified (screenshots)

- Default (Windows dark + red accent), Light Modern, Dark High Contrast: panels, quick pick, terminal palette,
  title bar.
- Seti and Material icons, Material special folders, system `.exe` icon, codicons in the drive menu.
- Live change: editing a watched theme file switched dark → light with a custom selection colour, terminal and title
  bar included, without restart.
- 27 theme/icon tests (colour parsing, include chains, defaults, accent, built-in types, icon resolution, light
  variants, extension candidates); 130 tests in total, 1 skipped (symlink privilege).

## Deviations and notes

- Codicons are rendered from the bundled `codicon.ttf` as glyph geometry at run time (`GlyphTypeface`), not converted
  at build time: WPF did not resolve the font family from the pack URI inside popups, glyph outlines are
  equally vector and crisp.
- Changing the Windows theme itself was not exercised (it would change the user's system settings); the same reload
  path was verified through the theme-file watcher.
- Live switching of the icon theme needs the settings UI (stage 10); `IconProvider.SetTheme` is in place.
- Source assets are kept under `%TEMP%\habi-assets\download` for re-running IconBuilder; they are not in the repository.
