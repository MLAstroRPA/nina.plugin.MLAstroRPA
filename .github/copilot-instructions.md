# Copilot Instructions

## Project Guidelines
- This is the merged **MLAstroRPA+TPPA** NINA plugin (single assembly `NINA.Plugins.MLAstroRPA_TPPA`,
  display name `MLAstroRPA+TPPA`). It combines the MLAstro Robotic Polar Alignment hardware control
  (CONTROL / CONNECTION / CONFIGURATION tabs) with the Three Point Polar Alignment (TPPA) wizard.
- MLAstro-origin code keeps the `MLAstro_Robotic_Polar_Alignment.*` namespaces (folder `MLAstroRPA-navigation\`).
  TPPA-origin code lives in `NINA.Plugins.PolarAlignment.*` at the repository root.
- There is exactly ONE `IPluginManifest`: `PolarAlignmentPlugin` (root). The MLAstro controller
  (`MLAstroRPA-navigation\Plugin\MLAstroController.cs`) is NOT a manifest - it is owned by `PolarAlignmentPlugin.MLAstro`.
- The plugin Options page is the root `Options.xaml` (`DataTemplate x:Key="MLAstroRPA+TPPA_Options"`)
  - a TabControl with tabs: `TPPA OPTION`, `CONTROL`, `CONNECTION`, `CONFIGURATION`. The MLAstro tab
  bodies live in `MLAstroRPA-navigation\Plugin\MLAstroOptions.xaml` (merged via `MergedDictionaries`).
- In the plugin options UI, only top-level sections (tabs / top-level Expanders) should be
  expandable/collapsible and they should default to expanded; nested subsections must not be collapsible.
- The serial COM port is owned by `SerialConnectionService` (MLAstro). TPPA's `MLAstroRPA` driver
  borrows it through the external-control API (`MLAstroLink` -> direct calls, NO reflection). Keep
  that architecture: one owner, "external control" borrow + pause-query, plus direct COM-scan fallback.

## Terminal UI Guidelines (CONNECTION tab)
- Do not tint the terminal (RichTextBox) background; keep the context menu background white.
- Place a Hex checkbox BEFORE the Send button; when Hex is checked, the input must accept only up to
  16 hex characters and send them as the corresponding hex bytes.
- HandShake over Serial: the handshake sequence is "[MLAstroRPA-TC]" sent to the connected serial
  device; expect "OK!" ("ok,...") as the response. Show "Handshake: OK!" when the response matches,
  otherwise "Handshake: NO ANSWER".
