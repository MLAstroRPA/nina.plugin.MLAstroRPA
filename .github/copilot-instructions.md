# Copilot Instructions

## Project Guidelines
- This is the **MLAstroRPA** NINA plugin (assembly `NINA.Plugins.MLAstroRPA`, display name `MLAstroRPA`,
  PluginId GUID `af3ab7b3-f11a-4671-a87f-e7c3985fb509`). It covers the MLAstro Robotic Polar
  Alignment hardware control only: CONTROL / CONNECTION / CONFIGURATION / SOFTWARE SETTING options
  tabs, header bar and the MLAstro docking panel.
- Three Point Polar Alignment (TPPA) is **NOT part of this assembly**. TPPA is a separate NINA
  plugin (separate repository) that talks to this one through the NINA message broker. The
  integration lives in `MLAstroRPA-broker\Broker\` (`TppaBrokerClient`, `TppaBrokerContract`,
  `TppaBrokerPayloads`, `ExternalCorrectionRunner`, `ExternalCorrectionEngine`, `HardwareAligner`)
  and `MLAstroRPA-implement\`.
- Code namespaces are `MLAstroRPA.*` (folder `MLAstroRPA-navigation\`) and `NINA.Plugins.MLAstroRPA.*`
  (manifest/options). Keep them unique from the merged `MLAstroRPA+TPPA` plugin: NINA merges every plugin
  `ResourceDictionary` into `Application.Current.Resources` and resolves views by key
  (`<plugin name>_Options`, `<type full name>_Dockable`), so a duplicate key or type name silently lets the
  other plugin override this one's templates (fixed in 2.2.2.0 by renaming namespaces + keys).
- There is exactly ONE `IPluginManifest`: `MLAstroPlugin` (root `MLAstroPlugin.cs`). The MLAstro
  controller (`MLAstroRPA-navigation\Plugin\MLAstroController.cs`) is NOT a manifest - it is owned by
  `MLAstroPlugin.MLAstro`.
- The plugin Options page is the root `Options.xaml` (`DataTemplate x:Key="MLAstroRPA_Options"`)
  - a TabControl with tabs: `CONTROL`, `CONNECTION`, `CONFIGURATION`, `SOFTWARE SETTING`. The MLAstro
  tab bodies live in `MLAstroRPA-navigation\Plugin\MLAstroOptions.xaml` (merged via `MergedDictionaries`).
- In the plugin options UI, only top-level sections (tabs / top-level Expanders) should be
  expandable/collapsible and they should default to expanded; nested subsections must not be collapsible.
- The COM port and the wireless (WebSocket) link are owned by `SerialConnectionService` (MLAstro).
  External consumers borrow it through the external-control API (`BeginExternalControlAsync` /
  `EndExternalControl` / `NotifyExternalStop` + `AddExternalControlListener`, mirrored on the wireless
  proxy in `MlastroWebSocketService`) - direct calls, NO reflection. Keep that architecture: one
  owner, "external control" borrow + pause-query, plus direct COM-scan fallback.

## Build
- `dotnet build MLAstroRPA.csproj -c Release -tl:off` (or the solution `MLAstroRPA.slnx`).
- Post-build runs for Debug AND Release: it stops NINA, copies the DLL to
  `%LOCALAPPDATA%\NINA\Plugins\3.0.0\MLAstroRPA\` and refreshes the MSI staging copy
  `Installer\MSI\Plugin\MLAstroRPA\`. After a Debug build, build Release again before packaging the MSI.
- Exactly ONE plugin project (`MLAstroRPA.csproj`) may exist in this repository - never add a second
  csproj for the same sources.
- Version source of truth: `MLAstroRPA.csproj` `<Version>` / `<AssemblyVersion>` / `<FileVersion>` /
  `<InformationalVersion>` + the top `Changelog.md` entry.

## Terminal UI Guidelines (CONNECTION tab)
- Do not tint the terminal (RichTextBox) background; keep the context menu background white.
- Place a Hex checkbox BEFORE the Send button; when Hex is checked, the input must accept only up to
  16 hex characters and send them as the corresponding hex bytes.
- HandShake over Serial: the handshake sequence is "[MLAstroRPA-TC]" sent to the connected serial
  device; expect "OK!" ("ok,...") as the response. Show "Handshake: OK!" when the response matches,
  otherwise "Handshake: NO ANSWER".
