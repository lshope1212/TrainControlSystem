# CTC.TestUI.Wpf

A standalone, developer-facing **test harness for CTC.Core**. It is not part of the
production CTC operator UI (`CTC.Wpf`). Neither project references the other; both
reference the same `CTC.Core`.

```
TrainControl.Contracts
          ^
          |
       CTC.Core
       ^      ^
       |      |
 CTC.Wpf   CTC.TestUI.Wpf
```

## What it does

The Test UI lets you exercise CTC on its own, without Track Controller, Track Model
or any other module running:

- **Simulated inputs.** It builds the real `TrainControl.Contracts` messages that other
  modules will eventually send, and passes them into the real `CTCService`:

  ```
  Simulated External Module Input
              |
              v
   TrainControl.Contracts Message
              |
              v
          CTCService
              |
              v
       CtcSystemState
  ```

- **Captured outputs.** `CTCService` is constructed with a `RecordingMessageSender`
  instead of the production `NamedPipeMessageSender`. Anything CTC sends is recorded
  (as the actual contract object) and listed in the UI instead of being transmitted:

  ```
          CTCService
              |
              v
         IMessageSender
              |
              v
   RecordingMessageSender
              |
              v
         Test UI Output
  ```

- **Internal state.** It displays a snapshot of the real `CtcSystemState`.

## Rules

- **No CTC business logic here.** All behavior belongs in `CTC.Core`. The harness only
  builds contract messages, calls `ICTCService`, and displays results.
- Use the real shared contracts; do not create test-only duplicates of message types.
- Never use `NamedPipeMessageSender` or any other real communication in this project.
- Add each new input simulator or dispatcher action as its own small view model,
  composed in `MainWindowViewModel`.

## Running it

In Visual Studio: right-click **CTC.TestUI.Wpf** in Solution Explorer (under the
`CTC` solution folder) and choose **Set as Startup Project**, then press **F5**.
This is stored in your local `.vs` settings and does not change the solution.

From the command line:

```
dotnet run --project src/CTC/CTC.TestUI.Wpf
```

Quick check: click **Apply Sample Layout**, enter `G3` as the Block ID, click
**Close Block**. A `MaintenanceRequestMessage` appears under Captured CTC Outputs and
block G3 shows `Closed` as its requested maintenance state.
