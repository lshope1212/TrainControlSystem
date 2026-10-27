# Blue Line: native TestUI input/output checks

> Historical October 7 pass. Derrick's October 8 correction removes the temperature/heater/maintenance controls, extra wayside outputs and Train Controller destination described below. See [current interface changes and checks](INTERFACE_CHANGES_OCT_8.md).

October 7, 2026. These checks used the actual Windows TestUI controls and inspected the messages displayed by its output panels while the separate Track Model dashboard was running. They are separate from the automated view-model tests and the console transport checks.

| Input configured manually | Observed result |
| --- | --- |
| Clean Blue Line startup | Train 01 on block 1, stopped; 09:00:00; zero commands/tickets; no failures. |
| Block 1: commanded speed 20 mph, authority 600 ft | Captured speed 20 mph and authority 600 ft; actual speed remained 0. |
| Actual speed 10 mph, then 0 | Captured actual speed changed independently; commanded speed and authority stayed 20 mph / 600 ft. |
| Block 5 switch Normal → Reverse | Switch output Reverse; next block changed 6 → 11. Dashboard highlighted the lower branch. |
| Block 6 signal Green → Yellow → Red | Wayside and Train Controller outputs GREEN/YELLOW/RED; Train Model track signal PROCEED/CAUTION/STOP. |
| Block 3 crossing Open → Closed | Captured gates Closed; dashboard displayed Gates down. |
| Unoccupied block 3 maintenance Closed | Captured maintenance CLOSED; block rendered gray on dashboard. |
| Block 3 temperature 32°F; commit with Refresh | Temperature 32°F, heater ON. Power failure made heater OFF and reported occupancy UNKNOWN; clearing power restored heater ON. Returning to 68°F turned heater OFF. |
| Move stopped train 01 to block 10 | Captured Occupied/train 01 on block 10; dashboard showed block 1 clear and block 10 occupied. |
| Station B waiting population 7; Set waiting | Captured 7 waiting, zero tickets. |
| Boarding 3, disembarking 2; Apply passenger exchange once | 4 waiting, totals 3 / 2, cumulative station tickets 3, preceding-hour tickets 3. Dashboard station table matched; Station C stayed at 24 waiting/zero tickets. |
| Refresh after exchange | Counts stayed 4 waiting / 3 tickets; no repeated exchange. |
| Block 10 broken rail, circuit, power; add and clear independently | Each corresponding failure flag changed. Circuit/power reported UNKNOWN occupancy while train 01 remained present. Clearing all restored OCCUPIED and NORMAL flags. |
| Paused clock Step 10 sec | 09:00:00 → 09:00:10 exactly. Counts did not change. |
| Clock multiplier 10; Start then Pause | Clock advanced about 50 simulation seconds at the observation around 6 real seconds after Start, and stopped around 09:01:39 when Pause was clicked around 10 real seconds after Start. Action/capture overhead accounts for the difference. |
| Paused time 10:00:01 | Preceding-hour tickets expired to 0; cumulative station tickets remained 3 and passenger totals remained 3 / 2. |
| Negative commanded speed −1 | Readable “Speed must be a nonnegative number” error; accepted output remained 0 mph. Restoring 0 returned to accepted input. |
| Try closing occupied block 10 | “An occupied block cannot be closed for maintenance” rejection; captured status remained OPEN and train remained present. |
| Output block 9, then 14 | Beacon Station B / next 10, then Station C / next 15. Train input remained on block 10. Station block 10 previously showed No beacon. |
| Layout details: Physical properties | All 15 rows visible, sections A/B/C, 50 m, zero grade/elevation, 13.889 m/s limit, Station B on 10 and Station C on 15. |
| Layout details: Equipment and connections | Initially exposed the crash described below. After correction, tab displayed all 15 rows: switch 5 with destinations 6/11, lights 6/11, crossing 3, beacons 9/14 with targets 10/15 and expected connections. |
| Remove train; choose Occupied again | CLEAR / No train, then OCCUPIED / train 01 on block 1. |

## Defect found and corrected

Opening **Equipment and connections** initially terminated TestUI. The Windows .NET event log identified a TwoWay binding to the read-only `CapturedBlockViewModel.HasSwitch` property. `DataGridCheckBoxColumn` defaults to a writable binding even when the grid is read-only.

All captured equipment checkbox bindings now explicitly use `Mode=OneWay`. A new STA regression test opens the real layout window, selects the equipment tab, renders its cells and checks the read-only bindings. The full build passed with zero warnings/errors; the full suite passed **97 tests (74 + 23)**. The corrected tab was then opened and visually checked again in the rebuilt native TestUI.

## Review limits and final state

This was a manual pass over major standalone controls, not an exhaustive test of every possible invalid value, timing race or integration with teammate applications. Earlier actual-workbook import parity and 18 real-process transport checks are documented in `ITERATION_2_READINESS.md`; a completed native XLSX file-picker import is not claimed here.

After rebuilding, both applications were reopened in the clean default profile: stopped train 01 on block 1, 09:00:00, zero commands/tickets, no failures, 24 waiting at each station. The final removal/reinsertion check left that profile intact.
