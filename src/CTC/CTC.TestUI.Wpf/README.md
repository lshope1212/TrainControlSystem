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

1. TestUI: **Send Sample Layout**.
2. CTC: pick `A4` in the *Selected Block* drop-down.
3. TestUI: Block ID `A4`, Occupancy `Occupied`, Signal `Red` → **Send Block Status**.
   The CTC *Selected Block* panel updates immediately.
4. CTC: **Close Block**. TestUI *Captured CTC Outputs* shows
   `MaintenanceRequestMessage` with `BlockId = A4, RequestedState = Closed`.

Dispatch check: queue a schedule in the CTC Schedule Builder, then advance the TestUI
system time past its departure. *Captured CTC Outputs* shows a
`MovementSuggestionMessage` (CTC's suggested speed/authority) followed by a
`MovementRequestMessage` for the train. Speed and authority are calculated by CTC;
the TestUI no longer enters them.
