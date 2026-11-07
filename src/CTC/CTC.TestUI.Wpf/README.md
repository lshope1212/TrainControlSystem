# CTC.TestUI.Wpf

A development-only **simulator of the modules external to CTC**. It is not part of the
production CTC operator UI, and it does **not** host `CTC.Core`.

- It runs **beside** `CTC.Wpf`, as a separate process, at the same time.
- It **simulates the modules CTC talks to** (Track Controller, Track Model, shared
  system clock) by sending real `TrainControl.Contracts` messages into the running CTC
  process over CTC's named pipe.
- It acts as a **fake Track Controller endpoint**, receiving whatever the real CTC
  sends to the Track Controller and listing it under *Captured CTC Outputs*.
- It is **disposable**: when the real modules are integrated they take over the same
  pipes and contracts, and this project can be deleted with no change to `CTC.Core`
  or `CTC.Wpf`.

```
TestUI                                   CTC.Wpf

BlockStatusMessage / TrackLayoutMessage /
SystemTimeMessage
      |
      +------ pipe: TrainControl.CTC ------>
                                         CtcNamedPipeReceiver
                                               |
                                          CTCService (the one instance)
                                               |  StateChanged
                                          CTC UI updates


                                         dispatcher clicks Close Block
                                               |
                                          CTCService.CloseBlockAsync
                                               |
                                          MaintenanceRequestMessage
                                               |
                                          NamedPipeMessageSender
      <---- pipe: TrainControl.TrackController
      |
FakeTrackControllerReceiver
      |
Captured CTC Outputs
```

## Project references

```
TrainControl.Contracts   TrainControl.Common (Communication/)
          ^                     ^
          +---------+-----------+
                    |
              CTC.TestUI.Wpf
```

No reference to `CTC.Core` or `CTC.Wpf`. Everything crosses the process boundary as
a `MessageEnvelope` (`messageType` + `payload`, JSON, enums as strings) via
`TrainControl.Common.Communication`, the same code the production side uses.

| Pipe                           | Server (listens)  | Client (sends)                          |
| ------------------------------ | ----------------- | --------------------------------------- |
| `TrainControl.CTC`             | CTC.Wpf           | TestUI (later: real external modules)   |
| `TrainControl.TrackController` | TestUI (fake)     | CTC.Wpf (`NamedPipeMessageSender`)      |

Only one process can own a pipe, so do not run the real Track Controller and this
TestUI at the same time.

### Test clock (isolated testing only)

The *System -> CTC* clock is a **test clock source**, not the integrated system clock.
It sends `SystemTimeMessage { SystemTime, SpeedMultiplier = 1.0, IsPaused = !running }`
once per second so CTC can be tested without the rest of the system.

In the integrated system the one authoritative clock is hosted by the Launcher and
controlled from the real CTC window (see the root README, *Simulation timing*). Do not run
this TestUI's clock while the Launcher is running, or CTC would receive two clocks.
The CTC window's own clock buttons send to the Launcher's clock, so they report
"simulation clock is not running" when only this TestUI is providing time.

## Rules

- **No CTC business logic and no CTC.Core access here.** Inputs only build contract
  messages and send them; outputs only display what actually arrived.
- **No dispatcher actions.** Closing a block etc. is done in the real CTC window;
  this UI only observes the resulting outgoing message.
- Use the real shared contracts; never create test-only duplicates of message types.
- A successful send means CTC's pipe received the bytes, not that CTC accepted the
  message (e.g. an unknown block ID is rejected by CTC; see CTC's status bar).
- Add each new simulated input as its own view model deriving from
  `CtcInputViewModelBase`, composed in `MainWindowViewModel`.

## Running it with CTC

Visual Studio: right-click the solution → *Configure Startup Projects…* →
*Multiple startup projects* → set **CTC.Wpf** and **CTC.TestUI.Wpf** to *Start* → F5.

Command line (two terminals):

```
dotnet run --project src/CTC/CTC.Wpf
dotnet run --project src/CTC/CTC.TestUI.Wpf
```

Quick check:

1. TestUI: **Send Sample Layout**. This sends the whole course railway (Blue 15, Red 76 and
   Green 150 blocks) from `SampleData/course-vf5-layout.json`, generated from the course
   workbook *Track Layout & Vehicle Data vF5*. Blue keeps IDs `A1`..`C15`; Red and Green IDs
   are line-qualified (`RED-C7`, `GREEN-W132`). The CTC *Current Line* selector then lists
   all three lines.
2. CTC: click block `A4` on the *Territory Overview* map (or pick it in the *Selected Block*
   drop-down; the two stay in sync).
3. TestUI: Block ID `A4`, Occupancy `Occupied`, Signal `Red` → **Send Block Status**.
   The CTC *Selected Block* panel updates immediately.
4. CTC: **Close Block**. TestUI *Captured CTC Outputs* shows
   `MaintenanceRequestMessage` with `BlockId = A4, RequestedState = Closed`.

CTC treats a block it has no status for as Unknown, which is unsafe, so before dispatching
send `Clear` statuses for the train's start block and the blocks ahead (a TestUI status
has no report time, so it is held until replaced and never goes stale).

Dispatch check: queue a schedule in the CTC Schedule Builder, then advance the TestUI
system time past its departure. CTC releases the train (*Dispatched Trains*, Release
`Awaiting placement at A1`) and sends nothing yet: no module receives a release request.
Then send `A1 Occupied` (or **Start (Occupy First Block)** below). CTC detects the train
and *Captured CTC Outputs* shows its first `MovementSuggestionMessage`: request ID, target
`A1`, the path ahead and the authority measured from A1's entry. Speed and authority are
calculated by CTC; the TestUI no longer enters them. No `MovementRequestMessage` is sent.

Re-plan check: in the CTC *Dispatched Trains* tab, select the train, change its
Destination and/or Arrival Time and click **Apply Changes**. *Captured CTC Outputs*
shows a cancellation of the old request (`active: false`, same request ID) followed by a
new `MovementSuggestionMessage` with a new request ID and the recalculated speed/authority.
Each **Next Occupancy Step** likewise cancels the old request and issues one for the
train's new block. The TestUI is not a Track Controller, so the *Track Controller* column
stays "Sent; awaiting acknowledgement".

Train-tracking check (*Occupancy Sequence Simulator*, TEST-ONLY): it sends only anonymous
`BlockStatusMessage`s through the same pipe, never a train ID, so CTC must infer which
train moved.

1. Dispatch a train that starts at `A1` (as above). CTC *Dispatched Trains* shows
   `Current Block A1`.
2. TestUI: Route blocks `A1,A2,A3,A4,A5` → **Start (Occupy First Block)** (sends `A1 Occupied`).
3. **Next Occupancy Step** sends `A2 Occupied` then `A1 Clear`; CTC shows `Current Block A2`.
4. **Next Occupancy Step** again: `A3 Occupied`, `A2 Clear`; CTC shows `A3`.
5. **Reset (Clear All)** reports every block the simulator left occupied as Clear.

The simulation time of these messages does not matter for the inference.
