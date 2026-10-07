# Track Model

Track Model runs independently with its own control dashboard and a separate Test
UI. The windows follow the supplied Track Model reference images.

- `TrackModel.Core` owns layout, block/equipment state, occupancy, failures, station
  passenger exchange, and ticket accounting. It has no WPF or pipe dependencies.
- `TrackModel.Wpf` hosts one Core service, draws the track map, imports/exports
  layouts, and receives/sends shared contract messages.
- `TrackModel.TestUI.Wpf` simulates the external modules in a separate process.
  It references Contracts/Common only. Its output panels display messages actually
  received from the dashboard; it never hosts a copy of Track Model.

## Run the standalone system

Build the solution, then start **TrackModel.Wpf** and **TrackModel.TestUI.Wpf**.

In Visual Studio, choose the **Track Model standalone** launch profile. Alternatively,
right-click the solution, choose **Configure Startup Projects**, select multiple
startup projects, and set those two projects to **Start**.

From two PowerShell terminals at the repository root:

```powershell
dotnet run --project src/TrackModel/TrackModel.Wpf
```

```powershell
dotnet run --project src/TrackModel/TrackModel.TestUI.Wpf
```

The Test UI replaces the receiving endpoints of Track Controller, Train Model,
Train Controller, and CTC. For standalone testing, run these two applications
alone; close CTC.TestUI and the real external modules to avoid competing listeners.
Track Model itself can run without any receivers; its status bar reports them
offline and its local controls continue working. After connecting/restarting a
receiver, click **Refresh outputs** to request a fresh layout and snapshot.

## Try it

1. The dashboard starts with a demonstration Blue/Green layout, train 01 on block
   104, train 02 on 116, and a track-circuit failure on 119. Click a block to inspect
   it. Arrow keys also change the diagram selection.
2. In Test UI, set block 104's commanded speed to 30 mph and authority to 1,500 ft.
   Edits send automatically after a 400 ms typing pause. Both the dashboard and
   captured Train Model outputs update, including actual speed for train telemetry.
3. Set train 01's current block to 105. Occupancy moves automatically from 104 to
   105 and the output selector follows the train. Under **From Train Model**, set
   **Occupancy** to **Clear** to remove that train, or **Occupied** to place it on
   the current block. **Remove train** is a shortcut for Clear; choose **Occupied** to place the train again. Speed edits while Clear leave the train off the track.
   Train ID commits on leaving the field; position and actual speed update live.
4. Select block 119 and toggle its failures; each change sends automatically.
   Failures can also be changed directly for the selected block on the dashboard.
5. On station block 104, enter boarding/disembarking counts, then click
   **Apply passenger exchange once** once per exchange.
   The train must be stopped. Boarding reduces waiting passengers and increments station ticket totals. Test UI displays cumulative boarding, disembarking, station tickets, and the rolling ticket rate separately.
   Ordinary speed/position edits send zero passenger counts, so they never replay
   the entered exchange.
6. Edit time directly, or use **Start clock** / **Step 10 sec** to drive simulation time. A step is exactly ten simulation seconds at any multiplier; a running clock uses elapsed wall time and preserves fractional seconds. Displayed time-of-day wraps at midnight; elapsed days remain internal so the rolling ticket ledger continues across midnight and pause/resume.
   Pause the clock before manually changing its time.
7. All inputs and output panels fit on a single page without vertical scrolling
   at the minimum window size of 1180 × 720. **Captured messages** opens a separate
   window with the latest 200 received envelopes. Command edits select their block's
   outputs; train edits select their current block. **Output block** can also be set manually.
8. **Restore demo** resets the demonstration; importing a layout resets train,
   equipment, failure, passenger, and ticket state for that layout.

The Test UI's status bar reports acceptance or rejection from Track Model over an
optional feedback endpoint; captured outputs show the resulting state. Invalid
inputs leave existing state intact. Layout refreshes suppress input sends while
WPF rebuilds dropdown selections.

Layout, block-state, and train-environment messages carry a shared `SnapshotId`.
After import, demo reset, or refresh, the Test UI waits for matching state and
environment messages before reloading controller inputs and the current block's
train occupancy and speed. This keeps stale inputs from being carried into the
newly loaded layout. Reported occupancy can be Unknown during circuit or power
failure; the train input remains Occupied or Clear according to physical occupancy.

## Layout import/export

Use **Import layout** for JSON/CSV and **Export layout** to save the current static
layout as JSON. Small example files are in `SampleLayouts/small-track.json` and
`SampleLayouts/small-track.csv`; they contain a switch, a station, and a crossing.
`SampleLayouts/demo-track.json` is the full 36-block layout exported from the
dashboard. The demonstration is illustrative, not a surveyed class track.

JSON uses a root object with `name` and `blocks`. Each block requires `id`,
`lineId`, and positive `lengthMeters`. Block IDs are globally unique. Optional fields:

`number`, `section`, `elevationMeters`, `gradePercent`,
`speedLimitMetersPerSecond`, `temperatureCelsius`, `stationName`,
`initialWaitingPassengers`, `hasSwitch`, `hasSignal`, `hasCrossing`,
`connectedBlockIds`, `normalNextBlockId`, `reverseNextBlockId`, `hasHeater`, and `travelDirection` (`Forward`, `Reverse`, or `Bidirectional`; default `Bidirectional`).

CSV uses the same property names as headers, case-insensitively; `Id`, `LineId`,
and `LengthMeters` are required. Separate connection IDs with semicolons in one
field. Booleans use `true`/`false`. Quoted fields are supported. A switch needs two
distinct destinations included in its connection list. JSON/CSV physical values
use SI units. Spreadsheet-specific XLSX import is not implemented.

The dashboard uses mph, feet, and Fahrenheit. Conversion occurs in the WPF layer;
all domain state and message quantities use SI. The schematic is a presentation
of block connectivity, rather than a geographic or distance-scaled map.

## Communication

The existing Common transport sends one newline-delimited JSON envelope per
connection, with camelCase fields and enums as strings.

| Receiving endpoint | Owner in standalone mode | Messages |
| --- | --- | --- |
| `TrainControl.TrackModel` | Dashboard | TrackModelCommandMessage, TrackModelTrainUpdateMessage, TrackModelFailureCommandMessage, SystemTimeMessage, MaintenanceRequestMessage, TrackModelSnapshotRequestMessage |
| `TrainControl.TrackController` | Test UI | TrackModelBlockStateMessage |
| `TrainControl.TrainModel` | Test UI | TrackModelTrainEnvironmentMessage |
| `TrainControl.TrainController` | Test UI | TrackModelSignalMessage |
| `TrainControl.CTC` | Test UI | TrackLayoutMessage, TicketSalesMessage |
| `TrainControl.TrackModel.TestUI` | Test UI | TrackModelInputResultMessage (optional input feedback) |

The dashboard marshals mutations onto its UI thread. Its publisher captures
immutable output messages, coalesces pending snapshots, and sends destinations
independently so an offline receiver does not block the other modules. A layout is
sent to CTC after import or an explicit snapshot request. Pipe delivery is not an
application-level acknowledgement; the optional tester feedback reports input
acceptance separately and does not alter the real module destinations.

The new Track Model contracts define the integration boundary; the other
subsystems must implement these messages when their placeholder services are
developed. The Test UI can then be removed without changing Track Model's domain
logic.

## Domain behavior and limits

- Train telemetry supplies a current block and actual speed. Moving a train clears
  its old block; collisions and entry to closed blocks are rejected atomically.
  This is manual telemetry testing, not automatic train travel or train physics.
- An occupied switch cannot be thrown. The captured next-block output follows its
  Normal/Reverse command. Imported connectivity is validated before replacing state.
- Signals are installed equipment, independent of occupancy detection. The demo
  places them at Blue blocks 101, 103, 110, 120, 125, and 126 for junctions and
  their approaches, and Green blocks G1 and G4 for loop entry/station departure.
  Other blocks show no signal dot and show **No signal** in place of a command.
  Imported layouts use each block's `HasSignal` setting; these demo locations
  are illustrative and can be replaced by the actual track equipment data.
- Broken rail, track-circuit, and power failures are independent flags. Circuit or
  power failure reports occupancy as Unknown without deleting actual train
  position. Power failure makes the signal Unknown. Wayside speed/authority safety
  decisions belong to Track Controller.
- Passenger exchanges require a stopped train at a station and cannot board more than its waiting
  demand. An ExchangeId makes retries idempotent. One ticket is counted per boarding
  passenger. Station totals persist until layout reset; CTC throughput counts
  tickets in the preceding simulation hour. Rewinding time resets that hourly ledger.
- Passenger demand starts from the layout's initial value; automatic demand
  generation and real class track-file formats can be added when specified.
- Ambient temperature is set per block from the dashboard or Test UI (°F controls, °C contracts). Installed heaters turn on at/below 32°F and turn off above 32°F or on power failure. This is a documented demonstration policy, not a specified class heater threshold. Imported layouts without `hasHeater` show N/A. Direction metadata is displayed; train physics and enforcement belong to later integration.
- System time comes from shared SystemTimeMessage inputs; Track Model does not own
  another independent clock. The Test UI's clock provides those messages for testing.

## Validation

```powershell
dotnet build TrainControlSystem.sln
dotnet test TrainControlSystem.sln --no-build
```

The solution includes a Windows-only WPF workflow suite, using the real Test UI view model, serialized contracts, and a model-backed test transport. Production Test UI still references Contracts/Common only. These tests cover all 13 supplied checklist categories; the separate core tests remain platform-neutral.

Tests cover train movement/removal, collision and maintenance rejection, failures,
passenger retry accounting, switch behavior, invalid layouts, JSON/CSV import,
runtime-state exclusion from exported layouts, and ticket throughput.

See [review and executed test results](REVIEW_RESULTS.md) for the local review, equipment IDs, defects, and remaining system integration limits.
