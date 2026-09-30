# MLAstroRPA

A **NINA** plugin for the **MLAstro Robotic Polar Alignment** hardware — full control of the
MLAstro RPA motor controller (ESP32) over **USB serial or Wi-Fi (WebSocket)**:

- **Full hardware control** — soft limits, TMC2209 motor drivers (AZ/ALT), backlash & P.A overshoot,
  WiFi (AP + Station), set home / return home, live serial terminal, save-all & reboot.
- **Two transports** — **serial** (COM port, with "Auto scan COM port", ESP32 reset and
  auto-reconnect) or **wireless** (WebSocket over the network: hostname `MLAstroRPA.local` or the
  device IP) — only one at a time, picked on the CONNECTION tab.
- **External correction over the NINA message broker** — with the *Assign* switch on the SOFTWARE
  SETTING tab, another plugin hands the polar-alignment correction over to this one: it receives the
  measured error, drives AZ/ALT, holds the capture windows, reports hardware readiness and cancels the
  session when the firmware reports an error.
- **Dock panel** `MLAstro RPA Control (MLAstroRPA)` in the Imaging tab — jog / relative moves,
  align Az / align Alt / ALIGN ALL, speed level 1-5, positions & polar-alignment error readout,
  alarm history, STOP. The dock header also carries FORCE STOP and RESET ERROR.

The plugin options page is a single page with top-level tabs:
`CONTROL` | `HARDWARE SETTING` | `SOFTWARE SETTING` | `CONNECTION`.

| Tab | Content |
| --- | --- |
| **CONTROL** | the same view as the dock panel: jog/relative, align, positions, error readout, alarm history |
| **HARDWARE SETTING** | soft limits, motor driver (TMC2209) AZ/ALT, backlash & P.A overshoot, WiFi configuration — needs a live link (Serial or Wireless) |
| **SOFTWARE SETTING** | external correction over the message broker (assign switch, correction axis mode, correction factor, maximum step per correction, automated adjustment settle time, software overshoot, software reverse directions) plus the bridge status and broker log |
| **CONNECTION** | connection type (Serial / Wireless), auto scan COM port, COM port and auto-reconnect, handshake status, pause polling, handshake timeout & polling period, wireless address, Reset ESP32, system log and the serial terminal (Hex + Send) |

## Architecture

One assembly, `NINA.Plugins.MLAstroRPA`, with a single `IPluginManifest` (`MLAstroPlugin.cs` at the
repository root). The manifest only wires things together: it builds the `MLAstroController`
(`MLAstroPlugin.MLAstro`) from the settings, the connection service, the dock view model and the message
broker — that controller is the state/command layer behind the options page.

The code is split by responsibility:

| Folder | Role | Content |
| --- | --- | --- |
| `MLAstroRPA-implement/Services/` | device + transport | `SerialConnectionService` owns the hardware link: open/close the COM port, handshake (`[MLAstroRPA-TC]` → `ok,...`), the `?` telemetry poll, parsing telemetry into `TelemetryData` and the firmware error line into `DriverErrorState`, and scanning COM ports for the controller. `MlastroWebSocketService` offers the same surface for the wireless transport, and the serial service forwards to it while wireless is in use — the UI never talks to two transports at once. |
| `MLAstroRPA-broker/Broker/` | external correction | `BridgeClient` (message-broker in/out), `BridgeContract` + `BridgePayloads` (the wire contract), `BridgeEngine` (how much to move for a given measurement), `HardwareAligner` (the firmware commands actually sent to the axes) and `BridgeRunner` (session state machine: measurement → capture window → move → settle → next measurement, plus pause, stop and cancel). |
| `MLAstroRPA-navigation/Plugin/` | options page | `MLAstroController` holds every bindable value and command of the options page; `MLAstroOptions.xaml` contains the four tab bodies (CONTROL, HARDWARE SETTING, SOFTWARE SETTING, CONNECTION). |
| `MLAstroRPA-navigation/Dockables/` | dock + header | `PolarAlignmentDockVM` (dock panel logic), `PolarAlignmentDockable.xaml`, the header bar, the alarm entries and the system-log entries. |
| `MLAstroRPA-navigation/Settings/` | persisted state | `PluginSettings` — everything the plugin remembers between NINA runs. |
| `Installer/MSI/` | packaging | WiX v6 MSI project plus `Release-MSI.ps1`, which builds the installer and creates the GitHub release. |

Key invariants:

- **One owner per device.** Only `SerialConnectionService` opens the COM port or the WebSocket. The
  dock, the options page and the correction loop all go through it; external code uses the
  "external control" API (`BeginExternalControlAsync` / `EndExternalControl` / `NotifyExternalStop`
  plus the matching listeners) — direct calls, no reflection, with a plain COM scan as fallback.
- **Views are found by resource key, not by type.** The options page is the `DataTemplate` keyed
  `MLAstroRPA_Options`, the dock view is resolved by the content id `MLAstroRPA`, and every app-level
  resource key of this plugin carries the `MlaRpa` prefix — so a side-by-side install can never have
  another plugin override these templates.
- **The UI binds to view models only.** The dock and the options page read/write
  `MLAstroController` / `PolarAlignmentDockVM`; commands travel from there into the service layer.
- **The correction loop is opt-in.** Without an assigned session `BridgeRunner` never runs, so the manual
  controls stay in charge of the axes.
- **State flows one way.** Device → transport service → controller/view model → UI; commands go back the
  same path, and the broker layer is the only part that also listens to another plugin.

## Requirements

- NINA 3.1.2.9001 or later (.NET 8 / Windows)
- MLAstro RPA controller, reachable over USB serial or Wi-Fi (for CONTROL / CONNECTION / HARDWARE SETTING)

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
4. The plugin options page (tabs `CONTROL` | `HARDWARE SETTING` | `SOFTWARE SETTING` | `CONNECTION`) is
   now available in the plugin settings, and the `MLAstro RPA Control` tool is in the Imaging tab.

## Documentation

- `MLAstroRPA-navigation/Documentation/Serial-protocol.md` — serial commands and telemetry
- `MLAstroRPA-navigation/Documentation/Websocket-protocol.md` — wireless (WebSocket) protocol
- `MLAstroRPA-navigation/Documentation/UI-TEXT-REFERENCE.md` — UI texts and where they are defined
- `Changelog.md` — version history · `FAQ.md` — frequently asked questions

## License

MPL-2.0 — see `LICENSE`. Part of the code history comes from the original open-source **Three Point
Polar Alignment** plugin for NINA by
[Isbeorn](https://github.com/isbeorn/nina.plugin.polaralignment) (MPL-2.0).
