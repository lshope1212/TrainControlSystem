# Live Track Model transport checks

This optional console check owns the four neighboring receiver endpoints plus the input-feedback endpoint and exercises the real running dashboard. It has no reference to TrackModel.Core. It is intentionally separate from solution unit tests because it requires a running WPF process and exclusive receiving endpoints.

1. Start **TrackModel.Wpf** alone. Close **TrackModel.TestUI.Wpf** and other module receivers.
2. Click **Restore Blue Line** in the dashboard.
3. From the repository root run:

```powershell
dotnet run --project tests/TrackModel.PipeSmoke
```

The run checks 18 groups of actual outputs: layout, clean initial profile, beacons, commanded speed/authority/switch, three signal aspects on all relevant outputs, crossing, demand, passenger exchange/occupancy/tickets, idempotent retry, all failures, heater threshold, maintenance, rejected movement/negative command and CTC time/hourly accounting. Every input also requires an accepted or rejected acknowledgement as expected. Failure produces a nonzero exit code.

The check changes simulation state. After it exits, click **Restore Blue Line**, reopen TestUI and refresh. Receivers are released on exit. This test proves standalone message delivery, not the teammates' unfinished module implementations.
