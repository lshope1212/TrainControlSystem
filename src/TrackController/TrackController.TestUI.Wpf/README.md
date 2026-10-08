# TrackController.TestUI.Wpf

Development-only simulator of the modules external to Track Controller. Runs
beside `TrackController.Wpf` as a separate application and does not host a controller.

- Sends simulated Track Model feedback and CTC requests through the normal input pipe.
- Acts as fake CTC and Track Model receivers, displaying actual controller outputs.
- References message contracts and shared transport only; no Core, Runtime, or Wpf reference.
- Can be removed when the real modules supply these inputs without changing the controller.

## Running it with the controller

Visual Studio: right-click the solution → **Configure Startup Projects** →
**Multiple startup projects** → set **TrackController.Wpf** and
**TrackController.TestUI.Wpf** to **Start** → **F5**.

Or use two terminals from the repository root:

```powershell
dotnet run --project src/TrackController/TrackController.Wpf
dotnet run --project src/TrackController/TrackController.TestUI.Wpf
```

Keep the real CTC/Track Model applications and other peer simulators closed during
this test; only one process can own each named pipe.

1. Controller: **Controller setup → Load sample layout**. Both territories run on PC.
2. Test UI: choose **Clear route**, then **Load preset**. This only edits the draft.
3. Test UI: **Send feedback**. Wait for all 12 feedback blocks to become current.
4. Controller: **Resume**.
5. Test UI: **Send movement request**. The defaults request 25 mph and 1,200 ft
   from block 5 over blocks 6, 7, and 8. Inspect **Controller response**.
6. Test UI: load **Switch mismatch** and send feedback. The controller withholds authority.

## Inputs and observations

**Repeat sent inputs every second** repeats only the last transmitted feedback
and movement request. Editing values or loading another preset does not send it.
Turning repeats off lets the configured feedback/request timeouts expire.

**Clear test inputs** stops repeats, cancels the submitted movement request, and
clears draft/captured state. Previously sent feedback expires normally; this
button does not reset or pause the controller. Use **Reset controller** in the
operator window when a new controller session is needed.

Switch and closure requests simulate CTC. Equipment feedback simulates Track
Model. Commands do not automatically become equipment feedback. Manual controls,
PLC loading, and PC/Pi placement remain in the operator application.

| Pipe | Listener | Sender |
| --- | --- | --- |
| `TrainControl.TrackController` | Controller | Test UI / real input modules |
| `TrainControl.CTC` | Test UI fake CTC | Controller |
| `TrainControl.TrackModel` | Test UI fake Track Model | Controller |

The input transport confirms delivery only. Receipts and snapshots arrive on the
output pipes; stale output is displayed as unavailable. A disconnected controller
has no local fallback inside this application. The `Wayside*` messages remain
controller-owned prototype contracts; team integration gaps are documented in the
[operator README](../TrackController.Wpf/README.md#module-integration).
