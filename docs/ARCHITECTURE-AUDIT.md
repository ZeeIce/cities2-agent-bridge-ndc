# Architecture audit — bridge 0.5.0 (baseline for 0.6.0 work)

Audited 2026-10-17 against the installed game, not the documentation alone.

## Environment examined

| Item | Observed |
|---|---|
| Game | Cities: Skylines II 1.6.2f1 (767.21d1), Unity 2022.3.71f1 |
| Install | Non-Steam, `C:\Games\Cities - Skylines II` (from `Player.log` Mono path) |
| Bridge | 0.5.0 DLL in `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CitiesIIAgentBridge` |
| Mailbox | `%LOCALAPPDATA%\CitiesIIAgentBridge` (`session.json`, `requests\`, `responses\`) |
| Relevant mods | Traffic (krzychu124) `pdx_mods\80095_29`, Road Builder `87190_42`, C2VM Traffic Tool Essentials `125342_20`. CS2-Mod-Suite is **not installed**. |
| Road Builder data | 51 `RoadConfig` JSON files in `ModsData\RoadBuilder` |
| Toolchain | .NET SDK 8.0 / 9.0 (no 10.0), git, no `pwsh` (PowerShell 7) |

Baseline: `build.ps1` compiles cleanly against the installed `Game.dll` (2 obsolete-API warnings). The offline suite passes except `MailboxClientTests`, which needs `pwsh` — an environment gap, not a code fault.

## How the bridge works

```
agent ─ bridge.ps1 ─► requests\<guid>.json ─► Mailbox.Pump (game main thread, 250 ms tick, ≤4 req/tick)
                                              └► Mod.Dispatch(command,args) ─► JObject
agent ◄──────────── responses\<guid>.json ◄───┘   (atomic replace, never replayed)
```

- **Entry**: `Mod : IMod` registers a main-thread updater (`GameManager.RegisterUpdater`) and five derived native tools (`BridgeRoadTool`, `BridgeZoneTool`, `BridgeDistrictTool`, `BridgeObjectTool`, `BridgeBulldozeTool`) in `SystemUpdatePhase.ToolUpdate`. No Harmony, no Burst jobs, no new components.
- **Dispatch**: one `switch` in `Mod.Dispatch`; reads are synchronous on the main thread; mutations start a tool and return an operation ID polled through `get_operation`. `batch_execute` sequences mutations from `WorkflowTick`.
- **Synchronisation**: every non-status command pauses the simulation (`PauseAnalysis`) when controls are enabled, so ECS reads see a quiescent world. Reads complete job dependencies explicitly where the game exposes them.
- **Safety**: `AllowControl` option (reset on every city load), `STOP` latch file, per-request 60 s expiry, session/citySession matching, request-ID replay protection, cost ceilings (`maxCost` ≤ 1,000,000), demolition preview checks, preview validation (`BuildingPreviewSafety`).
- **Serialization**: Newtonsoft `JObject`, indented, written atomically. Requests ≤ 16 KiB.
- **Raw-field reflection**: `ServiceDetails.RawFields` serialises an explicit allowlist of component types; no user-supplied type names.

## Capability inventory (52 commands)

| Area | Commands | Notes |
|---|---|---|
| Status | `ping`, `get_capabilities`, `get_city_state`, `get_city_management`, `get_city_diagnostics`, `pause_for_analysis` | Diagnostics include demand factors, happiness factors, workplaces, utilities totals, shortages, efficiency penalties |
| Buildings | `get_buildings`, `diagnose_connections`, `inspect_entity`, `get_selected`, `get_water_facilities`, `get_services` | Connection/shortage issues; raw allowlisted components. **No households, residents, employees, level, condition, district, upkeep** |
| Network | `get_network`, `get_network_edges`, `trace_network` | Edge curves + raw `Road` flow counters. **No lanes, no intersections, no flow ratio, no road class** |
| Zoning/terrain | `get_zone_cells`, `sample_terrain`, `get_tiles`, `get_city_map` | Raw zone indices without zone names |
| Districts | `get_district_atlas`, `create_district`, `edit_district`, `set_service_districts` | Snapshot pagination |
| Construction | `build_road`, `build_network`, `upgrade_network`, `zone_rectangle`, `clear_zoning`, `place_building`, `preview_building`, `relocate_building`, `demolish`, `purchase_tiles`, `find_building_sites` | Native preview/apply through derived tools |
| Economy | `set_tax`, `set_service_budget` | Range-checked |
| Workflow | `batch_execute`, `get_batch`, `cancel_batch`, `plan_neighborhood`, `execute_neighborhood`, `get_neighborhood_plan`, `save_checkpoint`, `get_operation` | Sequential, non-transactional |
| Simulation | `simulate_step`, `get_simulation_step`, `cancel_simulation_step`, `set_simulation_speed`, `get_camera`, `set_camera` | Bounded intervals with stop conditions |

## Weaknesses found (with evidence)

1. **Save lock can wedge indefinitely.** `saveTask` is never timed out and `OnPreload` does not clear it. Observed live on 2026-10-17: a save queued at 01:09 UTC before a city reload kept every later `save_checkpoint` and `execute_neighborhood` failing with `save_in_progress` for 15+ minutes.
2. **No building utilization data.** Agents cannot see households, residents, employees vs. capacity, building level/condition, abandonment, district, rent or upkeep. Diagnosing the 62 % unemployment observed in Fort Johnson required inference from city totals only.
3. **No traffic semantics.** `get_network_edges` returns raw `TrafficFlowDuration/Distance` counters but not the game's own ratio (`NetUtils.GetTrafficFlowSpeed`), lane counts, directions, intersection connections, signals or road class. Road hierarchy had to be inferred from prefab names.
4. **Zone indices are opaque.** `get_zone_cells` reports `zone: 46` with no mapping to `NA Residential Low`.
5. **Capability flags are static.** `trafficLaneRules`, `roadBuilderConfiguration` are hard-coded `false`; assembly presence is reported but never probed for the data types the bridge would need.
6. **Response format overhead.** Indented JSON roughly doubles response size for large pages.
7. **Client allowlist duplication.** Commands are listed in `Mod.Dispatch`, `get_capabilities`, `bridge.ps1` `ValidateSet` and `commands.json`; they drift independently.
8. **Test target pinned to .NET 10.** Fixed in this branch (`-p:BridgeTestFramework=net9.0`).
9. **Obsolete updater API** (`GameManager.RegisterUpdater`) — warning only; left unchanged to avoid altering the proven tick path without runtime verification.

## Third-party integration findings

| Mod | Public API | Data model (verified by reflection on installed DLL) | Feasible now |
|---|---|---|---|
| Traffic (krzychu124) | None | Node buffers `ModifiedLaneConnections`, `GeneratedConnection`, `LanePriority`; tags `ModifiedConnections`, `ModifiedPriorities`, `ModifiedIntersection`; edge `TrafficUpgrade` | **Read-only** adapter via runtime type lookup. Writing requires driving the mod's own tool/definition pipeline — not attempted (blocker recorded). |
| Road Builder | None (web `RoadBuilderEntry` only) | Prefab tag `RoadBuilderPrefabData`, edge tag `RoadBuilderNetwork`; configs are JSON `RoadConfig` files | **Read and validate** configs from disk; count edges built with each config. Creation/apply deferred (needs Road Builder's generation pipeline). |
| Vanilla | — | `CompositionFlags.Side.ForbidLeftTurn/ForbidRightTurn/ForbidStraight`, `General.TrafficLights/RemoveTrafficLights/AllWayStop` via `Upgraded` | Read now; write via the native upgrade tool is a candidate for a later phase. |

## Roadmap (priority order, status tracked in `IMPLEMENTATION-CHECKLIST.md`)

| P | Work | Approach |
|---|---|---|
| 1 | Audit | This document |
| 2 | Building telemetry + diagnostics | `building_get_details`, `building_query`, `building_analyze_utilization`, `city_get_zoning_summary`; save-lock timeout |
| 3 | Traffic telemetry | `traffic_get_network`, `traffic_get_intersection`, `traffic_find_bottlenecks`, `traffic_analyze` using `NetUtils.GetTrafficFlowSpeed`, lane buffers and node connection lanes |
| 4 | Traffic rules | Read-only `traffic_get_lane_rules` (vanilla + Traffic adapter); writes blocked pending a supported path |
| 5 | Road Builder inspection | `roadbuilder_list_configs`, `roadbuilder_get_config`, `roadbuilder_validate_config` |
| 6 | Road Builder apply + rule restoration | Blocked; see checklist |
| 7 | City analysis | `city_analyze` producing schema-checked recommendations |
| 8 | Execution workflows | Existing batch/plan pipeline retained; recommendations carry `requiresApproval` |
| 9 | Performance, tests, docs | Pure-logic modules unit-tested offline; docs updated |
