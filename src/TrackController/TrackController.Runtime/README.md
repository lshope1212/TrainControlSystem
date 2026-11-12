# TrackController.Runtime

The track controller separates three decisions:

| File in `TrackController.Wpf/Samples` | Responsibility |
| --- | --- |
| `track-map.json` | One physical track map: block lengths, connections, equipment, protection zones, and drawing coordinates. |
| `territories.json` | Named territories and the block IDs each owns. |
| `all-pc.deployment.json` or `pc-and-pi.deployment.json` | References those two files and the default PLC, with optional PLC overrides and PC/Pi execution per territory. |

Territory names do not imply hardware or software. The same territory can run on either target, using the same logic. Changing execution does not move blocks between owners or edit connections. The unprefixed examples use the illustrative 12-block Green Line map, not the team's final track data.

The `blue-line-*` files are the same three decisions for the course Blue Line prototype (15 blocks, switch 5, crossing 3, signals 6 and 11): `blue-line-track-map.json`, `blue-line-project-territories.json` (two local territories) or `blue-line-territories.json` (the two-territory PC/Pi example), and `blue-line-all-pc.deployment.json` or `blue-line-pc-and-pi.deployment.json`. See the [operator README](../TrackController.Wpf/README.md#blue-line-prototype) for the layout and alternative territory assignments. They share `wayside.plc` unchanged.

## Change PC/Pi placement in the UI

1. Enable **Maintenance mode** in **TrackController.Wpf**.
2. Open **Controller setup → Load layout**. The application starts the 16-wayside course network on the PC, without needing a Pi. For a saved plan, choose **Saved setup file…** in the selector, then **Load layout**.
3. Filter the **Wayside territories** list by line and select a territory. Choose **PC** or **Pi** under **Run this territory on**. Pi address and port fields appear only for Pi assignments. Block counts stay in the list; the complete block list is under **Advanced → Owned blocks**.
4. For a Pi, enter its IP address and runtime port, normally `192.168.50.2:11405`; see [Pi setup](../TrackController.Pi/README.md). Each additional territory on the same Pi needs a separate runtime port. Two territories cannot share one runtime process.
5. Select **Apply changes**. This validates the choices, requires maintenance mode, and replaces the deployment. Feedback and movement requests are cleared; the current valid PLC source is retained. It retains maintenance mode, and a missing Pi stays unavailable.
6. Send fresh feedback, then leave maintenance mode when ready. Test UI requests still enter through the normal module message interface.
7. Optionally select **Save setup**. This writes execution placement and PLC assignments to the loaded deployment JSON. Assigned programs are saved as content-addressed `.plc` copies beside that JSON; original PLC files, the map, and ownership remain unchanged. **Apply** by itself does not save to disk.

**Currently** shows the applied target for the selected territory; **Run this territory on** edits a draft until **Apply changes**. Edited territories remain marked in the list when selecting another territory. Changes outside maintenance mode are refused. An invalid assignment leaves the existing deployment in place. The save operation detects an externally edited deployment file and asks you to reload it rather than overwriting that edit.

The default file dialog opens the application's copied `Samples` directory. To keep changes in your repository, load the deployment from the source `src/TrackController/TrackController.Wpf/Samples` folder before saving. No rebuild is needed to load edited JSON files directly.

## Change territory boundaries

Edit `territories.json`, move block IDs between lists, then enter maintenance mode and reload the deployment. For example:

```json
{
  "territories": [
    { "id": "wayside-a", "name": "West approach", "blockIds": ["1", "2", "3"] },
    { "id": "wayside-b", "name": "Junction and crossing", "blockIds": ["4", "5", "6", "7", "8", "9", "10", "11", "12"] }
  ]
}
```

Every map block must have exactly one owner. Missing, duplicate, and unknown block IDs are rejected. A switch or crossing and its complete protection zone must stay in the same territory. Each territory needs one execution assignment, with a matching `territoryId`. This version supports 1–16 territories per deployment, using the existing 500-block map limit per line.

## Several lines in one deployment

`course-territories-all-pc.deployment.json` runs the Blue, Red and Green course lines in one controller process: two Blue waysides, six Red waysides, and eight Green waysides. Instead of `trackFile`/`territoriesFile` it lists `lines`, each with its own shared map and ownership file:

```json
"lines": [
  { "trackFile": "blue-line-track-map.json", "territoriesFile": "blue-line-project-territories.json" },
  { "trackFile": "red-line-track-map.json", "territoriesFile": "red-line-project-territories.json" },
  { "trackFile": "green-line-track-map.json", "territoriesFile": "green-line-project-territories.json" }
]
```

Each line's rules are the single-line rules above. In addition, a line may appear once, territory IDs are unique across the deployment, and block IDs are unique across every line (the Track Model's `1`–`15`, `RED-n`, `GREEN-n`), because module messages identify a block without its line. A territory owns blocks of one line. The router delivers every message to the owner of its block, so CTC never needs to know which territory owns what. The Red and Green maps are derived from the Track Model layout (see `tests/Integration.Tests/CourseTrackMaps.cs`, which also checks they stay identical): Green's exits follow its one-way sections, and Red runs both ways. Red and Green have no signals, because the workbook lists none.

Do not cut physical edges to make a territory load. The sample retains the real `3 → 4` connection in the shared map even though those blocks have different owners. Ownership affects permission, not topology. Keep boundaries on simpler track stretches and choose final boundaries with the team; disconnected pieces in a territory are not automatically split into additional controllers.

## Neighbor observations and command ownership

For each territory, the runtime derives adjacent foreign blocks from the shared map's incoming and outgoing edges. The router forwards those blocks' feedback to the owner and adjacent observers. This applies to feedback batches and the existing single-block feedback messages.

Only the owner can issue movement, maintenance, switch, or manual commands for a block. The controller core enforces this independently of the router, including when running on the Pi. A territory's snapshot publishes commanded block statuses only for owned blocks. Foreign feedback appears separately in `NeighborObservations`, which contains no commanded equipment state or authority. The combined module snapshot has exactly one commanded status per map block.

The controller-owned `TrackController.Contracts.Messages.WaysideConfigurationMessage.OwnedBlockIds` is explicit for shared-map deployments. Null retains the legacy meaning: every supplied block is owned. Each runtime receives the map so its physical topology remains intact, and, in coordinated mode, accepts measured feedback for the full line to validate shared routes. Observations retain the same freshness checks as owned feedback.

## Project territory split and cross-wayside movement

`course-territories-all-pc.deployment.json` is the operator application's default.
It uses 16 controllers. The individual Blue, Red and Green presets use these same ownership files:

| Territory | Owned blocks | Reason for boundary |
| --- | --- | --- |
| BL-A · Yard & junction | 1–6, 11 | Keeps the overlapping switch/crossing zones and both branch signals together. |
| BL-B · Station B & Station C | 7–10, 12–15 | Shares one controller across both short terminal branches; handoffs at 6→7 and 11→12. |
| RL-A · Shadyside & Herron | RED-1–17 | Northern loop, switch 16 and crossing 11. |
| RL-B · Swissville & Penn | RED-18–28, RED-75–76 | Penn junction and its branch approach. |
| RL-C · Steel Plaza | RED-29–36, RED-72–74 | Steel Plaza and switch 33 with its branch. |
| RL-D · First Ave junctions | RED-37–45, RED-67–71 | Both ends of the city bypass, switches 38 and 44. |
| RL-E · Station Square junction | RED-46–53, RED-66 | Crossing 47 and the southern loop's switch 52. |
| RL-F · South Hills loop | RED-54–65 | Outer loop and South Hills Junction station. |
| GL-A · Pioneer & Edgebrook | GREEN-1–20 | Northern loop, switch 13, Edgebrook and crossing 19. |
| GL-B · Whited & South Bank junction | GREEN-21–35, GREEN-138–150 | Both approaches meet at switch 28. |
| GL-C · Central & Inglewood outbound | GREEN-36–53 | Outbound corridor through Central and Inglewood. |
| GL-D · Yard to Dormont | GREEN-54–75 | Connected outbound corridor through the yard entry, Overbrook, Glenbury and Dormont. |
| GL-E · Glenbury to Central return | GREEN-110–137 | Return corridor through Glenbury, Overbrook and Inglewood toward Central. |
| GL-F · Mt Lebanon junctions | GREEN-76–86, GREEN-100–101 | Both southern junctions and their complete switch zones. |
| GL-G · Poplar & Castle Shannon | GREEN-87–99 | Southern outer loop and its stations. |
| GL-H · Dormont return & crossing | GREEN-102–109 | Connected return corridor through Dormont and crossing 108. |

These project defaults divide Red into six controllers and Green into eight, while the short Blue line retains two. Each Red and Green territory forms a connected stretch of track. Branches can have nonconsecutive block IDs where they join at a switch. Every block has one owner and every switch/crossing protection zone stays intact. Each territory can run on PC or a separate Pi endpoint; a missing Pi has no automatic local substitute. Single-line presets use exactly the same ownership as the combined 16-controller network. Schematic coordinates affect presentation only; connections, direction, lengths and safety inputs are unchanged.

For a split line, `PreparedDeployment` enables private `CoordinatedRoutes` mode.
The router forwards the same route, staged successor, cancellations and measured
line feedback to each controller on that line. Each runs its own PLC and
independent safety checks, including its locally owned maintenance/interlocks.
Shadow evaluation of foreign blocks does not publish foreign equipment commands:
`Blocks` remains owner-only, `NeighborObservations` is measurement-only, and
private `RouteAssessments` are consumed exclusively by the router.

The router requires current assessments from every controller on the line,
publishes the most restrictive common speed/authority, and holds that issued
block-entry limit for unchanged requests. A reduction becomes zero, never a
shortened nonzero value mid-block; improvement cannot extend it. CTC's existing
replacement, explicit limit change and station departure semantics remain intact.
An individual runtime assessment is not permission to operate a train on its own.

A staged continuation is published only with joint approval, capped by the
predecessor's actual published grant minus the current block's full length.
Promotion requires an observed clear-to-occupied transition while that joint
continuation was valid, in addition to the cores' existing ambiguity, route,
freshness and overlap checks. A cancelled, expired or unavailable participant
withholds the combined route and its continuation. A rejected partial delivery
is cancelled at every participant. The router serializes inputs and polls all
participants before publishing a completed transaction; a Pi lease cannot be
extended by transport delay. Cross-line routes remain invalid.

A poll asks every territory once (Pi territories first, then the local ones, so
a slow Pi cannot consume a local lease). A controller whose inputs, carried-over
state and time thresholds have not changed since its last evaluation answers a
poll with that evaluation re-stamped instead of repeating it (see
`TrackControllerService.EvaluationReuse.cs`), so polling territories that saw
no change costs almost nothing. Local territories receive feedback batches as
measurements, without another JSON round trip; a Pi still receives an envelope.
Set `TRACKCONTROLLER_PERF=1` to have the operator host print evaluation, poll,
lock-wait and message-handling statistics every 10 s.
## External test inputs and resets

Input source is independent of placement. The separate `TrackController.TestUI.Wpf`
application sends to the same controller pipes used by CTC and Track Model, with
either all-PC or mixed PC/Pi execution. It does not create a second deployment.

Stop repeated test inputs before **Reset all controllers** in the operator window.
Reset clears feedback and requests, retains applied placement and the current
valid PLC, and retains maintenance mode across new sessions. A lost Pi never transfers its blocks to
a local controller. Send fresh feedback and leave maintenance mode when ready.

## Compatibility and verification

Older deployments that reference `configurationFile` still load. `legacy-pc-and-pi.deployment.json` preserves the previous demonstration; use the new shared-map examples for further development.

The Pi protocol is version 11 to carry automatic-operation and maintenance initialization as well as coordinated route assessments alongside owner-only block snapshots, alongside shared movement requests and feedback contracts: authority is a fixed route limit from the target block's entry, with no train offset input, and signal feedback accepts Super Green. Recopy the updated Pi package before connecting an older device installation. A version mismatch is rejected so the two ends cannot interpret authority differently.

Automated checks cover intact boundary edges, exclusive command ownership, invalid partitions and split protection zones, feedback fan-out, direct Pi ownership enforcement, equivalent local/Pi decisions, explicit placement saving, and the operator execution editor receiving external Test UI inputs through module pipes. Cross-territory tests cover local/Pi handoff, remote closure, lost Pi, overlapping requests and rejected promotion after revocation. The full-module integration test checks that no false zero reaches the onboard train at a wayside boundary. Physical Pi/LED verification remains a hardware test.

## Per-territory PLC assignment

Enter maintenance mode, then open Controller setup and select a **territory** in the wayside list. The source viewer shows that assignment. Under Advanced, choose **Replace selected PLC…**. The replacement is validated before sending and targets only that territory on PC or Pi; inspect the reported PLC status before leaving maintenance mode. Reset and Apply changes retain the individual assignments. **Save setup** saves portable PLC copies and assignment references next to the loaded deployment file.

The deployment-level `plcFile` remains the default. Each entry in `territories` may override it with its own relative `plcFile`. Every referenced PLC is validated before runtimes are created. Paths must remain inside the deployment folder. Existing deployments need no changes. A PLC message with `territoryId` targets that owner; omitting it retains the legacy broadcast behavior.

## Vital Boolean PLC control

The assigned PLC now owns route interlocking, numeric speed/authority selection using explicit Boolean gates, signals, switch interlocking, crossings, and closure completion. C# supplies individual facts and encoded input words, decodes the PLC output words, and independently vetoes unsafe results. It no longer supplies precomputed route permission or normal numeric movement limits to the PLC. See the [language and execution contract](../TrackController.Core/Control/README.md).

Use the updated `Samples/wayside.plc`, beginning with `plc boolean_v1`. Older permission-only programs are rejected. Rebuild/redeploy the Pi package for protocol version 11. Source and programs are the same on PC/Pi; territory assignment selects the file used by each controller.

## Production lifecycle and simulation ownership

CTC owns the system clock, speed multiplier and simulation pause. The Track Controller operator UI has no Hold/Enable movement or local time controls. Production PC/Pi sessions use `AutomaticOperation`: valid PLCs evaluate immediately, but authority still requires fresh safe feedback and a current CTC request. Maintenance mode withholds automatic movement and is required for layout/placement changes, PLC replacement and resets. Exiting maintenance returns to automatic evaluation. Pi reconnection retains the requested maintenance state, clears old requests/feedback and requires fresh inputs. An unavailable or invalid controller cannot grant authority.

The `SetSimulation` interface and snapshot time fields remain only for compatibility with isolated legacy test harnesses; production sessions reject that command and do not advance a local simulation clock. Pi protocol 11 carries the production lifecycle and maintenance initialization, so rebuild/copy/restart older Pi binaries.