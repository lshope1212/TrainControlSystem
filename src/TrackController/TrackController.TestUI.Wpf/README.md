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

1. Controller: **Controller setup**, choose a line, then **Load setup**.
   This loads the sample PLC and starts paused. Green Line territories run on PC;
   so does the single Blue Line territory.
2. Test UI: choose **Clear route**, then **Load preset**. This only edits the draft.
3. Test UI: **Send feedback**. Wait for every feedback block (12 on Green, 15 on Blue) to become current.
4. Controller: **Resume**.
5. Test UI: **Send movement request**. The default speed is 25 mph; requested
   authority is calculated from the route. Green: block 5 to the entrance of block
   8 via 6 and 7, a 300 m / 984.25 ft limit. Blue: block 4 to the entrance of block
   7 via 5 and 6, a 150 m / 492.13 ft limit. Inspect **Controller response**.
6. Test UI: load **Switch mismatch** and send feedback. The controller withholds authority.

The block IDs, route, and equipment in the drafts and presets come from the layout the
controller reports: the route is the first switch, the block that leads into it, and the
two blocks after its normal exit. Layouts without a switch have no presets; edit the
feedback rows directly.

## Inputs and observations

Movement uses the occupied target block and requested path; there is no train
offset input. **Calculated authority (ft)** is read-only and updates when the target
or path changes. It sums the lengths from the target block's entry to the **entry of
the final requested block**: include the current block and intermediate blocks,
but exclude the destination's own length. An empty path has zero authority.
Invalid, repeated or disconnected blocks show no calculated value and cannot be
sent. Calculations and messages retain full precision in metres; the UI shows feet.
The returned limit identifies the permitted endpoint, not distance remaining from
the train's unknown position within its block.

After **Send movement request**, manually move occupancy along the route and select
**Send feedback**. When the old target is clear and the next route block is occupied,
the Test UI acts as CTC: it cancels the old request, removes the next block from the
remaining path, sets it as the new target and sets the switch entry to the previous
block. It subtracts the previous block's full length from the requested limit,
preserving the original endpoint and suggested speed. It sends a new request ID
unless the destination has been reached. The movement
fields update from the last sent request; unsent movement edits are not sent by this
automatic update. Authorized fields always come from the controller's real output.

For example, on Blue Line, target 4 with path `5, 6, 7, 8` calculates 200 m /
656.17 ft. As occupancy advances to blocks 5, 6, 7 and 8, authority becomes
150, 100, 50 and 0 m (492.13, 328.08, 164.04 and 0 ft). Each Blue block is 50 m.
On arrival at block 8 with block 7 clear, the request is cancelled and removed from
controller output. Block 8 stays occupied. The Test UI does not need the train to
leave its destination to finish, and repeating feedback cannot recreate the request.
You can send those two occupancy changes together or in either order. While both
blocks are occupied, or neither is occupied, the existing controller rules may
withhold movement until the transition is complete. Merely editing the feedback
grid or repeating unchanged feedback does not advance the request.

Only the last sent movement is followed. The next block must have been observed
clear during that request; an existing occupied obstacle is not assumed to be this
train. Blocks are never skipped. This is a single-train test convenience, not real
train tracking. Faults, signal feedback, switch alignment and other restrictions
still determine the authorization. Cancelling or clearing test inputs stops route
updates. Arrival leaves an empty path and zero authority in the draft; send a new
route to continue. An existing zero-authority request is also removed once its
target clears. Automatic updates also work with
**Keep test inputs active** off, but watchdog timeouts still apply between sends.

**Track feedback → Equipment → Actual aspect** includes **Super Green**. It
adds no signal speed cap; normal track limits, CTC requested speed and authority,
and equipment protection still apply. Red/unknown withhold movement, and Yellow
restricts speed. Feedback reports actual equipment state; selecting an aspect
does not itself issue a controller signal command.

Pause withholds movement and keeps signals red. You can still send feedback and
requests. Manual equipment commands require maintenance mode and their normal
protection checks; CTC switch requests while paused also need maintenance mode.
The 5-second feedback and 10-second request watchdogs use real elapsed time and
continue while paused. The displayed simulation clock and speed multiplier do
not change those watchdog periods.

**Keep test inputs active** refreshes the last transmitted feedback and movement
request once per second, simulating the updates the real modules send. Editing
values or loading another preset does not send it. Turn this off to test the
configured feedback/request timeouts (5 and 10 seconds by default).

The footer shows whether controller output is current. Switch and closure
receipts stay beside their Send buttons; transport counters are not part of the
test workflow.

**Clear test inputs** stops repeats, cancels the submitted movement request, and
clears draft/captured state. Previously sent feedback expires normally; this
button does not reset or pause the controller. Use **Reset controller** in the
operator window when a new controller session is needed.

Switch and closure requests simulate CTC. Equipment feedback simulates Track
Model. Commands do not automatically become equipment feedback. Manual controls,
PLC loading, and PC/Pi placement remain in the operator application.

Each CTC request section has its own Send button. **Cancel block … movement**
cancels only the last movement sent from this window and stops repeating it.
It uses that movement's original block and request ID, even after the draft
target changes. Other movements, switch requests, and closures are unaffected.
Sending a new movement replaces an existing movement for the same target block.
Earlier movements for other targets remain until cancelled or expired.

For switch testing, load **All blocks clear** and select **Send feedback** with
repeats enabled. The usual **Clear route** preset occupies the switch approach:
block 5 locks switch 6 on Green, and block 4 locks switch 5 on Blue.
Resume the controller for a CTC switch request, or enable its
maintenance mode for manual operation. Send Normal or Reverse and inspect
**Command**. In **Track feedback → Equipment**, set the reported switch position
and select **Send feedback** to simulate confirmation from Track Model.
The switch request result remains visible in its section, including the block
or condition that prevents movement.

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
