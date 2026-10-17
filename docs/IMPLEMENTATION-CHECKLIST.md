# Implementation checklist and verification status

Levels: **1** implemented · **2** compiles against the installed `Game.dll` · **3** passes automated offline tests · **4** native members verified by `verify-api.ps1` · **5** verified in the running game · **6** verified through the full agent → mailbox → game workflow.

A level is claimed only with evidence. Levels 5–6 require the rebuilt DLL to be installed and a city loaded. **As of 2026-10-17 the game was not running, so nothing below is verified at levels 5–6.**

| Feature | 1 | 2 | 3 | 4 | 5 | 6 | Notes |
|---|---|---|---|---|---|---|---|
| Save-lock timeout (180 s) | ✔ | ✔ | — | — | ✗ | ✗ | Needs an in-game repro of a stuck save |
| `building_get_details` / `building_query` | ✔ | ✔ | logic ✔ | ✔ | ✗ | ✗ | Category/utilization logic unit-tested |
| `building_analyze_utilization` | ✔ | ✔ | logic ✔ | ✔ | ✗ | ✗ | |
| `city_get_zoning_summary` | ✔ | ✔ | — | ✔ | ✗ | ✗ | |
| `traffic_get_network` | ✔ | ✔ | logic ✔ | ✔ | ✗ | ✗ | Lane direction from `EdgeLane.m_EdgeDelta` must be checked in-game |
| `traffic_get_intersection` / `traffic_get_lane_rules` | ✔ | ✔ | — | ✔ | ✗ | ✗ | Edge resolution via `PathNode.GetOwnerIndex`; unresolved count reported. Confirm in-game that resolution succeeds |
| `traffic_find_bottlenecks` / `traffic_analyze` | ✔ | ✔ | logic ✔ | ✔ | ✗ | ✗ | |
| `city_analyze` + recommendation schema | ✔ | ✔ | ✔ | — | ✗ | ✗ | Rules tested against Fort Johnson figures measured live on 2026-10-17 |
| Traffic mod read adapter | ✔ | ✔ | — | types checked by reflection offline | ✗ | ✗ | |
| Road Builder list/get/validate | ✔ | ✔ | ✔ (all 51 installed configs parse) | — | ✗ | ✗ | |
| Registry consistency (66 commands) | ✔ | ✔ | ✔ | ✔ | — | — | |
| Install discovery (`find-game.ps1`) | ✔ | — | manual ✔ | — | — | — | Resolved `C:\Games\Cities - Skylines II` from `Player.log` |

## Blockers and deferred work (with evidence)

| Item | Status | Evidence / reason |
|---|---|---|
| Traffic mod rule writes (lane connections, priorities) | Blocked | The installed `Traffic.dll` exposes no public API (reflection: only `Traffic.Mod`, `ModSettings` and internal systems). Its rules are created through the mod's own tool, which writes `Temp`/definition entities processed by its systems. Writing its buffers directly would bypass its validation and serialization. A supported path would need an upstream API or a carefully driven instance of its tool. |
| Vanilla turn restrictions / traffic lights / all-way stop (write) | Feasible, not started | `CompositionFlags.Side.ForbidLeftTurn/ForbidRightTurn/ForbidStraight` and `General.TrafficLights/RemoveTrafficLights/AllWayStop` are applied through the native net tool's upgrade mode. The existing `BridgeRoadTool` would need an upgrade-flags path, plus preview validation. |
| Road Builder create/apply | Blocked | Road Builder generates prefabs from its own config objects (`RoadBuilderSystem`, `RoadBuilderSerializeSystem`). Writing new JSON files would only take effect on its next load and bypass its UI validation. There is no public apply API. |
| Rule capture/restore across road replacement | Blocked on the two items above | Entity IDs change on replacement, so restoration needs re-identification by position plus lane index. This can be designed once a write path exists. |
| CS2-Mod-Suite integration | Not applicable | Not installed in this environment. |
| `MailboxClientTests` | Environment | Requires PowerShell 7 (`pwsh`), which is not installed. All other suites pass. |
| Transit routes and ridership telemetry | Not started | Next after in-game verification of the current commands. |
| Plan/execute workflow extensions (`plan_validate`, …) | Not started | The existing `batch_execute`/`plan_neighborhood` pipeline already provides dry run, sequencing, cash reserve, checkpoints and cancellation. Extend it rather than duplicate it. |

## Next steps

1. Install the 0.6.0-dev DLL (backing up 0.5.0) and load a disposable or backed-up city.
2. Run each new command and record outputs as evidence (levels 5–6). In particular, check lane directions, intersection edge resolution and that zone names match the in-game zoning.
3. Then implement vanilla upgrade-flag writes behind preview validation and explicit approval.
