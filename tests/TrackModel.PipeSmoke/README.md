# Live Track Model transport checks

This optional console check owns the four neighboring receiver endpoints plus the private TestUI endpoint. It exercises the real running dashboard with no reference to TrackModel.Core. It is intentionally separate from solution unit tests because it requires a running WPF process and exclusive receiving endpoints.

1. Start **TrackModel.Wpf** alone. Close **TrackModel.TestUI.Wpf** and other module receivers.
2. Click **Restore course lines** in the dashboard.
3. From the repository root run:

```powershell
dotnet run --project tests/TrackModel.PipeSmoke
```

The run checks 17 groups: private layout setup, clean initial profile, beacons, commanded speed/authority/switch, three signal values delivered to both Train Model and Train Controller, clearing on movement off a signal, crossing, demand, passenger exchange/occupancy/tickets, idempotent retry, all failures, rejected negative command, private clock setup/hourly accounting, CTC messages limited to the layout and ticket sales and Train Controller messages limited to traffic-light states. Every input also requires an accepted or rejected acknowledgement as expected. Failure produces a nonzero exit code.

The check changes simulation state. After it exits, click **Restore course lines**, reopen TestUI and refresh. Receivers are released on exit. This test proves standalone message delivery, not the teammates' unfinished module implementations.

The harness replaces the legacy demonstration ID `01` with canonical `001` before testing direct lights, and stores ticket messages per line so Red/Green updates cannot overwrite the Blue result.
