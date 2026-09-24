# TPPA and MLAstro integration proposal

**My proposal:** I would like to keep the alignment measurements in TPPA and let your MLAstro plugin control the hardware and correction strategy. I propose connecting the two through NINA's message broker.

This proposal describes the hooks I would add to TPPA. How your plugin implements its correction strategy and hardware control would remain your decision.

## Why I prefer this to a fork

My main concern with a fork is maintaining two versions of the alignment calculations. Fixes and improvements I make in TPPA would not automatically reach users of your plugin.

**The supplied fork already lacks my continuous error estimator.** Its live correction still uses the older image-plane calculation, and it contains neither the new estimator nor its configuration option. Current TPPA also offers an experimental, time-aware estimator that uses each current plate solve to estimate the remaining polar-axis error. Legacy mode remains the default, but users of the supplied fork cannot choose the newer estimator.

I see this as a significant maintenance downside of a fork. Keeping your copy current would require tracking my changes, porting the relevant calculation code and tests, checking interactions with your changes and releasing another version. That work repeats for later fixes and improvements to the error calculations. Until those changes are ported, the two plugins offer different calculation capabilities and can produce different results.

With this integration, installing a compatible TPPA update brings fixes to the selected calculation model directly to your users. You would not need to copy or merge that calculation code. The integration would use the calculation mode configured in TPPA, just like TPPA's own workflow.

You would keep your own UI, hardware behavior and release cycle, so you could fix overshoot, motor control or connection problems independently. I would maintain the TPPA side of the interface and its calculations. The tradeoff is a dependency on TPPA and a stable interface between the plugins. I think that is a more focused maintenance commitment than maintaining another alignment implementation.

## What TPPA would provide

| TPPA responsibility | Hook available to your plugin |
| --- | --- |
| Initial three-point acquisition, including mount RA movement | Start a session and receive progress |
| Camera control, plate solving and error calculations | Request a fresh measurement and receive its result |
| Coordination of capture and adjustment | Obtain an adjustment window and confirm readiness for capture |
| Session lifecycle | Pause, resume, stop and query session state |
| Final alignment verification | Request completion and receive the verified outcome |

```mermaid
flowchart LR
    T[TPPA measurements] <-->|NINA message broker| M[MLAstro correction strategy]
    M <-->|Device commands and status| H[MLAstro hardware]
```

TPPA would retain its error display and overlays. Your plugin would own the correction strategy and device connection. At this boundary, TPPA needs confirmation that adjustment and settling are complete; it does not need to know how you establish that internally.

## Alignment workflow

```mermaid
flowchart LR
    A[Connect and prepare] --> B[Acquire three reference points]
    B --> C[Measure and adjust]
    C --> D[Verify final alignment]
    D --> E[Finish]
```

**Prepare:** TPPA opens a session and waits for your readiness confirmation before starting its existing three-point acquisition. The polar-adjustment axes must remain stationary during acquisition.

**Measure and adjust:** Repeat the following cycle as needed:

1. TPPA captures, solves and publishes a measurement, then waits.
2. On your adjustment request, TPPA grants a window in which it will not capture.
3. TPPA waits until your plugin confirms that adjustment and settling are complete and requests another measurement.

These hooks allow additional measurements between corrections, including during an overshoot-and-return sequence. TPPA would not prescribe the number or direction of those corrections.

**Verify:** TPPA would only begin final verification after your completion request confirms readiness for capture. It would require two consecutive valid, stationary measurements within the selected tolerance, preserving its current confirmation policy. Otherwise it would return the result and allow further correction.

I would want the same coordination to support TPPA's imaging tool and advanced-sequence instruction.

## Message-broker proposals

### Preserve the existing messages

TPPA already accepts `PolarAlignmentPlugin_DockablePolarAlignmentVM_StartAlignment` with exposure and alignment parameters, plus the corresponding `StopAlignment` topic. It also accepts `PolarAlignmentPlugin_PolarAlignment_PauseAlignment` and `ResumeAlignment`, and publishes `Progress` and `AlignmentError` under that same prefix.

I would keep these contracts intact. Today, start/stop target the dockable, pause has no hardware-stop acknowledgement and error publication does not reserve an adjustment window. I would add session coordination in the shared alignment workflow so it works in both the imaging tool and sequence instruction.

### Add two topics for external correction

I suggest these names:

- `PolarAlignmentPlugin_PolarAlignment_ExternalCommand`: MLAstro sends a request to TPPA.
- `PolarAlignmentPlugin_PolarAlignment_ExternalEvent`: TPPA reports a result or requests action from MLAstro.

I would use `IMessage.Version = 1` and a `Kind` field to distinguish the operations below. A capability exchange would establish support for this interface before starting a session.

| Command to TPPA | TPPA response | What the hook provides |
| --- | --- | --- |
| `GetCapabilities` | `Capabilities` | Advertise the interface version and available operations. |
| `StartSession` | `SessionPreparing` | Start the imaging workflow with the requested alignment parameters. Sequence execution emits the same preparation event when its instruction runs. |
| `ControllerReady` | `SessionState` | Accept your confirmation that the mechanism is ready and stationary, then begin reference acquisition. |
| `BeginAdjustment` | `AdjustmentGranted` | Validate the referenced measurement, suspend capture and return an adjustment ID. |
| `RequestMeasurement` | `MeasurementResult` | Accept your stationary-and-settled confirmation, close any active adjustment window and take a fresh sample. Also permit remeasurement without movement. |
| `RequestCompletion` | `SessionState`, then result | Verify tolerance after your readiness confirmation; emit `SessionEnded` on success or return a measurement result for further correction. |
| `Pause`, `Resume` or `Stop` | `SessionState` / `StopRequested` | Coordinate interruption and resumption, including requests from TPPA's UI or the sequencer. |
| `ControllerStopped` | `SessionState` or `SessionEnded` | Acknowledge your stop confirmation and finish the pending pause or cancellation. |
| `ControllerFault` | `SessionEnded` | End the session with a failure reason and the reported or unknown hardware-stop status. |
| `GetSessionState` | `SessionState` | Return the authoritative state after an uncertain response. |

TPPA would reply to every command with acceptance or rejection. Acceptance would be separate from operation completion. It would publish the first correction measurement automatically after reference acquisition and wait for subsequent requests.

### Keep the payload small

I would identify the session through NINA's `CorrelationId`, with an intended recipient, stable command ID and reply-to ID. TPPA would reject stale or out-of-order requests and handle duplicate commands without repeating an operation. The payload would be plain documented data, independent of either plugin's view-model types.

I would include the following in `MeasurementResult`:

- A measurement ID, observation time and result status: valid, solve failed or unstable.
- `AltitudeError`, `AzimuthError` and `TotalError`, keeping the existing degree units and sign conventions.
- Explicit correction directions, so your plugin can interpret the result without parsing UI text.
- The calculation mode configured in TPPA, as informational metadata.

Failed results would contain no actionable error values. TPPA would validate the measurement ID against its latest usable sample before granting adjustment. Selecting or implementing a calculation model would remain inside TPPA.

## TPPA behavior at the boundary

- **Separate capture from adjustment.** TPPA would not expose during a granted adjustment window or grant adjustment during capture.
- **Disable competing automation.** TPPA's built-in hardware adjustment would remain inactive during an external-correction session.
- **Confirm interruptions.** TPPA would request a stop and wait for acknowledgement before reporting a confirmed pause or stop. A timeout or disconnection would be reported with hardware status unknown.
- **Refresh on resume.** TPPA would require readiness confirmation and a fresh measurement before granting another adjustment.
- **Wait for completion to be requested.** Reaching tolerance alone would not end the session while your correction sequence might still be running.

The readiness and stop acknowledgements are interface requirements. How your plugin determines those conditions would remain under your control.

## Implementation scope

I would add an external-correction mode in TPPA's shared alignment workflow, reusing acquisition, calculations and overlays. Existing manual, Avalon and OAPA behavior would remain available outside that mode.

TPPA's broker handlers would queue requests and return promptly because NINA's broker awaits subscriber callbacks. The workflow would process those requests and publish results asynchronously.

I would test acquisition, repeated measurements, adjustment windows, completion and interruption using a simulated external controller in both TPPA entry points. Compatibility checks would also cover stale requests, duplicate commands, failed solves and missing acknowledgements.

## Remaining question for you

Do these hooks cover your requirements, or do you need additional measurements, workflow controls or events from TPPA?

Based on the supplied code, I think this separation is feasible. I would like to understand any additional requirements before settling the interface with your developer.