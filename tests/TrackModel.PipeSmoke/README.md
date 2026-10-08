# Live Track Model transport checks

This optional console check owns the three neighboring receiver endpoints plus the private TestUI endpoint and monitors Train Controller to detect forbidden traffic. It exercises the real running dashboard with no reference to TrackModel.Core. It is intentionally separate from solution unit tests because it requires a running WPF process and exclusive receiving endpoints.

1. Start **TrackModel.Wpf** alone. Close **TrackModel.TestUI.Wpf** and other module receivers.
2. Click **Restore Blue Line** in the dashboard.
3. From the repository root run:

```powershell
dotnet run --project tests/TrackModel.PipeSmoke
```

The run checks 16 groups: private layout setup, clean initial profile, beacons, commanded speed/authority/switch, three Train Model track-signal values, crossing, demand, passenger exchange/occupancy/tickets, idempotent retry, all failures, rejected negative command, private clock setup/hourly accounting, ticket-only CTC messages and zero Train Controller messages. Every input also requires an accepted or rejected acknowledgement as expected. Failure produces a nonzero exit code.

The check changes simulation state. After it exits, click **Restore Blue Line**, reopen TestUI and refresh. Receivers are released on exit. This test proves standalone message delivery, not the teammates' unfinished module implementations.
