# Telemetry, analysis and integration commands (0.6.0-dev)

All commands below are **read-only**. They pause analysis like other reads (unless noted), return `citySession`, and never modify the city. Entity IDs are `{index, version}` pairs valid only for the current `citySession`.

Common conventions:

- **Area filters**: either `x`, `z`, `radius` (metres) or `bbox: {minX, minZ, maxX, maxZ}`.
- **Pagination**: `offset`, `limit`; responses carry `total`, `truncated`, `nextOffset` (null at the end). Follow `nextOffset` while the city stays paused.
- **Measurement labels**: fields ending in `Native` or `Raw` are the game's own values. Fields described as *derived* (`class`, `flowStatus`, `status`, `rankScore`) are the bridge's interpretation, and the native inputs are always returned alongside.
- **Unsupported metrics** are `null`, never zero. Each building lists the metrics it actually has in `measured`.

Errors use the standard envelope (`ok: false`, `error: "<Type>: <code>"`).

## Buildings

### `building_get_details`
| Input | Type | Required |
|---|---|---|
| `index`, `version` | int | yes |

Output: one building row (see `building_query`) plus `roadEdge`, `buildingFlags`, `efficiencyFactors[]`, `upgrades[]`, `renters[]` (≤64, with `kind`, `prefab`, `rentRaw`), `rentersTruncated`, `serviceDataRaw`.

Errors: `live_building_entity_required`.

### `building_query`
| Input | Type | Default |
|---|---|---|
| area filter | see above | whole city |
| `category` | string or array: `residential`, `mixed`, `commercial`, `industrial`, `office`, `service`, `other` | all |
| `district` | `{index, version}` | any |
| `filter` | prefab-name substring | — |
| `problemsOnly` | bool | false |
| `abandoned` | bool | — |
| `minLevel` | int | — |
| `utilization` | `underused` / `partial` / `healthy` / `overloaded` | — |
| `includeSubBuildings` | bool (include upgrade extensions) | false |
| `offset`, `limit` | int, limit ≤ 2048 | 0, 256 |

Row fields: `index`, `version`, `prefab`, `category`, `position`, `district` (`{index, version, name}` or null), `level`, `condition`, `abandoned`, `underConstruction`, `subBuilding`, `efficiency` (`BuildingUtils.GetEfficiency`), `serviceUsage`, `utilization` (`housing`, `residents`, `employment`, `companies`, `education`, `healthcare`, `averageRentRaw`, each present only when supported), `designUpkeepRaw`, `issues[]`, `measured[]`.

Each utilization object has the shape `{used, capacity, ratio, status}`. Status bands: underused < 0.25 ≤ partial < 0.7 ≤ healthy ≤ 1.0 < overloaded.

```json
{"command":"building_query","args":{"category":"service","utilization":"overloaded","limit":50}}
```

### `building_analyze_utilization`
Inputs: area filter, `district`, `listLimit` (1–100, default 20).

Output: `byCategory.{cat}.{buildings, withIssues, abandoned, residents, housing, employment}`, `services.{education, healthcare}`, `underused[]`/`overloaded[]` (sorted, with `metric`, `used`, `capacity`, `ratio`), `underusedTotal`, `overloadedTotal`, `thresholds`, `notes`.

### `city_get_zoning_summary`
Inputs: area filter (block position).

Output: `byArea` and `byZone` (keyed by zone prefab name), each `{cells, occupied, emptyBuildable, blocked}`, plus `blocks`. Only visible cells are counted.

## Traffic

### `traffic_get_network`
| Input | Type | Notes |
|---|---|---|
| area filter | required | radius ≤ 2000 |
| `class` | string or array: `local`, `collector`, `arterial`, `highway` | optional |
| `offset`, `limit` | limit ≤ 1024 | default 256 |

Edge row: `prefab`, `class` (derived), `length`, `startNode`, `endNode`, `start`, `end`, `lanes.{carForward, carBackward, publicTransportOnly, parking, pedestrian, trackOnly}`, `speedLimitNative`, `highwayRules`, `separatedCarriageways`, `widthMetres`, `flowSpeedNative[4]`, `minFlowSpeed`, `flowStatus` (derived), `upgrades[]` (e.g. `left:ForbidLeftTurn`). The page summary adds `lengthByClassMetres` and `lengthByFlowStatusMetres`.

Class rule (derived): `highwayRules` → highway; otherwise the two-way-equivalent lane count is ≥6, or ≥4 with separated carriageways → arterial; ≥4 → collector; else local.

### `traffic_get_intersection`
Inputs: node `index`, `version`; `includeLanes` (bool).

Output: `position`, `edges[]` (edge rows), `roadEdgeCount`, `hierarchyGap`, `hierarchySeverity`, `trafficLights` (`state`, `signalGroups`, `currentGroup`, …) or null, `nodeUpgrades[]`, `movements[]` (`fromEdge`, `toEdge`, `movement`: straight/left/right/gentle_left/gentle_right/uturn, `laneConnections`, `controls`: Yield/Stop/TrafficLights/…), `carConnectionLanes`, `pedestrianLanes`, `unresolvedConnections`, and `laneConnections[]` (≤256) when `includeLanes` is set.

### `traffic_get_lane_rules`
Inputs: node `index`, `version`.

Output: `vanilla.{nodeUpgrades, edgeUpgrades, trafficLights}`, `realisedConnections`, `movements`, `trafficMod` (raw Traffic-mod records when the mod's types resolve; otherwise `available: false` with the adapter status), `writeSupport: "read_only…"`.

### `traffic_find_bottlenecks`
Inputs: area filter (optional), `limit` (≤100, default 20), `maxFlowSpeed` (default 0.6).

Output: `edges[]` ranked by `rankScore`, `edgesBelowThreshold`, `congestedIntersections[]` (`meanApproachCongestion`), `sampledEdges`, `edgesWithoutFlowData`, `cityAverages` (TrafficFlowSystem).

### `traffic_analyze`
Inputs: area filter (optional), `listLimit`.

Output: `roadEdges`, `roadKm`, `lengthByClassMetres`, `intersections`, `signalledNodes`, `intersectionsPerKm`, `hierarchyGaps[]` (gap ≥2), `hierarchyGapTotal`, `deadEnds[]` (outside connections excluded), `shortLinksBetweenIntersections[]` (<40 m), `severeSegments`, `cityAverages`, `recommendations[]`.

### `traffic_get_queues`
Inputs: area filter (optional, radius ≤10000), `limit` (≤200, default 20).

Output: `drivingVehicles`, `stoppedVehicles` (under 0.5 m/s), `byType` (total/stopped), `stoppedWaitingFor` (the game's car AI `Blocker` type: Continuing, Signal, Crossing, Oncoming, Spawn, None), `destinations`, `queues[]` (where each queue's head vehicle is held up, with `vehiclesQueued`, `queueHeads`, `headWaitingFor`) and `deadlocks[]` (loops of vehicles blocking each other). It shows the cause behind the flow figures from `traffic_find_bottlenecks`.

## City telemetry

### `city_get_notifications`
Inputs: area filter (optional), `type` (case-insensitive substring of the icon prefab name), `offset`, `limit` (≤1000, default 100).

Output: `notifications[]` (`type`, `priority`, `position`, `target` with `kind` and `prefab`), `countsByType` for the filtered set, `iconsInCity`, and paging fields. These are the in-world warning icons, for example the commercial "not enough customers" icon.

### `city_get_statistics`
Inputs: none, which lists every recorded series with its current value; or `type` (a `StatisticType` name such as `Population`, `Unemployed`, `ServiceCount`, `PassengerCountBus`), `parameter` (resource index for Service*/Processing*/Office* series; listed by the no-type call) and `samples` (1–512, default 64).

Output: `current`, `recorded`, `totalSamples`, `frames[]`, `values[]`, and `resource` for per-resource series. The game keeps 32 samples per in-game day.

## Transit

### `transit_get_lines`
Inputs: `type` (`bus`, `tram`, `train`, `metro`, `ship`, `airplane`, ...), `includeStops`, `offset`, `limit` (≤200, default 50).

Output: `lines[]` with `name`, `number`, `type`, `color`, `active`, `schedule`, `stops`, `vehicles`, `passengersOnBoard`, `usage` (on board ÷ capacity), `lengthMetres`, `waitingPassengers`, and with `includeStops`, a `stopList[]` holding the waiting passengers at each stop. The figures come from `TransportUIUtils.BuildTransportLine`, the helper the game's transportation panel uses.

The data sources and the queue-head approach for these four commands follow LancerComet/cities-skylines-2-mcp (Apache-2.0).

### `traffic_get_game_bottlenecks`
Inputs: area filter (optional), `limit` (≤500, default 50).

Output: `cityTrafficFlowPercent` and `cityTrafficVolume` as the traffic info view computes them, `cityRoadsAveraged`, `bottleneckLanes`, and `bottlenecks[]`. Each entry is a lane the game marks with `Bottleneck` (the red car icon), grouped by its road or junction, with `lanes`, `position` and the road's `flowPercent`.

### `city_get_map_layer`
Inputs: `layer` (`landValue`, `groundPollution`, `airPollution`, `noisePollution`, `groundWater`, `groundWaterPollution`), an optional area (`bbox`, or `x`/`z`/`radius` used as its bounding square) and `resolution` (4–256, default 64).

Output: `values[row][column]` (row 0 = minZ, column 0 = minX; each output cell is the mean of the native cells it covers), `bounds`, `nativeCells`, `nativeCellMetres`, `min`, `max`, `mean` and `peak` with its position. Land value uses a 128×128 map with 112 m cells; the other layers use 256×256 with 56 m cells. With the NoPollution mod installed, the pollution layers are genuinely all zero.

### `city_capture_view`
Inputs: `width` (160–3840, default 1280); the height follows the screen aspect ratio.

Output: `path` to a PNG under `<mailbox>\captures`, `width`, `height`, `bytes` and `camera`. The image is rendered from the main camera the way the save preview is, with the UI, overlays and info views hidden. Use `set_camera` first to frame an area.

### `city_get_finances` / `city_get_utilities` / `city_get_policies`
- `city_get_finances` returns the loan (`amount`, `dailyInterestRate`, `dailyPayment`, `creditworthiness`) and `serviceFees` (`fee`, `estimatedIncome`, and `collectedInternal`/`collectedExport`/`collectedImport` from `ServiceFeeSystem.GetServiceFees`).
- `city_get_utilities` returns electricity production, consumption, battery, import and export; water and sewage capacity, consumption and trade; and the garbage accumulation rate, all in native units.
- `city_get_policies` takes optional `districtIndex`/`districtVersion` (by default it returns city policies). It returns `policies[]` with `name`, localized `title`, `active`, `adjustment`, `hasSlider` and `locked`.

### Additions to existing commands
- `get_city_state.clock`: `year`, `dayOfYear`, `daysPerYear`, `timeOfDay` and `normalizedDate`, from the game clock.
- `get_city_diagnostics.populationDetails.ageStructure`: children, teens, adults, seniors and moved-in citizens.
- `get_tiles`: per-tile `features` (Area, BuildableLand, FertileLand, Forest, Oil, Ore, GroundWater, Fish, ...), plus `ownedTiles`, `upkeepEnabled` and `upkeepCostMultiplier`.
- `city_get_notifications`: lane-owned icons (traffic bottlenecks) now resolve to their road or junction, with the lane in `target.via`.
- `transit_get_lines`: line and stop names come from `NameSystem.GetName`, as the UI shows them (e.g. "Bus line 1", "127 Linden Street").

The data sources for these commands follow LancerComet/cities-skylines-2-mcp (Apache-2.0).

## Analysis

### `city_analyze`
Inputs: `focus`: `all` (default), `infrastructure`, `economy`, `services`, `traffic`, `zoning`.

Output: `measured` (the inputs used), and `recommendations[]` sorted by `priority` (1 = most urgent). Each recommendation has `id`, `category`, `priority`, `problem`, `evidence`, `affectedEntities`, `proposedAction`, `expectedBenefit`, `risksAndTradeoffs`, `confidence` (low/medium/high), `verificationCriteria`, `requiresApproval`, and `outcomeGuarantee: "none…"`. Nothing is applied.

## Integrations

These work without a loaded city, except where noted.

| Command | Inputs | Output |
|---|---|---|
| `integrations_status` | — | Per mod: `loaded`, `version`, `readSupport`, `missingTypes`, `writeSupport`; city counts when a city is loaded |
| `roadbuilder_list_configs` | `filter`, `offset`, `limit` (≤500), `includeUsage` (needs a city) | Summaries: `id`, `name`, `category`, `speedLimit`, `addons`, `lanesByKind`, `carLanesNormal`, `carLanesInverted`, `estimatedWidthMetres`, `file`, `edgesInCity` |
| `roadbuilder_get_config` | `id` | `summary`, full `config`, `file`, `edgesInCity` |
| `roadbuilder_validate_config` | `config` (RoadConfig object, request ≤16 KiB) | `valid`, `errors[]`, `warnings[]`, `summary`, `knownGroupPrefabs` |

Road Builder direction is reported as its own `Invert` flag. Widths are summed from lane options and labelled as estimates.

## Vanilla traffic controls (mutating; require bridge controls)

These drive the game's own road-upgrade tool (`NetToolSystem`, Replace mode) exactly as the in-game upgrade brush does. Removal uses the tool's native remove-upgrade path. Both commands:

- validate the preview before applying: no errors, nothing deleted, only the target node or edge and its directly connected network touched, and cost ≤ `maxCost`;
- return an operation ID (poll with `get_operation`), or `changed: false` when the target is already in the requested state;
- read the `Upgraded` flags back after applying, and fail with `upgrade_not_observed` if they don't match.

Both are allowed in `batch_execute`. Changes are reversible with the opposite request. Entity IDs may change if the network is later rebuilt.

### `traffic_set_intersection_control`
| Input | Type | Notes |
|---|---|---|
| `index`, `version` | node | intersection with ≥3 road edges |
| `control` | `traffic_lights` / `all_way_stop` / `default` | `default` removes whichever upgrade is present, leaving the game's automatic behaviour |
| `previewOnly` | bool | validate without applying |
| `maxCost` | int ≤ 1,000,000 | default 100,000 |

Operation result: `target`, `flagsBefore`, `flagsPreview`, `flagsAfter`, `previewTouched`, `previewCost`, `applied`.

### `traffic_set_turn_restriction`
| Input | Type | Notes |
|---|---|---|
| `edgeIndex`, `edgeVersion` | road edge | the approach road |
| `nodeIndex`, `nodeVersion` | node | the end of that road where the ban applies |
| `movement` | `left` / `right` / `straight` | |
| `forbid` | bool | true = add the ban, false = remove it |
| `previewOnly`, `maxCost` | as above | |

A banned movement is not removed from the junction: the game keeps the connection lane and flags it `Forbidden`, which `traffic_get_intersection` reports in `movements[].controls`. For removals, `flagsPreview` can still show the old flags; `flagsAfter` is authoritative.

The ban is stored on the edge side that corresponds to the chosen end: start → left and end → right in right-hand traffic, swapped in left-hand traffic. The result reports `side`, and verification also checks that the opposite end did not change.

```json
{"command":"traffic_set_turn_restriction","args":{"edgeIndex":212264,"edgeVersion":1,"nodeIndex":199995,"nodeVersion":1,"movement":"left","forbid":true,"previewOnly":true}}
```
