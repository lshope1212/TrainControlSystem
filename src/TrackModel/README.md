# Track Model implementation checkpoint

This checkpoint implements the first pass of Track Model domain state and shared
messages. The dashboard, separate Track Model Test UI, and application named-pipe
wiring are **not implemented yet**. The existing Track Model window is still the
original placeholder.

Implemented in `TrackModel.Core`:

- Validated layout import through `TrackFileRepository` (JSON and CSV, SI units).
- A 36-block Blue/Green demonstration layout and optional demo state matching the
  supplied dashboard's two trains and block 119 circuit failure.
- Block equipment commands, train occupancy updates, failure injection, maintenance
  state, station passenger exchange, and ticket totals.
- Atomic rejection of invalid moves, occupied-switch changes, and invalid layouts.
- Passenger exchange IDs to prevent duplicate accounting on retries.
- Factories for outgoing shared-contract layout, block, train environment, and
  ticket-sales messages. These factories do not send messages yet.

The Test UI will act as external modules in a separate process. It must reference
Contracts/Common only and must observe actual outgoing messages, rather than host
its own copy of Track Model state. The dashboard will host one `TrackService` and
marshal inbound updates to the UI thread, following the existing CTC pattern.

## Layout format

JSON uses a root object containing `name` and `blocks`. Each block requires `id`,
`lineId`, and a positive `lengthMeters`. IDs are globally unique. Optional fields:
`number`, `section`, `elevationMeters`, `gradePercent`, `speedLimitMetersPerSecond`,
`temperatureCelsius`, `stationName`, `initialWaitingPassengers`, `hasSwitch`,
`hasSignal`, `hasCrossing`, `connectedBlockIds`, `normalNextBlockId`, and
`reverseNextBlockId`.

```json
{
  "name": "Small layout",
  "blocks": [
    { "id": "B1", "lineId": "Blue", "lengthMeters": 100,
      "hasSignal": true, "connectedBlockIds": ["B2"] },
    { "id": "B2", "lineId": "Blue", "lengthMeters": 120,
      "stationName": "Station A", "initialWaitingPassengers": 24,
      "connectedBlockIds": ["B1"] }
  ]
}
```

CSV uses the same property names as headers, case-insensitively; `Id`, `LineId`,
and `LengthMeters` are required. Connections are separated with semicolons inside
one field. Boolean values are `true`/`false`. Quoted fields are supported. Switches
need two distinct destinations included in their connection list. Runtime train,
equipment, failure, and passenger-total state is reset when a layout is loaded.

## Domain behavior

- Train telemetry chooses the occupied block; this does not implement train physics
  or automatic travel. An empty current block removes the train.
- Circuit and power failures report occupancy as Unknown while retaining the actual
  train position. Power failure also makes the effective signal Unknown.
- Boarding consumes waiting passengers and adds one ticket per boarding passenger.
  Ticket throughput uses an average since layout load with a one-hour minimum
  window to avoid startup spikes; the final class specification may refine this.
- Commands and message quantities use SI units. UI conversion to mph, feet, and
  Fahrenheit remains part of the upcoming WPF work.

## Validation

From the repository root:

```powershell
dotnet build TrainControlSystem.sln
dotnet test TrainControlSystem.sln --no-build
```

The checkpoint adds ten domain tests for train moves, collision rejection, failure
reporting, idempotent passenger exchange, switch behavior, validation, and demo state.
