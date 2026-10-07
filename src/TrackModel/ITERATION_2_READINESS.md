# Track Model: Iteration 2 software review

Reviewed October 7, 2026. Scope: Derrick's standalone Track Model dashboard and separate TestUI. Local branch: `derrick-track-model-review`. No remote push.

## Applicable grading criteria

The **Detailed Rubric, page 6, “ITERATION #2 - SUB-SYSTEM PRESENTATIONS”** is the grading basis. Its software criteria total **90 points**. The presentation/observation criteria (1.5–1.7, 20 points) are outside this request. The Track Model 3.0–3.9 checklist on page 7 is under Work Package 3, and is **not** substituted for the Iteration 2 rubric.

The **Deliverable Descriptions v5, page 2, item 6** calls for a full subsystem UI, a test interface, key functionality and as many minor functions as possible. It explicitly permits stubs for communicating subsystems and says to start with the Blue Line. Thus, standalone named-pipe receivers in TestUI satisfy the permitted test setup. Systemwide dispatch and real neighboring-module integration are Iteration 3 work.

| Iteration 2 item | Points | Implemented evidence |
| --- | ---: | --- |
| 1.1 Sub-System User Interface | 20 | Separate dashboard: workbook-based selectable schematic, all-block table, physical properties, train telemetry, equipment, station counts, failures, temperature, import/export and delivery status. |
| 1.2 Key Subsystem Inputs/Outputs Work | 30 | Layout load; commanded speed/authority; train occupancy/actual speed; three failures; track-controller occupancy/failure outputs; train-model speed/authority/beacon/environment outputs; traffic-light output and CTC ticket/layout output. See the I/O table below. |
| 1.3 All other Subsystem Inputs/Outputs Work | 20 | Switch/signal/crossing commands and states, stopped passenger exchange, adjustable station demand, temperature/heaters, maintenance, simulation time, full static layout definitions, independent output selection, validation and input feedback. |
| 1.4 Separate Test UI for Inputs and Outputs | 20 | Separate executable and project. Production TestUI references Contracts/Common only. It sends to the real Track Model process and captures the four external destinations. Readable panels, layout details and raw message log expose outputs. |

No software criterion is knowingly omitted from the supplied Iteration 2 scope. This table records implementation and evidence, rather than a prediction of the instructor's awarded score.

## Course Blue Line

The default is now `SampleLayouts/blue-line.json`, embedded in Core so it also works when launched outside the repository directory. Its physical values come from **Track Layout & Vehicle Data vF5.xlsx → Blue Line → A1:J16**. The embedded schematic in that worksheet supplies the branching connectivity.

| Source feature | Implementation |
| --- | --- |
| Blocks 1–15; sections A, B, C | Fifteen selectable Blue blocks, with the original numbers and sections. |
| 50 m length; 0% grade; 50 km/h limit; zero cumulative elevation | Preserved in SI internally and in layout messages. Dashboard uses feet/mph; captured layout details show SI values, including 13.889 m/s = 50 km/h. |
| Yard → 1 → 2 → 3 → 4 → 5 | Yard is a visual endpoint. The workbook assigns it no numbered block or physical values, so no artificial yard block is invented. |
| Switch at 5 | Normal connects 5–6; Reverse connects 5–11. The selected branch is highlighted. Throwing an occupied switch is rejected. |
| Upper branch 6–10 | Ends at Station B on block 10. |
| Lower branch 11–15 | Ends at Station C on block 15. There is no return loop or bypass. |
| Railway crossing at 3 | Open/Closed gate command and captured state. |
| “Switch …; Light” at 6 and 11 | Approach lights at 6 and 11. These rows describe the shared switch connection; they are not two additional independently movable switches. |
| Transponder at 9 and 14 | Beacon on block 9 identifies Station B / target 10; beacon on 14 identifies Station C / target 15. Stations do not implicitly emit beacons. |

The workbook has no Blue Line direction, heater, weather or waiting-population columns. The simulation profile explicitly supplies bidirectional travel, installed heaters, 68°F ambient temperature and 24 waiting passengers per station. These are test configuration, not claimed workbook facts. Installed heaters turn on at/below 32°F when powered; the supplied Iteration 2 materials specify no threshold. Temperature and waiting population can be changed through the UI.

**Import layout** accepts JSON, CSV and the actual course XLSX. XLSX import deliberately reads only the course **Blue Line** format, validates its headers, blocks and infrastructure, and constructs the diagram's documented topology. It does not silently import Red/Green, schedules, drawings, macros or arbitrary workbooks. Elevation formulas are evaluated from length/grade without depending on stale formula caches. Import validates before replacing live state. **Restore Blue Line** restores a clean profile with stopped train 01 on block 1 at 09:00:00, zero commands/tickets and no failures.

## Input/output dictionary and controls

All physical message values use SI; speed/authority/elevation/temperature controls use mph/ft/°F. Counts are nonnegative whole people/tickets. Equipment values use shared enums. Layout and block IDs are strings.

| Direction / source or destination | Contract or operation | Fields and behavior | Visible control/output |
| --- | --- | --- | --- |
| Track Builder → Track Model | JSON / CSV / course XLSX | Block IDs, line, section, length, grade, elevation, speed limit, station, beacon, equipment and connections | Dashboard **Import layout**, schematic, **All blocks**, selected properties; TestUI **Layout details** |
| Track Controller → Track Model | `TrackModelCommandMessage` | Block, commanded speed (m/s), authority (m), switch, signal, crossing; actual speed stays independent | TestUI **From Track Controller**; dashboard Movement/Equipment |
| Train Model → Track Model | `TrackModelTrainUpdateMessage` | Train ID, current block, actual speed (m/s), explicit boarding/disembarking counts and exchange ID | TestUI **From Train Model**, **Remove train**, **Apply passenger exchange once**; dashboard occupancy/stations |
| Murphy → Track Model | `TrackModelFailureCommandMessage` | Broken rail, track circuit, power flags independently | Both UIs' failure checkboxes; captured flags and physical/reported occupancy |
| Simulation environment → Track Model | `TrackModelTemperatureCommandMessage` | Per-block °C, validated finite and ≥ absolute zero | Dashboard **Apply** temperature; TestUI temperature (commit by leaving field); captured temperature/heater |
| Simulation demand → Track Model | `TrackModelPassengerDemandMessage` | Replace a station's waiting population; does not board passengers or sell tickets | TestUI **Simulation station demand → Set waiting**; captured demand and dashboard Stations |
| Wayside / maintenance stub → Track Model | `MaintenanceRequestMessage` | Open/Closed; cannot close occupied block or enter closed block | TestUI **Maintenance**; dashboard selected status / gray closed block; captured maintenance |
| Shared simulation clock stub → Track Model | `SystemTimeMessage` | Elapsed `TimeSpan`; display HH:mm:ss. Pause, positive multiplier, exact 10-second step and midnight continuity | TestUI clock; dashboard system time; CTC captured time mirror |
| Track Model → Track Controller | `TrackModelBlockStateMessage` | Reported occupancy, all three failure flags, switch, signal, crossing, maintenance | TestUI **To Track Controller** and raw captured messages |
| Track Model → Train Model | `TrackModelTrainEnvironmentMessage` | Train ID, commanded/actual speed, authority, signal, beacon and station target, next branch, elevation/grade, temperature, demand, passenger/ticket totals, heater, speed limit/direction | TestUI **To Train Model**, additional passenger/environment panel, layout details and raw log |
| Track Model → Train Controller | `TrackModelSignalMessage` | Block/train IDs and traffic-light state | TestUI **Traffic light** and raw log |
| Track Model → CTC | `TrackLayoutMessage` | Full static physical/equipment/route definitions; no presentation coordinates | TestUI **Layout details**, raw log |
| Track Model → CTC | `TicketSalesMessage` | Count of tickets sold in the preceding simulation hour, per line | TestUI hourly ticket output. Station totals are cumulative and separately labeled. |
| TestUI → Track Model → TestUI | Snapshot request / input result | Refresh flushes edits first; accepted/rejected messages; consistent snapshot IDs | **Refresh outputs**, status bar, **Captured messages** |

Passenger exchange requires a stopped train at a station and cannot exceed waiting demand. Normal telemetry never resends an exchange. Exchange IDs prevent duplicated retries. Invalid movements, collisions and input values leave accepted model state intact. Circuit/power failure makes reported occupancy Unknown while retaining physical train position. Power failure makes signals Unknown and turns heaters off. Track Controller owns the subsequent safe speed/authority decisions.

## Validation evidence

| Check | Result |
| --- | --- |
| Solution build | Passed with zero warnings and zero errors on the final build. |
| Automated suite | **97 passed: 74 core/common/module tests + 23 WPF workflow/rendering tests.** No failures or skips. Includes a regression test that opens the captured layout's equipment tab and renders its read-only flags. |
| Real-process transport | **18 output check groups passed** against the running dashboard, with separate receivers for all four destinations and accepted/rejected acknowledgements for every input. Covers equipment, layout, beacons, train/demand/exchange/tickets, all failures, heaters, maintenance, validation and time. Reproduce with [TrackModel.PipeSmoke](../../tests/TrackModel.PipeSmoke/README.md). |
| Real user-supplied workbook | Read with the production importer; its serialized result exactly matched every field of the bundled 15-block Blue Line. Source file SHA-256: `9dfde0e22dbee87e2a33688871940cef47e439562eed80f3a38ec3f8d3be7c37`. |
| Workbook parser regressions | Inline/shared strings, formulas without cached values, wrong headers and missing blocks tested. |
| Production TestUI workflows | Commands, all equipment outputs, physical properties/beacon placement, station demand/exchange/tickets, failures, heaters, maintenance rejection, refresh/selection, clock and validation tested. |
| Native Windows inspection | Manually configured TestUI speed, authority, actual speed, switch, all signal aspects, crossing, maintenance, temperature, failures, station demand, one-time passenger exchange, clock and invalid inputs; observed their captured outputs and cross-checked dashboard state. See [Blue Line manual results](MANUAL_BLUE_LINE_RESULTS.md). Native XLSX file-picker opened; importer was independently verified against the actual file. |

Automated WPF workflows use the production view model with serialized contracts and a model-backed transport. They are not pixel tests or proof of integration with the teammates' unfinished modules. The earlier native 13-section checklist results are retained in `REVIEW_RESULTS.md`; their old 104/103/etc. block numbers describe the previous demonstration, not the new default.

## Manual Blue Line walkthrough

Close existing Track Model processes before rebuilding. Run `Run-TrackModel.cmd` from the repository root (requires .NET 10 SDK), or start the dashboard and TestUI projects in Visual Studio. Run the standalone TestUI with the dashboard alone, since it occupies the neighbors' receiving endpoints.

1. Click **Restore Blue Line**. Refresh TestUI. Expect 15 layout blocks, train 01 on block 1 stopped, no failures, time 09:00:00, 24 waiting at each station, zero tickets.
2. On command block **1**, set speed **20 mph**, authority **600 ft**. Refresh. Captured commanded speed/authority should change; actual speed remains 0. Set actual speed to **10**, then **0** to demonstrate independent telemetry.
3. Select command block **5**. Switch Normal → Reverse → Normal. Next block should be **6 → 11 → 6**, and the dashboard highlights the chosen branch.
4. Select command block **6**. Signal Green → Yellow → Red → Green. Check the track signal, wayside signal state and Train Controller traffic-light output. Block **11** is the other equipped signal block.
5. Select command block **3**. Crossing Open → Closed → Open. Check the captured gate state and schematic label.
6. Leave train 01 Occupied and set its current block to **10**, actual speed **0**. In station demand choose **Station B**, enter **7**, click **Set waiting**. Enter boarding **3**, disembarking **2**, click **Apply passenger exchange once**. Expect **4 waiting**, **3 boarded / 2 disembarked**, **3 station tickets**, **3 tickets/hour**. Refresh twice; counts must remain unchanged. Station C remains independent.
7. Set only **Output block** to **9**, then **14**, then **10**. Expect **Station B**, **Station C**, then **No beacon**. Train position must remain 10. Inspect **Layout details** tabs for all static properties and actual equipment locations.
8. Select command block **10** and toggle each failure, then all three, then clear them. Physical train remains; circuit/power reports Unknown occupancy. Check all captured flags. These controls are independent of the signal/crossing equipment controls.
9. On command block **3**, set temperature **32°F**, leave the field, refresh: heater ON. Inject Power: heater OFF. Clear Power and set **68°F**: heater OFF. Main dashboard temperature Apply provides the same operation.
10. On unoccupied command block **3**, Maintenance Closed → Open. Check captured maintenance and gray closed-block map. Optional rejection checks: try closing occupied block 10, or moving into closed block 3; verify rejection and unchanged physical occupancy, then restore the input selection.
11. Set clock multiplier **10**, Start, Pause. Time should advance around 10 simulation seconds per real second. While paused, **Step 10 sec** always advances exactly ten simulation seconds. Manual time uses HH:mm:ss. Time rewinds intentionally clear the rolling ticket ledger; cumulative station counts remain.
12. Enter negative/nonnumeric speed, authority, demand, passenger counts or invalid time. Verify a readable error and unchanged accepted outputs. Blocks without equipment have disabled equipment inputs and N/A/No signal outputs. Shrink the TestUI: a scrollbar keeps the lower panels accessible at 1180×720.

## Scope boundaries

The supplied construction archives and product brochures are reviewed by file in `PROJECT_INFORMATION_AUDIT.md`. They are contextual engineering references, not additional Iteration 2 grading rows or commands to implement an actual railway installation. No presentation content, Work Package 3 paperwork, Red/Green schedule dispatcher, PLC interpreter, train physics, real hardware protocol or full team integration was added. Track Model consumes block-level telemetry; it does not move a train autonomously or calculate onboard capacity. Beacon output is available at the transponder block, with passage timing handled by later train integration.
