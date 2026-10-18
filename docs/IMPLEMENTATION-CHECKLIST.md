# Implementation checklist and verification status

Levels: **1** implemented · **2** compiles against the installed `Game.dll` · **3** passes automated offline tests · **4** native members verified by `verify-api.ps1` · **5** verified in the running game · **6** verified through the full agent → mailbox → game workflow.

A level is claimed only with evidence. Levels 5–6 require the rebuilt DLL to be installed and a city loaded.

**Live run 1 (2026-10-17, Fort Johnson, pop 7,225, 0.6.0-dev):** all 17 read-only calls returned `ok`, through the PowerShell mailbox client (level 6 for transport). Content cross-checks:
- Per-building job capacity sums to 1,716, exactly matching `CountWorkplacesSystem`.
- HighSchool02 reports 400/400 students, matching the 0.5.0 diagnostics.
- Resident total of 7,580 vs population 7,225 is consistent with households still moving in.
- Zone names map correctly: NA Residential High has 614 cells, matching the raw `zone 21` count.
- The intersection at (4816, −4487) resolved all 24 connection lanes (0 unresolved), with 2 lanes per movement on Medium Roads and 1 on Small Roads, and detected the game's traffic lights.
- Traffic-mod adapter: types resolved and records read (no custom rules at that node). Road Builder 0.7.0 and Traffic 0.2.13 were detected.

**Bugs found live and fixed:** edge lane counts were doubled on long edges (the game splits each lane into pieces along the edge); `RoadData.SeparatedCarriageways` is not set on Medium Road Divided, so divided roads are now detected by carriageway groups. Both fixes still need live run 2.

**Live run 3 (traffic controls, authorised apply-and-undo):** every apply was refused at preview by an over-strict neighbourhood whitelist (the game regenerates neighbouring geometry). No change occurred, the junction's 16 movements were identical before and after, and a checkpoint had been saved. Fixed by enforcing the real property: nothing deleted, no new network, and no Upgraded change except on the target.

**Live run 6 (2026-10-18, Fort Johnson, paused, controls off):** every new call returned a result.
- Fixes confirmed: bottleneck icons resolve to `road`/`junction` with `via` lane. Lines read `Bus line 1-3` and stops read `127 Linden Street`, etc.
- `traffic_get_game_bottlenecks`: city flow 61%, volume 97 over 364 roads, and 7 marked bottleneck lanes, the worst being a Small Road at (4177, -4521) with 14% flow.
- `city_get_map_layer`: land value 128x128 at 112 m (peak 266 at 4872, -4648), with a bbox over the shop strip (mean 227). Ground water was non-zero. Air, noise and ground pollution were all zero because the NoPollution mod (pdx 78601) is installed. An invalid layer listed the valid names.
- `city_capture_view`: a 960x540 PNG (817 kB) showing the city without UI.
- `city_get_finances`: loan 0, creditworthiness 52,200,000, and 9 service fees. The third value pair is the collected internal/export/import income, not a slider range; the labels were fixed after this run and re-checked live on the next launch.
- `city_get_utilities`: electricity 333,148 produced / 314,422 consumed, water 102,596 / 65,183, garbage 343,920.
- `city_get_policies`: 6 city policies (1 active) with localized titles. The city has no districts, so the district path was only checked for its `district_not_found` error.
- `get_city_state.clock` returned 2027, day 3 of 12, 03:10. `ageStructure` returned 5,838 children, 1,871 teens, 19,871 adults and 2,581 seniors. `get_tiles` returned 529 owned tiles with resource features, upkeep disabled.

**Live run 5 (2026-10-18, Fort Johnson, population 30,076, paused, controls off):** all 14 calls returned the expected result.
- `city_get_notifications`: 10 icons (6 No Customers on low-density commercial, 3 Traffic Bottleneck, 1 Powerline Not Connected). The `customer` filter returned 6. Bottleneck icons belong to a lane entity, so their `target.kind` is `other`.
- `city_get_statistics`: 389 recorded series, 278 samples each. Population, Unemployed and PassengerCountBus histories read. ServiceCount[2] resolved to ConvenienceFood. An unknown type returned the list of valid names.
- `traffic_get_queues`: 687 driving and 173 stopped (138 Continuing, 14 Signal, 5 Crossing, 16 None), no deadlocks. Largest queue: 66 vehicles on a Medium Road at (4176, -4441). The area filter worked.
- `transit_get_lines`: the 3 bus lines matched the panel (6/6/10 stops; 5/6/12 vehicles). Per-stop waiting passengers were read and the tram filter returned 0. `name` is the prefab name (`Bus Line Tool`) for lines without a custom name.

**Live run 4 (fixed DLL, quiet local 4-way in the east district):**
- Previews predicted the flags correctly (`AllWayStop`, `TrafficLights`, right-side `ForbidLeftTurn`) with no city change.
- All-way stop applied: all 16 movements gained `Stop`. `default` removed it and restored all 16 exactly.
- Left-turn ban applied on the expected end. The game keeps the turning lane but flags it `Forbidden` (visible in `movements[].controls`). Allowing it restored the original state.
- Repeating a request returned `changed: false`.

**Live run 2 (same city, fixed DLL):** all 17 calls returned `ok`.
- Lane counts: Small Road 1/1, Medium Road 2/2, Medium Road Divided 2/2.
- Carriageway groups: 1 on undivided roads and 2 on Medium Road Divided. Classes are now local, collector and arterial respectively.
- Hierarchy gaps fell from 6 (inflated by the lane bug) to 1 real collector–highway junction.
- `compositionFlags` showed only `Edge,Pavement` on all three road types, so composition flags cannot detect medians in this version; carriageway groups can.

| Feature | 1 | 2 | 3 | 4 | 5 | 6 | Notes |
|---|---|---|---|---|---|---|---|
| Save-lock timeout (180 s) | ✔ | ✔ | — | — | ✗ | ✗ | Needs an in-game repro of a stuck save |
| `building_get_details` / `building_query` | ✔ | ✔ | logic ✔ | ✔ | ✔ run 1 | ✔ run 1 | Category/utilization logic unit-tested |
| `building_analyze_utilization` | ✔ | ✔ | logic ✔ | ✔ | ✔ run 1 | ✔ run 1 | |
| `city_get_zoning_summary` | ✔ | ✔ | — | ✔ | ✔ run 1 | ✔ run 1 | |
| `traffic_get_network` | ✔ | ✔ | logic ✔ | ✔ | ✔ run 2 | ✔ run 2 | Lane direction from `EdgeLane.m_EdgeDelta` must be checked in-game |
| `traffic_get_intersection` / `traffic_get_lane_rules` | ✔ | ✔ | — | ✔ | ✔ run 1 | ✔ run 1 | Edge resolution via `PathNode.GetOwnerIndex`; unresolved count reported. Confirm in-game that resolution succeeds |
| `traffic_find_bottlenecks` / `traffic_analyze` | ✔ | ✔ | logic ✔ | ✔ | ✔ run 2 | ✔ run 2 | |
| `city_analyze` + recommendation schema | ✔ | ✔ | ✔ | — | ✔ run 1 | ✔ run 1 | Rules tested against Fort Johnson figures measured live on 2026-10-17 |
| Traffic mod read adapter | ✔ | ✔ | — | types checked by reflection offline | ✔ run 1 | ✔ run 1 | |
| Road Builder list/get/validate | ✔ | ✔ | ✔ (all 51 installed configs parse) | — | list ✔ run 1 | list ✔ run 1 | |
| `traffic_set_intersection_control` / `traffic_set_turn_restriction` | ✔ | ✔ | — | ✔ (PlaceableNetData, Temp, leftHandTraffic) | ✔ run 4 | ✔ run 4 | Native upgrade tool; logic follows decompiled NetToolSystem.CreatePath/AddControlPoints |
| `city_get_notifications` / `city_get_statistics` | ✔ | ✔ | — | ✔ (Icon, CityStatisticsSystem API by decompile) | ✔ run 5 | ✔ run 5 | Adapted from LancerComet/cities-skylines-2-mcp (Apache-2.0) |
| `traffic_get_queues` | ✔ | ✔ | queue logic ✔ | ✔ (Blocker) | ✔ run 5 | ✔ run 5 | Queue heads and deadlock loops; same source |
| `transit_get_lines` | ✔ | ✔ | — | ✔ (TransportUIUtils.BuildTransportLine) | ✔ run 5 | ✔ run 5 | Same source |
| `traffic_get_game_bottlenecks` / `city_get_map_layer` / `city_capture_view` | ✔ | ✔ | — | ✔ (Bottleneck, CellMapSystem, ScreenCaptureHelper) | ✔ run 6 | ✔ run 6 | Pollution layers zero under the NoPollution mod |
| `city_get_finances` / `city_get_utilities` / `city_get_policies` | ✔ | ✔ | — | ✔ | ✔ run 6 | ✔ run 6 | Fee labels fixed after run 6 and re-checked live (collectedInternal matches estimatedIncome); district policies untested (no districts) |
| Clock, age structure, tile features | ✔ | ✔ | — | ✔ (TimeSystem, CountHouseholdDataSystem, MapFeatureElement) | ✔ run 6 | ✔ run 6 | |
| Registry consistency (80 commands) | ✔ | ✔ | ✔ | ✔ | — | — | |
| Install discovery (`find-game.ps1`) | ✔ | — | manual ✔ | — | — | — | Resolved `C:\Games\Cities - Skylines II` from `Player.log` |

## Blockers and deferred work (with evidence)

| Item | Status | Evidence / reason |
|---|---|---|
| Traffic mod rule writes (lane connections, priorities) | Blocked | The installed `Traffic.dll` exposes no public API (reflection: only `Traffic.Mod`, `ModSettings` and internal systems). Its rules are created through the mod's own tool, which writes `Temp`/definition entities processed by its systems. Writing its buffers directly would bypass its validation and serialization. A supported path would need an upstream API or a carefully driven instance of its tool. |
| Vanilla turn restrictions / traffic lights / all-way stop (write) | Done; verified live (run 4) | `CompositionFlags.Side.ForbidLeftTurn/ForbidRightTurn/ForbidStraight` and `General.TrafficLights/RemoveTrafficLights/AllWayStop` are applied through the native net tool's upgrade mode. The existing `BridgeRoadTool` would need an upgrade-flags path, plus preview validation. |
| Road Builder create/apply | Blocked | Road Builder generates prefabs from its own config objects (`RoadBuilderSystem`, `RoadBuilderSerializeSystem`). Writing new JSON files would only take effect on its next load and bypass its UI validation. There is no public apply API. |
| Rule capture/restore across road replacement | Blocked on the two items above | Entity IDs change on replacement, so restoration needs re-identification by position plus lane index. This can be designed once a write path exists. |
| CS2-Mod-Suite integration | Not applicable | Not installed in this environment. |
| `MailboxClientTests` | Environment | Requires PowerShell 7 (`pwsh`), which is not installed. All other suites pass. |
| Transit routes and ridership telemetry | Not started | Next after in-game verification of the current commands. |
| Plan/execute workflow extensions (`plan_validate`, …) | Not started | The existing `batch_execute`/`plan_neighborhood` pipeline already provides dry run, sequencing, cash reserve, checkpoints and cancellation. Extend it rather than duplicate it. |

## Next steps

1. Still unverified live: the save-lock timeout (needs a stuck save), `roadbuilder_get_config` and `roadbuilder_validate_config` in-game.
2. Implement vanilla upgrade-flag writes behind preview validation and explicit approval.
