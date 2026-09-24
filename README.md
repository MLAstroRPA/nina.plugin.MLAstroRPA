# MLAstroRPA + TPPA

> **Origin & license (TPPA).** The polar-alignment portion of this plugin is a fork of the
> original open-source **Three Point Polar Alignment (TPPA)** plugin for NINA by
> [Isbeorn](https://github.com/isbeorn/nina.plugin.polaralignment), which is licensed under the
> **Mozilla Public License 2.0 (MPL-2.0)**. The TPPA-derived code in this project therefore stays
> under MPL-2.0, keeping the original license/copyright notices. **MLAstroRPA+TPPA is a separate,
> unofficial build — it is NOT the original/official TPPA plugin.** When redistributing, comply
> with MPL-2.0: retain the license and notices, credit the original author, and make the source
> (including your modifications) available.

A single **NINA** plugin that merges:

- **MLAstro Robotic Polar Alignment** — full hardware control for the MLAstro RPA motor
  controller (ESP32) over USB serial:
  - **CONTROL** — manual jog/move, home, position & polar-alignment error readout,
    alarm history, FORCE STOP / RESET ERROR.
  - **CONNECTION** — COM port + baud selection, connect, ESP32 reset, live serial terminal
    (Hex checkbox before Send, handshake `[MLAstroRPA-TC]` → `Handshake: OK!` / `NO ANSWER`).
  - **CONFIGURATION** — soft limits, TMC2209 motor drivers (AZ/ALT), backlash & P.A overshoot,
    WiFi (AP + Station), save-all & reboot.
- **Three Point Polar Alignment (TPPA)** — the polar alignment wizard/assistant available as a
  tool pane inside the imaging tab plus an advanced sequencer instruction, able to drive the
  mount manually or fully automated.

The plugin options page is a single page with top-level tabs:
`TPPA OPTION` | `CONTROL` | `CONNECTION` | `CONFIGURATION`.

## Architecture notes

- Single assembly `NINA.Plugins.MLAstroRPA_TPPA`, plugin display name **MLAstroRPA+TPPA**, with a
  UNIQUE PluginId (GUID) `1352D162-2E66-4F80-A05B-854F021DB913` — distinct from the standalone TPPA
  plugin (`1de8d7d3-f11e-494c-a371-95cb48dffa18`) so NINA treats them as two separate plugins and
  both can be installed side by side.
- MLAstro-origin code keeps the `MLAstro_Robotic_Polar_Alignment.*` namespaces under `MLAstroRPA-navigation\`.
- There is exactly ONE `IPluginManifest` (`PolarAlignmentPlugin`). The MLAstro options/state
  controller (`MLAstroRPA-navigation\Plugin\MLAstroController.cs`) is owned by it (`PolarAlignmentPlugin.MLAstro`).
- The serial COM port is owned by `SerialConnectionService` (MLAstro). The TPPA `MLAstroRPA`
  driver borrows it through the external-control API (`MLAstroRPA\MLAstroLink.cs` → direct calls,
  no reflection) with a direct COM-scan fallback.

## Requirements

- NINA 3.1.2.9001 or later (.NET 8 / Windows)
- Camera + plate solving (for TPPA wizard)
- MLAstro RPA controller over USB serial (for CONTROL / CONNECTION / CONFIGURATION)
- Latitude & longitude configured in NINA options (TPPA)

## BUILD (development)

Build the plugin (Debug/Release) — the post-build step auto close N.I.N.A and copies the DLL to
`%LOCALAPPDATA%\NINA\Plugins\3.0.0\MLAstroRPA-TPPA\`.

```powershell
dotnet build MLAstroRPA_TPPA.csproj -c Release -tl:off
# or via the solution
dotnet build MLAstroRPA_TPPA.slnx -c Release -tl:off
```

> Uninstall any previous separate installations of the old `MLAstro Robotic Polar Alignment`
> and `Three Point Polar Alignment` plugins before using this merged plugin.

## INSTALL (end users)

Not a developer? Install the pre-built plugin from the
[GitHub Releases](https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA_TPPA/releases) page.
Two options are available: the **MSI installer** (recommended) or a **manual DLL copy**.

### Option 1 - MSI installer (recommended)

1. **Close N.I.N.A** completely.
2. Download the latest `MLAstroRPA_TPPA_Plugin_<version>.msi` from the release.
3. Run the `.msi` and follow the setup wizard:
   - The installer checks that N.I.N.A. is installed and prompts you to close it if it is running.
   - It installs the plugin into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\MLAstroRPA-TPPA\`.
4. Restart N.I.N.A.
5. To uninstall, use **Windows Settings → Apps** (or *Programs and Features*) and remove
   **MLAstroRPA+TPPA**.

### Option 2 - Manual DLL copy (advanced)

1. **Close N.I.N.A** completely.
2. Download the latest `NINA.Plugins.MLAstroRPA_TPPA.dll` from the release.
3. Locate your N.I.N.A plugins folder (create it if it does not exist):
   - Default: `%LOCALAPPDATA%\NINA\Plugins\3.0.0\`
   - Or: `C:\Users\<YourUsername>\AppData\Local\NINA\Plugins\3.0.0\`
4. Create a folder named `MLAstroRPA-TPPA` inside it.
5. Copy the downloaded `.dll` into that folder.
6. Restart N.I.N.A.

### Verify installation

1. Open N.I.N.A.
2. Go to **Options → Plugins**.
3. Confirm **MLAstroRPA+TPPA** appears in the list and is enabled.
4. The plugin options page (tabs `TPPA OPTION` | `CONTROL` | `CONNECTION` | `CONFIGURATION`) is
   now available in the plugin settings.

## License

MPL-2.0
