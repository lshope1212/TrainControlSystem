# Track Model

Track Model has its own WPF dashboard and a separate TestUI process. The default is the course **Blue Line**, with fifteen 50 m blocks, one branching switch, two stations, two approach signals, a crossing and two transponders.

- `TrackModel.Core` owns layout, equipment state, occupancy, failures, passenger exchange, demand, temperature/heaters and ticket accounting.
- `TrackModel.Wpf` hosts Core, imports/exports layouts, draws the selectable schematic and exchanges shared messages over named pipes.
- `TrackModel.TestUI.Wpf` replaces Track Controller, Train Model and CTC for standalone testing. It references Contracts/Common only and displays messages actually received from the dashboard. It has no Train Controller receiver.

## Run

Install the .NET 10 SDK and run `Run-TrackModel.cmd` at the repository root, or choose **Track Model standalone** in Visual Studio. Close running Track Model windows before rebuilding. Alternatively, from two terminals at the repository root:

```powershell
dotnet run --project src/TrackModel/TrackModel.Wpf
```

```powershell
dotnet run --project src/TrackModel/TrackModel.TestUI.Wpf
```

Run these two applications alone for standalone testing. TestUI occupies Track Controller, Train Model and CTC receiving endpoints, plus its private setup/feedback endpoint, so other stubs/real modules must not compete for them. The dashboard works with offline receivers and reports each destination's delivery status. After starting a receiver, click **Refresh outputs**.

## Blue Line quick start

1. **Restore Blue Line** gives stopped train 01 on block 1, no failures, zero commands/tickets, 24 waiting per station and time 09:00:00.
2. Command block **5** controls the one switch: Normal → **6**, Reverse → **11**. Signals are on **6** and **11**; the crossing is on **3**.
3. Keep train Occupancy **Occupied**, select current block **10** (Station B), and keep actual speed **0**. Set station demand with **Set waiting**, then use **Apply passenger exchange once** for one boarding/disembarking event. Ordinary telemetry edits never repeat passenger exchange. **Remove train** clears its occupancy; choose Occupied to reinsert it.
4. Beacons are on approach blocks **9** (Station B / target 10) and **14** (Station C / target 15). Station blocks 10 and 15 do not implicitly emit beacons.
5. Controller commands and failures refer to the selected command block. Output block selection is independent of train position. Refresh flushes pending edits before requesting outputs. TestUI has no temperature, heater or maintenance controls/displays.
6. **Layout details** shows captured static properties/equipment/topology in SI units. **Captured messages** shows the latest 200 received envelopes. TestUI uses font sizes of 20 for body text, 18 for buttons and 23 for section headings, with larger rows and tables. Button/failure rows wrap and the TestUI scrolls at smaller window sizes to keep controls reachable.
7. Start/Pause the test clock, edit HH:mm:ss while paused, or step exactly ten simulation seconds. Running speed uses elapsed wall time and a positive multiplier. Midnight preserves elapsed days and preceding-hour tickets.

For expected inputs/outputs and a full walkthrough, see [Iteration 2 software readiness](ITERATION_2_READINESS.md). The [Project Information audit](PROJECT_INFORMATION_AUDIT.md) lists every supplied reference file and its relevance. [Earlier review results](REVIEW_RESULTS.md) retain the tests performed against the previous demonstration layout.

## Import/export and units

**Import layout** accepts JSON, CSV, or the supplied **Track Layout & Vehicle Data vF5.xlsx**. XLSX import supports the course **Blue Line** worksheet/format only; it validates columns, blocks and infrastructure, and reconstructs the supplied branch topology. It does not import Red/Green or schedules. No Excel installation is required. The bundled `SampleLayouts/blue-line.json` exactly matches the production import of the supplied workbook.

**Export layout** writes static JSON, excluding live trains, equipment states, failures and ticket totals. Importing validates before replacing live state and resets runtime state. `small-track.json` / `.csv` are additional input examples; `demo-track.json` and `SampleTrackLayout` preserve the earlier illustrative Blue/Green fixture for regression tests and are not the default course layout.

JSON requires `name` and a nonempty `blocks` list. Each block requires unique `id`, `lineId` and positive `lengthMeters`. Optional fields include `number`, `section`, `elevationMeters`, `gradePercent`, `speedLimitMetersPerSecond`, `temperatureCelsius`, `stationName`, `initialWaitingPassengers`, `hasSwitch`, `hasSignal`, `hasCrossing`, `hasHeater`, `travelDirection`, `beacon`, `beaconTargetBlockId`, `connectedBlockIds`, `normalNextBlockId` and `reverseNextBlockId`. `travelDirection` accepts Forward, Reverse or Bidirectional. A beacon target must identify a station on the same line. A switch needs two distinct connected destinations.

CSV uses these names case-insensitively, with Id, LineId and LengthMeters required, semicolon-separated connected IDs, true/false booleans and quoted fields. Domain state and contracts use SI. Speed/distance controls use mph/feet; the dashboard temperature control uses Fahrenheit. Captured layout details expose SI values. The schematic describes connectivity, not geographic position or scale.

Blue Line direction, installed heaters, 68°F ambient temperature and initial waiting populations are documented simulation configuration because the workbook does not specify these fields. Heaters operate at/below 32°F when powered. The original workbook is not modified.

## Messages

| Receiving named-pipe endpoint | Messages |
| --- | --- |
| `TrainControl.TrackModel` | TrackModelCommandMessage, TrackModelTrainUpdateMessage, TrackModelFailureCommandMessage, TrackModelTemperatureCommandMessage, TrackModelPassengerDemandMessage, MaintenanceRequestMessage, SystemTimeMessage, TrackModelSnapshotRequestMessage |
| `TrainControl.TrackController` | TrackModelBlockStateMessage |
| `TrainControl.TrainModel` | TrackModelTrainEnvironmentMessage |
| `TrainControl.TrainController` | None: Track Model does not send to this endpoint |
| `TrainControl.CTC` | TicketSalesMessage only |
| `TrainControl.TrackModel.TestUI` | TrackLayoutMessage and accepted SystemTimeMessage (private setup), TrackModelInputResultMessage (feedback) |

Common transport sends newline-delimited JSON envelopes with camelCase fields and string enums. The dashboard serializes mutations on its UI thread, captures immutable snapshots, and publishes destinations independently. Layout/state/environment messages share a SnapshotId so TestUI can synchronize inputs after import/refresh without replaying stale state. Layout definitions include all physical values, beacon/equipment metadata and switch destinations. No UI coordinates enter the integration contracts.

Per Derrick's October 8 interface correction, **To Track Controller** displays only occupancy and the three failure flags. **To CTC** displays only ticket sales. Passenger totals are shown under **To Train Model**. Physical traffic-light output and the Train Controller destination are removed. Existing shared state/environment fields are retained for contract compatibility and dashboard behavior; hiding fields does not alter those shared schemas.

## Behavior and scope

- Train Model supplies block-level telemetry and actual speed. Moving/removing a train clears its old block; collision and closed-block entry are rejected atomically. An occupied switch cannot be thrown, and an occupied block cannot close for maintenance.
- Passenger exchange requires a stopped train at a station and cannot exceed waiting demand. Exchange IDs prevent duplicate retries. One ticket per boarding passenger updates cumulative station totals and CTC's preceding-simulation-hour count. Rewinding time clears the hourly ledger, not cumulative station totals. Set waiting changes demand only.
- All three failures are independent. Circuit/power failure reports Unknown occupancy while retaining physical train position; power failure makes signals Unknown and heaters Off. Track Controller owns safe speed/authority decisions.
- Temperature remains adjustable in the main dashboard only. Maintenance remains a Core operation. Neither is a TestUI input. Unsupported equipment inputs are disabled; beacon text comes from the transponder's explicit metadata, independent of station location.
- Iteration 2 permits neighboring-module stubs. This module does not implement train physics, onboard capacity, automatic train travel, PLC logic, geographic reconstruction, physical relay/PTC protocols or full team integration. Travel direction is metadata; beacon passage timing belongs to later integration.

## Validate

```powershell
dotnet build TrainControlSystem.sln
dotnet test TrainControlSystem.sln --no-build
```

The suite includes platform-neutral Core/import tests and Windows STA workflows against the production TestUI view model with serialized contracts and a model-backed transport, plus a rendered layout-tab regression test. The current result is 97 passing tests (74 + 23). [October 8 interface changes](INTERFACE_CHANGES_OCT_8.md) records the latest checks; [Blue Line manual results](MANUAL_BLUE_LINE_RESULTS.md) retains the October 7 manual pass. Actual-workbook import verification is recorded separately in the Iteration 2 readiness report.

For a reproducible check against the actual running dashboard, follow [TrackModel.PipeSmoke](../../tests/TrackModel.PipeSmoke/README.md). Its 16 check groups include verifying ticket-only CTC traffic and zero messages to Train Controller. Close TestUI before running it and restore the Blue Line afterward.
