# MLAstroRPA

> **License & origin.** Distributed under the **Mozilla Public License 2.0 (MPL-2.0)** — see
> `LICENSE`. Part of the code history comes from the original open-source **Three Point Polar
> Alignment (TPPA)** plugin for NINA by
> [Isbeorn](https://github.com/isbeorn/nina.plugin.polaralignment) (MPL-2.0); the TPPA wizard is
> no longer part of this repository — it is a separate plugin (in a separate repository) that talks
> to this one through the NINA message broker.

A **NINA** plugin for the **MLAstro Robotic Polar Alignment** hardware — full control of the
MLAstro RPA motor controller (ESP32) over USB serial:

- **CONTROL** — manual jog/move, home, position & polar-alignment error readout,
  alarm history, FORCE STOP / RESET ERROR.
- **CONNECTION** — COM port + baud selection, connect, ESP32 reset, live serial terminal
  (Hex checkbox before Send, handshake `[MLAstroRPA-TC]` → `Handshake: OK!` / `NO ANSWER`).
- **CONFIGURATION** — soft limits, TMC2209 motor drivers (AZ/ALT), backlash & P.A overshoot,
  WiFi (AP + Station), save-all & reboot.

The plugin options page is a single page with top-level tabs:
`CONTROL` | `CONNECTION` | `CONFIGURATION` | `SOFTWARE SETTING`.

## Architecture notes

- Plugin assembly `NINA.Plugins.MLAstroRPA`, display name **MLAstroRPA**, with a UNIQUE PluginId
  (GUID) `af3ab7b3-f11a-4671-a87f-e7c3985fb509` — distinct from the standalone TPPA plugin
  (`1de8d7d3-f11e-494c-a371-95cb48dffa18`) and from the merged `MLAstroRPA+TPPA` plugin
  (`1352D162-2E66-4F80-A05B-854F021DB913`), so NINA treats them as separate plugins and they can
  all be installed side by side.
- Code namespaces are `MLAstroRPA.*` (hardware control) and `NINA.Plugins.MLAstroRPA.*` (manifest/options),
  and app-level resource keys carry the `MlaRpa` prefix: NINA merges every plugin `ResourceDictionary` into
  `Application.Current.Resources`, so duplicate keys or type names would let the merged `MLAstroRPA+TPPA`
  plugin override this plugin's templates.
- There is exactly ONE `IPluginManifest` (`MLAstroPlugin`, root `MLAstroPlugin.cs`). The MLAstro
  options/state controller (`MLAstroRPA-navigation\Plugin\MLAstroController.cs`) is owned by it
  (`MLAstroPlugin.MLAstro`).
- The serial COM port is owned by `SerialConnectionService` (MLAstro). External consumers (e.g. the
  TPPA plugin) borrow it through an "external control" API — direct calls, no reflection — with a
  direct COM-scan fallback.
- The NINA message-broker integration (external correction session with the TPPA plugin) lives in
  `MLAstroRPA-broker\` + `MLAstroRPA-implement\`.

## Requirements

- NINA 3.1.2.9001 or later (.NET 8 / Windows)
- MLAstro RPA controller over USB serial (for CONTROL / CONNECTION / CONFIGURATION)

## BUILD (development)

Build the plugin (Debug/Release) — the post-build step closes N.I.N.A and copies the DLL to
`%LOCALAPPDATA%\NINA\Plugins\3.0.0\MLAstroRPA\` (Debug included), plus the MSI staging folder
`Installer\MSI\Plugin\MLAstroRPA\`.

```powershell
dotnet build MLAstroRPA.csproj -c Release -tl:off
# or via the solution
dotnet build MLAstroRPA.slnx -c Release -tl:off
```

> Building Debug also overwrites the MSI staging DLL — build Release again before packaging the MSI.

## INSTALL (end users)

Not a developer? Install the pre-built plugin from the
[GitHub Releases](https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA/releases) page.
Two options are available: the **MSI installer** (recommended) or a **manual DLL copy**.

### Option 1 - MSI installer (recommended)

1. **Close N.I.N.A** completely.
2. Download the latest MSI from the release.
3. Run the `.msi` and follow the setup wizard:
   - The installer checks that N.I.N.A. is installed and prompts you to close it if it is running.
   - It installs the plugin into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\MLAstroRPA\`.
4. Restart N.I.N.A.
5. To uninstall, use **Windows Settings → Apps** (or *Programs and Features*).

### Option 2 - Manual DLL copy (advanced)

1. **Close N.I.N.A** completely.
2. Download the latest `NINA.Plugins.MLAstroRPA.dll` from the release.
3. Locate your N.I.N.A plugins folder (create it if it does not exist):
   - Default: `%LOCALAPPDATA%\NINA\Plugins\3.0.0\`
   - Or: `C:\Users\<YourUsername>\AppData\Local\NINA\Plugins\3.0.0\`
4. Create a folder named `MLAstroRPA` inside it.
5. Copy the downloaded `.dll` into that folder.
6. Restart N.I.N.A.

### Verify installation

1. Open N.I.N.A.
2. Go to **Options → Plugins**.
3. Confirm **MLAstroRPA** appears in the list and is enabled.
4. The plugin options page (tabs `CONTROL` | `CONNECTION` | `CONFIGURATION` | `SOFTWARE SETTING`) is
   now available in the plugin settings.

## License

MPL-2.0
