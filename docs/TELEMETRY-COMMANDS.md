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
