# October 8 TestUI interface corrections

Implemented Derrick's six requested corrections on local branch `derrick-track-model-review`. Derrick subsequently requested larger fonts and publication of the accumulated changes to the GitHub `Derrick` branch.

## Final interface

| Requested change | Result |
| --- | --- |
| Remove temperature from Failure Injection and the combined output panel | Removed the input, output and view-model send logic. |
| Remove track heater output | Removed from TestUI's main window and captured-layout equipment table. |
| Remove Traffic light | Removed the display, captured signal model and Train Controller receiver. |
| Remove maintenance input/output | Removed controls, display properties and TestUI maintenance-message generation. |
| Remove switch position, crossing gates, signal states and maintenance from To Track Controller | That panel now contains only occupancy, broken rail, track circuit failure and power failure. |
| Send nothing to Train Controller; only ticket sales to CTC | Production outbound batches no longer connect to Train Controller. The CTC batch contains only `TicketSalesMessage`. |

The combined panel is now **To CTC**, containing only tickets in the preceding simulation hour. Boarded/disembarked and cumulative station ticket totals are under **To Train Model**, from whose environment message those values originate. Track signal remains under To Train Model as the requested software input to that module.

Layout and accepted clock were previously routed through CTC to initialize TestUI. They now use the private **TestUI setup / feedback** endpoint, so TestUI still initializes its block selectors and clock without adding non-ticket traffic to CTC.

Shared message schemas retain their existing optional equipment, closure, temperature and heater fields for teammate compatibility. The removed fields are absent from the requested TestUI panels; they may still appear in the raw contract log. Main-dashboard environmental simulation and Core maintenance support remain outside this TestUI change.

## Why heaters and maintenance were originally added

The heater was an environmental simulation extension during the broader review: an installed rail heater switched on at or below 32°F when powered, to demonstrate cold-weather behavior. The Blue Line workbook has no heater or weather columns, and that threshold was a simulation assumption, not a workbook requirement.

Maintenance was added to exercise closing/opening a block and rejecting unsafe closure or entry. It expanded the demonstration beyond the interface Derrick now specifies. Both additions have been removed from TestUI as requested.

## Verification

- Solution build succeeded with zero errors and one existing unrelated CTC nullable warning (CS8600, `CTCService.cs:96`).
- All **97 tests passed**: 74 Core/common/module tests and 23 WPF workflow/rendering tests; no failures or skips.
- All **16 real-process transport checks passed**. A listener monitored Train Controller throughout the run and received no messages. Every CTC message received was `TicketSalesMessage`; layout and accepted time arrived at the private TestUI endpoint.

Native Windows checks were performed against the rebuilt dashboard and TestUI, using the actual controls and inspecting their outputs:

| Input/action | Observed output |
| --- | --- |
| Inspect main TestUI window | No temperature/heater/Traffic light/maintenance controls or outputs. To Track Controller has the four requested fields. To CTC has only hourly ticket sales. |
| Block 1: commanded speed 20 mph, authority 600 ft, actual speed 10 mph | To Train Model independently displayed 20 mph, 600 ft and 10 mph. |
| Inject power failure on occupied block 1 | To Track Controller changed to UNKNOWN occupancy and FAILURE power. Train 01 remained present with its telemetry. Clearing power restored OCCUPIED and NORMAL. |
| Stop train 01 and select block 10; set Station B waiting population to 7 | To Train Model displayed train 01 and 7 waiting. |
| Board 3, disembark 2, Apply passenger exchange once | To Train Model displayed 4 waiting, 3 / 2 exchanged and 3 cumulative station tickets. To CTC displayed 3 tickets/hour (Blue). |
| Refresh outputs after exchange | Waiting population, passenger totals and hourly ticket count remained unchanged. |
| Open captured layout physical and equipment tabs | Fifteen Blue Line blocks rendered correctly; equipment tab opened without a heater column or a crash. |

After inspection, the dashboard and TestUI were restarted with the clean default profile for Derrick's manual testing.

## Larger-font follow-up

TestUI body text increased from 16 to 20, buttons from 14 to 18, and section headings from 19 to 23. Input/output rows and table rows grew to fit. Main, captured-layout and message-log windows use larger initial sizes; layout columns size to their content with extra space for direction and beacon values. Passenger action buttons and failure controls wrap in narrower windows; vertical scrolling remains available.

The rebuilt TestUI passed compilation with no warnings/errors. All 23 existing WPF workflow/rendering tests passed again after the typography changes. Native inspection checked larger labels, input controls, output values, layout tables and scrolling.
