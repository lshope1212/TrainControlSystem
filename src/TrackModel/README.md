# Track Model

Track Model has its own WPF dashboard and a separate TestUI process. The default is the complete course network: the **Blue** (15 blocks), **Red** (76) and **Green** (150) Lines of the vF5 workbook, loaded together and published to CTC as one layout. The Blue Line is unchanged: fifteen 50 m blocks (IDs `1`–`15`), one branching switch, two stations, two approach signals, a crossing and two transponders. Red and Green block IDs are `RED-n` and `GREEN-n`; choose the line to display in the dashboard's line selector.

- `TrackModel.Core` owns layout, equipment state, occupancy, failures, passenger exchange, demand, temperature/heaters and ticket accounting.
- `TrackModel.Wpf` hosts Core, imports/exports layouts, draws the selectable schematic and exchanges shared messages over named pipes.
- `TrackModel.TestUI.Wpf` replaces Track Controller, Train Model, Train Controller and CTC for standalone testing. It references Contracts/Common only and displays messages actually received from the dashboard. It captures the direct per-train traffic-light messages too.

## Run

Install the .NET 10 SDK and run `Run-TrackModel.cmd` at the repository root, or choose **Track Model standalone** in Visual Studio. Close running Track Model windows before rebuilding. Alternatively, from two terminals at the repository root:

```powershell
dotnet run --project src/TrackModel/TrackModel.Wpf
```

```powershell
dotnet run --project src/TrackModel/TrackModel.TestUI.Wpf
```

Run these two applications alone for standalone testing. TestUI occupies Track Controller, Train Model, Train Controller and CTC receiving endpoints, plus its private setup/feedback endpoint, so other stubs/real modules must not compete for them. The dashboard works with offline receivers and reports each destination's delivery status. After starting a receiver, click **Refresh outputs**.

## Blue Line quick start

1. **Restore course lines** reloads all three lines and gives stopped train 01 on Blue block 1 (Red and Green clear), no failures, zero commands/tickets, 24 waiting per station and time 09:00:00.
2. Command block **5** controls the one switch: Normal → **6**, Reverse → **11**. Signals are on **6** and **11**; the crossing is on **3**.
3. Set **Train present** to **Yes**, select current block **10** (Station B), and keep actual speed **0**. Enter boarding **3** and disembarking **2**, then click **Simulate boarding / disembarking**. One click records one event: waiting passengers decrease from 24 to 21 and station tickets increase to 3; inspect these counts in the dashboard's Station Information table or captured messages. **To CTC** shows 3 tickets/hour. Every further click records another event; ordinary telemetry edits and refresh never repeat it. Select **Train present → No** to remove the train and clear its previous block; select **Yes** to place it on the current block.
4. Beacons are on approach blocks **9** (Station B / target 10) and **14** (Station C / target 15). Station blocks 10 and 15 do not implicitly emit beacons.
5. **Commanded speed (mph)** and authority apply to the TestUI **Command block**. To inspect those values in dashboard **Train Information**, choose the same **Block number** under **Block Information**; the Train Information card displays its block ID. The two windows keep independent selections. Edits send automatically after a short typing pause; Refresh outputs also flushes pending edits. **Track availability: Open** means no maintenance closure, regardless of occupancy; **Closed** prevents new train entry. Controller commands and failures refer to the selected command block. Output block selection is independent of train position. Refresh flushes pending edits before requesting outputs. **Output last updated** shows local computer receipt time for the selected block's state/environment output, separate from simulation time. TestUI has no station-demand setup, temperature, heater or maintenance controls. Station populations initialize from the Track Model layout (24 per station in the bundled Blue Line); runtime counts remain available in the dashboard and raw captured messages.
6. **Layout details** shows captured static properties/equipment/topology in SI units. **Captured messages** shows the latest 200 received envelopes. TestUI uses font sizes of 20 for body text, 18 for buttons and 23 for section headings, with larger rows and tables. Button/failure rows wrap and the TestUI scrolls at smaller window sizes to keep controls reachable.
7. Start/Pause the test clock, edit HH:mm:ss while paused, or step exactly ten simulation seconds. Running speed uses elapsed wall time and a positive multiplier. Midnight preserves elapsed days and preceding-hour tickets.

For expected inputs/outputs and a full walkthrough, see [Iteration 2 software readiness](ITERATION_2_READINESS.md). The [Project Information audit](PROJECT_INFORMATION_AUDIT.md) lists every supplied reference file and its relevance. [Earlier review results](REVIEW_RESULTS.md) retain the tests performed against the previous demonstration layout.

The dashboard groups block properties, equipment and environment under **Block Information**, and train ID, actual/commanded speed and authority under **Train Information**. **Station Information** lists Boarding, Disembarking, Waiting and Tickets. The old Line Status heading and Next block field are removed. Environment temperature comes from simulated state (initially 68 F in the bundled profile), not a physical sensor connection.

The dashboard schematic uses the same section paths, block placements, station positions and visual yard markers as CTC for Blue, Red and Green. Fit, zoom buttons and Ctrl+wheel support inspecting dense lines; clicking or using the arrow keys selects a block. Unknown imported lines/sections use a generic fallback, with connections taken from the imported data.

## Import/export and units

**Import layout** accepts JSON, CSV, or the supplied **Track Layout & Vehicle Data vF5.xlsx**. XLSX import reads the workbook's **Blue Line**, **Red Line** and **Green Line** worksheets (whichever are present); it validates columns, blocks and infrastructure against the vF5 layout and reconstructs the branch topology. It does not import schedules. No Excel installation is required. The bundled `SampleLayouts/course-lines.json` (the default) is exactly the production import of the supplied workbook, exported as JSON; regenerate it the same way (Import the workbook, then Export). `SampleLayouts/blue-line.json` is the Blue Line alone and is identical to the Blue part.

Red/Green conversion rules, since the worksheets list blocks and equipment but not connections (see `CourseWorkbookReader`): a switch `SWITCH (a-b; c-d)` is placed on the block common to both pairs, Normal to the first pair's other end, Reverse to the second's (the workbook writes some on the neighbouring row: Red 15/16, 32/33, 43/44, Green 12/13, 76/77); consecutive numbers are connected except between two switch-branch ends (Red 66-67 and 71-72, Green 100-101). The result matches the independently generated CTC TestUI fixture block for block. Not in the workbook, so not modelled: the yard (yard switches at Red 9, Green 58 and 62 stay plain blocks), Red/Green signals and transponders, station side, underground sections, and a name for the Green 16 station (shown as "Green 16 (unnamed station)").

**Export layout** writes static JSON, excluding live trains, equipment states, failures and ticket totals. Importing validates before replacing live state and resets runtime state. `small-track.json` / `.csv` are additional input examples; `demo-track.json` and `SampleTrackLayout` preserve the earlier illustrative Blue/Green fixture for regression tests and are not the default course layout.

JSON requires `name` and a nonempty `blocks` list. Each block requires unique `id`, `lineId` and positive `lengthMeters`. Optional fields include `number`, `section`, `elevationMeters`, `gradePercent`, `speedLimitMetersPerSecond`, `temperatureCelsius`, `stationName`, `initialWaitingPassengers`, `hasSwitch`, `hasSignal`, `hasCrossing`, `hasHeater`, `travelDirection`, `beacon`, `beaconTargetBlockId`, `connectedBlockIds`, `normalNextBlockId` and `reverseNextBlockId`. `travelDirection` accepts Forward, Reverse or Bidirectional. A beacon target must identify a station on the same line. A switch needs two distinct connected destinations.

CSV uses these names case-insensitively, with Id, LineId and LengthMeters required, semicolon-separated connected IDs, true/false booleans and quoted fields. Domain state and contracts use SI. Speed/distance controls use mph/feet; the read-only dashboard temperature display uses Fahrenheit. Captured layout details expose SI values. The schematic describes connectivity, not geographic position or scale.

Blue and Red Line direction (bidirectional), installed heaters, 68°F ambient temperature and initial waiting populations are documented simulation configuration because the workbook does not specify these fields. Green Line direction follows the workbook's Track Layout drawing: A–C (1–12) `Reverse` (toward block 1), D–F (13–28) and N (77–85) `Bidirectional`, everything else `Forward` (increasing block numbers). Heaters operate at/below 32°F when powered. The original workbook is not modified.

## Messages

| Receiving named-pipe endpoint | Messages |
| --- | --- |
| `TrainControl.TrackModel` | TrackModelCommandMessage (and the Track Controller's TrackModelCommandBatchMessage, applied command by command), TrackModelTrainUpdateMessage, TrackModelFailureCommandMessage, TrackModelTemperatureCommandMessage, TrackModelPassengerDemandMessage, MaintenanceRequestMessage, SystemTimeMessage, TrackModelSnapshotRequestMessage |
| `TrainControl.TrackController` | `TrackModelBlockStates`: one batch of TrackModelBlockStateMessage per line (the Track Controller's batch input) |
| `TrainControl.TrainModel` | TrackModelTrainEnvironmentMessage |
| `TrainControl.TrainController` | TrackModelSignalMessage: effective light for each occupying canonical train; Unknown clears a removed train or a train on a block without a signal |
| `TrainControl.CTC` | TrackLayoutMessage (on start-up, after a layout change or snapshot request, and retried until CTC has it) and TicketSalesMessage |
| `TrainControl.TrackModel.TestUI` | TrackLayoutMessage and accepted SystemTimeMessage (private setup), TrackModelInputResultMessage (feedback) |

Common transport sends newline-delimited JSON envelopes with camelCase fields and string enums. **Feedback heartbeat:** whenever no snapshot was published for 1 s, the dashboard re-observes every block on its UI thread and sends the block states, with a fresh `ObservedAtUtc`, to the Track Controller, whose 5 s watchdog would otherwise mark a healthy but unchanged track stale. The heartbeat uses wall time (independent of simulation speed or pause), also retries current train environments and direct traffic lights without regenerating layout or ticket-sale snapshots, and stops with the dashboard, so the Track Controller's feedback then goes stale; nothing is ever sent on the model's behalf. The production layout also goes to CTC, so CTC no longer needs its TestUI's sample layout. The TestUI listens on the CTC endpoint too; it logs that layout copy but uses only its private setup copy. The dashboard serializes mutations on its UI thread, captures immutable snapshots, and publishes destinations independently. Layout/state/environment messages share a SnapshotId so TestUI can synchronize inputs after import/refresh without replaying stale state. Layout definitions include all physical values, beacon/equipment metadata and switch destinations. No UI coordinates enter the integration contracts.

Per Derrick's October 8 interface corrections, **To Track Controller** displays only occupancy and the three failure flags. **To CTC** displays only ticket sales. **To Train Model** displays train ID, commanded speed, authority, track signal, beacon and elevation/grade. Actual speed is entered under **From Train Model**. Waiting passengers, next block, passenger totals and cumulative station tickets are omitted from **To Train Model**. **To Train Controller** displays only captured traffic-light messages for the selected block. This direct output follows the module-relationships diagram. Existing shared state/environment fields are retained for contract compatibility, dashboard behavior and raw message diagnostics; hiding fields does not alter those shared schemas.

## Direct traffic lights to Train Controller

Track Model sends the occupying train the **effective signal of its current block**, consistent with the signal in that block's train environment. This does not infer a signal farther ahead or choose a route. Power failure and blocks without an installed light produce `Unknown`. Removing a train or replacing the layout sends `Unknown` to clear the Train Controller's previous light. Ambiguous aliases for the same train also produce `Unknown`.

The sender uses the existing `TrackModelSignalMessage { BlockId, TrainId, Signal }` and the unchanged Train Controller receiver. Train IDs must follow the existing canonical convention (`001`, or a recognized label such as `TRAIN-001` that normalizes to it). The legacy demonstration ID `01` is not guessed to mean `001`.

Light changes and train movements are captured immediately on the model thread. A dedicated bounded sender keeps the newest pending snapshot; an absent Train Controller never delays wayside feedback or Train Model environments. The regular feedback heartbeat retries current lights for late/restarted controllers. A clearing message for a removed train is retained until delivery succeeds. Track Model does not add sequence or expiry fields to the shared contract; receiver-side behavior after the Track Model itself disappears remains the existing Train Controller policy.

For a standalone UI demonstration, run only Track Model and its TestUI. Remove the legacy demo train (`Train present: No`), enter train ID `001`, select current block `6`, and set `Train present: Yes`. Select command block `6` and output block `6`, then change the signal. **To Train Controller** and **Captured messages** show the received aspect. Moving the train to block `7` publishes `Unknown` for that train. When demonstrating the real Train Controller, close Track Model TestUI first so it releases the Train Controller receiving pipe.

## Behavior and scope

- Train Model supplies block-level telemetry and actual speed. Moving/removing a train clears its old block; collision and closed-block entry are rejected atomically. An occupied switch cannot be thrown, and an occupied block cannot close for maintenance.
- Passenger exchange requires a stopped train at a station and cannot exceed waiting demand. Exchange IDs prevent duplicate retries. One ticket per boarding passenger updates cumulative station totals and CTC's preceding-simulation-hour count. Rewinding time clears the hourly ledger, not cumulative station totals. The station-demand operation remains supported in Core/shared contracts for simulation setup but is not emitted by TestUI.
- All three failures are independent. Circuit/power failure reports Unknown occupancy while retaining physical train position; power failure makes signals Unknown and heaters Off. Track Controller owns safe speed/authority decisions.
- Temperature is read-only under **Block Information**, refreshed from the simulated environment state. Manual temperature entry and Apply are removed from the dashboard; Core and the environment message contract retain temperature updates. Maintenance remains a Core operation. Neither is a TestUI input. Unsupported equipment inputs are disabled; beacon text comes from the transponder's explicit metadata, independent of station location.
- Iteration 2 permits neighboring-module stubs. This module does not implement train physics, onboard capacity, automatic train travel, PLC logic, geographic reconstruction, physical relay/PTC protocols or full team integration. Travel direction is metadata; beacon passage timing belongs to later integration.

## Validate

```powershell
dotnet build TrainControlSystem.sln
dotnet test TrainControlSystem.sln --no-build
```

The suite includes platform-neutral Core/import tests and Windows STA workflows against the production TestUI view model with serialized contracts and a model-backed transport, plus rendered layout-tab, Yes/No train-presence and dashboard Train Information and read-only environment checks. The direct-light tests use isolated named pipes and the unchanged Train Controller receiver/display, including all light aspects, movement, power loss, removal, layout replacement, late receivers, reconnects and train-ID isolation. Historical Iteration 2 counts are recorded in the linked review documents. [October 8 interface changes](INTERFACE_CHANGES_OCT_8.md) records earlier checks before station-demand setup, the redundant removal button and extra Train Model output rows were removed; [Blue Line manual results](MANUAL_BLUE_LINE_RESULTS.md) retains the October 7 manual pass. Actual-workbook import verification is recorded separately in the Iteration 2 readiness report.

For a reproducible check against the actual running dashboard, follow [TrackModel.PipeSmoke](../../tests/TrackModel.PipeSmoke/README.md). Its 17 check groups include verifying layout/ticket-only CTC traffic, direct per-train light states, and clearing the previous aspect when a train moves off a signal. Close TestUI before running it and restore the Blue Line afterward.
