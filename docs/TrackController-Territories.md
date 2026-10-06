# Track map, territory ownership, and PC/Pi execution

The track controller separates three decisions:

| File in `TrackController.Wpf/Samples` | Responsibility |
| --- | --- |
| `track-map.json` | One physical track map: block lengths, connections, equipment, protection zones, and drawing coordinates. |
| `territories.json` | Named territories and the block IDs each owns. |
| `all-pc.deployment.json` or `pc-and-pi.deployment.json` | References those two files and the shared PLC, then chooses PC/Pi execution per territory. |

Territory names do not imply hardware or software. The same territory can run on either target, using the same logic. Changing execution does not move blocks between owners or edit connections. The examples use the illustrative 12-block map, not the team's final track data.

## Change PC/Pi placement in the UI

1. Select **Test UI** or **Live feedback** and pause that mode.
2. Open **Controller setup → Load sample layout**. It starts two named territories on the PC, without needing a Pi. Use **Load deployment…** for a saved plan instead.
3. Under **Execution setup**, inspect **Owned blocks** and **Observe only**. Choose **PC** or **Pi** in **Run on** for each territory.
4. For a Pi, use its local SSH tunnel endpoint. The default junction endpoint is `127.0.0.1:11405`; see [Pi setup](TrackController-Pi5.md). Each additional Pi territory needs a separate runtime/tunnel port. Two territories cannot share one runtime process.
5. Select **Apply execution**. This validates the choices, verifies that all selected-mode controllers are paused, and replaces the deployment. Feedback and movement requests are cleared; the current valid PLC source is retained. It restarts paused, and a missing Pi stays unavailable.
6. Send fresh feedback and explicitly resume. Test UI requests still enter through the normal module message interface.
7. Optionally select **Save applied choices**. This writes only execution placement to the deployment JSON that was loaded. The shared map, ownership file, and PLC file are unchanged. **Apply** by itself does not save to disk.

**Applied** shows the execution target currently in use; **Run on** is an editable draft until Apply. Changes while running are refused. An invalid assignment leaves the existing deployment in place. The save operation detects an externally edited deployment file and asks you to reload it rather than overwriting that edit.

The default file dialog opens the application's copied `Samples` directory. To keep changes in your repository, load the deployment from the source `src/TrackController/TrackController.Wpf/Samples` folder before saving. No rebuild is needed to load edited JSON files directly.

## Change territory boundaries

Edit `territories.json`, move block IDs between lists, then pause and reload the deployment. For example:

```json
{
  "territories": [
    { "id": "wayside-a", "name": "West approach", "blockIds": ["1", "2", "3"] },
    { "id": "wayside-b", "name": "Junction and crossing", "blockIds": ["4", "5", "6", "7", "8", "9", "10", "11", "12"] }
  ]
}
```

Every map block must have exactly one owner. Missing, duplicate, and unknown block IDs are rejected. A switch or crossing and its complete protection zone must stay in the same territory. Each territory needs one execution assignment, with a matching `territoryId`. This version supports 1–16 territories on one line, using the existing 500-block map limit.

Do not cut physical edges to make a territory load. The sample retains the real `3 → 4` connection in the shared map even though those blocks have different owners. Ownership affects permission, not topology. Keep boundaries on simpler track stretches and choose final boundaries with the team; disconnected pieces in a territory are not automatically split into additional controllers.

## Neighbor observations and command ownership

For each territory, the runtime derives adjacent foreign blocks from the shared map's incoming and outgoing edges. The router forwards those blocks' feedback to the owner and adjacent observers. This applies to feedback batches and the existing single-block feedback messages.

Only the owner can issue movement, maintenance, switch, or manual commands for a block. The controller core enforces this independently of the router, including when running on the Pi. A territory's snapshot publishes commanded block statuses only for owned blocks. Foreign feedback appears separately in `NeighborObservations`, which contains no commanded equipment state or authority. The combined module snapshot has exactly one commanded status per map block.

The controller-owned `TrackController.Contracts.Messages.WaysideConfigurationMessage.OwnedBlockIds` is explicit for shared-map deployments. Null retains the legacy meaning: every supplied block is owned. Each runtime receives the map so its physical topology remains intact, but accepts feedback only for owned blocks and directly observed neighbors. Observations retain the same freshness checks as owned feedback.

Neighbor observations currently provide context; they do not grant cross-boundary movement. Requests whose target and future path span owners are rejected. A previous entry block may be an observed neighbor if the actual map connects it to the target, but all newly authorized distance remains within the target's territory. Continuous inter-territory movement still needs a coordinated handoff protocol. Do not interpret a physical edge or a clear neighboring block as permission to cross it.

## Test/live independence and resets

Input source is separate from placement. You can use Test UI or live module inputs with all-PC or mixed PC/Pi execution. Each mode retains its own applied deployment and execution drafts; switching modes pauses the outgoing controllers. A single physical Pi runtime still permits only one owning session, so give up that endpoint in one mode before assigning it to the other.

**Reset test** clears test inputs and requests, retains the applied execution and current valid PLC, and creates paused sessions. It discards unapplied execution drafts and leaves the live deployment alone. If a PLC replacement was invalid, load a valid program before resetting or changing execution. A lost Pi connection never transfers its blocks to a local controller.

## Compatibility and verification

Older deployments that reference `configurationFile` still load. `legacy-pc-and-pi.deployment.json` preserves the previous demonstration; use the new shared-map examples for further development.

The Pi protocol is now version 2 because configuration distinguishes topology from ownership and snapshots include read-only neighbor observations. Recopy the updated Pi package before connecting an older device installation. A version mismatch is rejected rather than running an older controller that could misunderstand ownership.

Automated checks cover intact boundary edges, exclusive command ownership, invalid partitions and split protection zones, feedback fan-out, direct Pi ownership enforcement, equivalent local/Pi decisions, explicit placement saving, and the actual WPF Test UI changing execution through the module pipes. Device-level Pi/LED verification and cross-territory handoff remain separate integration work.
