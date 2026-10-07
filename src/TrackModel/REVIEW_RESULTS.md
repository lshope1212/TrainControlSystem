# Track Model review and executed tests

Reviewed October 7, 2026 (Asia/Shanghai). Repository: `https://github.com/Caden404/ECE1140-Trains-`.
Base: `origin/Derrick`, commit `24456ff98b7ab221d5828d816ed9cea960266a98`.
Local review branch: `derrick-track-model-review`. Nothing was pushed.

## What was checked

The supplied 13-section TestUI checklist, Track Model source and contracts, and the supplied grading PDFs were reviewed. The Detailed Rubric's Track Model section (page 7, items 3.0–3.9) requires loading/displaying track properties, passenger/ticket counts, occupancy, track circuits, environmental temperature, heaters, equipment states, and all three failures. The Deliverable Descriptions (page 2, Iteration 2) requires a separate working input/output test interface and starts with the Blue Line.

Native computer-use checks exercised the two real WPF processes and their named-pipe outputs. The new Windows-only test project additionally drives the production TestUI view model against serialized contracts and a model-backed test transport; it does not claim to be a pixel or named-pipe integration test. The production TestUI still owns no Track Model instance and references Contracts/Common only.

## Corrected defects and missing controls

1. Refresh could request a snapshot before a debounced edit was sent; the arriving layout cleared that edit. Refresh now flushes edits first and serializes concurrent flushes.
2. Refresh reset Output block to the controller block. It now preserves the independently selected output.
3. Changing controller blocks before debounce discarded the previous block's edit. Pending commands/failures/temperature are captured for their original block before switching.
4. The requested Remove train action was missing. It now sends Clear occupancy for the selected train; choose Occupied to reinsert the train.
5. Missing switches/crossings showed Unknown, which was ambiguous with failed equipment. They now show N/A; their controls remain disabled.
6. Elevation was shown in metres in TestUI while the dashboard used feet. Both now show feet.
7. Passenger totals and cumulative tickets were absent from TestUI. They are now captured outputs, distinct from the rolling one-hour ticket count. Exchange remains an explicit one-shot action, and moving trains cannot exchange passengers.
8. Step 10 sec used the multiplier. It now always advances ten simulation seconds. The running clock uses elapsed wall time, retains fractional seconds, validates a positive multiplier, and wraps the displayed time-of-day consistently at midnight while retaining elapsed days internally. Pause/resume preserves those days, so midnight does not clear the preceding-hour ticket ledger.
9. Snapshot acknowledgements could hide validation errors. They no longer overwrite those messages, and initial connection now shows Connected after a snapshot.
10. Environmental temperature was read-only, heaters were absent, and speed limit/direction/beacon were not all shown on the dashboard. Added per-block °F temperature controls, heater state, and the missing property displays. The additive temperature input contract carries °C.
11. Clock buttons clipped at minimum TestUI size. Reduced spacing and visually verified all controls at 1180×720.

## Executed checklist results

The automated workflow tests assert each intermediate expected output in the supplied value sequences. Native UI checks additionally confirmed the displayed data and real module transport; equipment checks used the installed equipment listed below.

| # | Test | Result and observed behavior |
|---|---|---|
| 1 | Stable starting state | PASS — Blue 104, Train 01, Occupied, actual 22 mph; all three failures Normal. Clock starts paused. |
| 2 | Commanded speed | PASS — 30 → 15 → 30 mph; actual remained 22 mph, train/occupancy unchanged. |
| 3 | Authority | PASS — 600 → 0 → 600 ft; command stayed 30 mph, actual stayed 22 mph. |
| 4 | Signal | PASS — block 103: Green/Proceed → Yellow/Caution → Red/Stop → Green/Proceed. Train actual speed is independent. |
| 5 | Actual speed | PASS — 10 → 0 → 22 mph; command 30 mph and authority 600 ft retained. |
| 6 | Movement/removal | PASS — Train 01 moved 104 → 105 at 22 mph, 104 cleared, Remove train cleared 105, Occupied reinserted it on 104. |
| 7 | Switch | PASS — block 103 Normal → Reverse → Normal; next block 104 → 121 → 104. |
| 8 | Crossing | PASS — block 109 Open → Closed → Open; signal remained independent. |
| 9 | Failures | PASS — each flag on/off and all three together; all indicators cleared afterward. Circuit/power report Unknown occupancy while retaining physical Train 01 and its actual speed. Speed/authority remain the controller's commands. |
| 10 | Passenger exchange | PASS — baseline 12 waiting, 12 boarded, 8 disembarked, 12 tickets. First 3/2 exchange yielded 9/15/10/15; Refresh repeated neither exchange. Second yielded 6/18/12/18. A 0/0 exchange changed nothing. |
| 11 | Beacon/elevation/grade | PASS — 104 reports Station A, 720 ft, 1.5%; 105 reports No station, 720 ft, 1.5%, matching demo data. |
| 12 | Clock | PASS — paused time remained fixed; 09:42:18 → 09:42:28 → 09:42:38; manual 10:00:00 reached dashboard. Measured 14 simulation seconds / 14.306 real seconds at 1× and 26 / 13.545 at 2×, within whole-second display/tick precision. Pause held fixed. A step at 2× still added exactly 10 seconds. |
| 13 | Selection/validation | PASS — output selection survives Refresh without moving the train; blocks 104/105 retain 30/15 mph independently. Negative speed, authority, passengers, letters, and 25:99:99 are rejected with readable messages; missing equipment disables controls and displays N/A/No signal. |

Additional native checks: dashboard and TestUI both set 32°F/68°F and display heater On/Off. Domain regression tests also verify heater loss/restoration with power, absent heater equipment, absolute-zero validation, atomic rejection of moving passenger exchanges, collision/maintenance rejection, retry idempotency, and ticket expiry. A further WPF regression verifies that tickets sold just before midnight survive the day transition and subsequent pause/resume.

Final validation:

```text
dotnet build TrainControlSystem.sln          PASS (0 errors)
dotnet test TrainControlSystem.sln --no-build
  TrainControl.Tests                       68 passed
  TrackModel.Wpf.Tests                     18 passed
  Total                                    86 passed, 0 failed, 0 skipped
git diff --check                            PASS
```

A full initial build reported one existing nullable warning in `CTCService.cs:96`; no CTC source was changed. The final build still reported that same unrelated CTC warning and no errors.

## Equipment and behavior to use when testing manually

- Station A: Blue 104, initial physical elevation 219.456 m = 720 ft, grade 1.5%. Demo startup has already exchanged 12/8 passengers, leaving 12 waiting and 12 tickets.
- Blue 103 switch: Normal destination 104, Reverse destination 121. It also has a signal.
- Blue 109: crossing. Blue 104/105: no installed switch, crossing, or signal.
- Blue signals: 101, 103, 110, 120, 125, 126; Green signals: G1, G4.
- Circuit/power failure makes reported occupancy Unknown, without erasing physical occupancy. Power also makes an installed signal Unknown. Existing documented policy leaves speed/authority safety decisions to Track Controller.
- The beacon output is the station name for the selected block, continuously exposed as environment data; there is no one-time physical beacon-passage trigger.
- Ticket rate means tickets in the preceding simulation hour. Cumulative station tickets do not expire. Rewinding time clears the hourly ledger; layout reset clears both.
- Installed heaters run at/below 32°F when power is available. This threshold is an explicit demonstration policy because the supplied rubric does not specify a threshold. This is an on/off model, not a thermal simulation.

## Limits relevant to grading

The UI fixes do not establish complete systemwide rubric compliance. The loaded demo is illustrative and is not the supplied class XLSX surveyed track. Import supports validated JSON/CSV; XLSX-specific parsing is still absent. Layout files can set heater equipment and Forward/Reverse/Bidirectional metadata; legacy files default to no heater and Bidirectional. Direction is displayed metadata, not enforced train motion. Train Model, Train Controller, and Track Controller remain team architecture placeholders in this branch, so actual train physics, PLC behavior, dispatch-to-station integration, and agreed physical beacon/heater behavior require team integration. Passenger availability is station waiting demand; onboard passenger capacity belongs to Train Model.

## Run and review locally

Close existing copies before rebuilding or starting another pair, to avoid locked binaries and competing pipe listeners. Double-click `Run-TrackModel.cmd` at the repository root, or use the existing Track Model standalone Visual Studio launch profile. The script builds the solution and opens both windows. Only one tester should own the external receiving endpoints.

The applications were left open in the demo starting state with the tester clock paused, so the supplied sequence can be rerun. Review this local branch before deciding whether to push to Derrick. No remote write, pull request, or push was performed.
