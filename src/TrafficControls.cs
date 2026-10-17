using System;
using System.Collections.Generic;
using System.Linq;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CitiesIIAgentBridge
{
    // Applies the game's own road upgrades (traffic lights, all-way stop, turn bans) through
    // NetToolSystem in Replace mode, exactly as the upgrade tool does from the UI. Removal uses
    // the tool's native removeUpgrade path. Nothing is written to ECS directly.
    public class BridgeUpgradeTool : NetToolSystem
    {
        public override string toolID => "CitiesIIAgentUpgrade";
        private string operation;
        private int stage, frames, maxCost;
        private ControlPoint point;
        private Entity target;
        private bool remove, previewOnly;
        private DateTime deadline;
        internal Func<CompositionFlags, string> Verify; // returns null when the applied flags are as intended

        public void Begin(NetPrefab upgrade, ControlPoint p, Entity targetEntity, bool removeUpgrade, bool preview, int budget)
        {
            operation = ConstructionAccess.Begin(preview ? "traffic_control_preview" : "traffic_control");
            point = p; target = targetEntity; remove = removeUpgrade; previewOnly = preview; maxCost = budget;
            stage = frames = 0; deadline = DateTime.UtcNow.AddSeconds(20);
            ConstructionAccess.Results[operation]["target"] = NativeBuild.Id(target);
            ConstructionAccess.Results[operation]["flagsBefore"] = FlagsJson(CurrentFlags(EntityManager, target));
            prefab = upgrade; mode = Mode.Replace; elevation = 0; parallelCount = 0;
            World.GetExistingSystemManaged<ToolSystem>().activeTool = this;
        }

        internal static CompositionFlags CurrentFlags(EntityManager em, Entity e) =>
            em.Exists(e) && em.HasComponent<Upgraded>(e) ? em.GetComponentData<Upgraded>(e).m_Flags : default;

        internal static JObject FlagsJson(CompositionFlags f) => new JObject
        {
            ["general"] = f.m_General.ToString(), ["left"] = f.m_Left.ToString(), ["right"] = f.m_Right.ToString()
        };

        protected override void OnStopRunning()
        {
            if (operation != null) { ConstructionAccess.Finish(operation, "interrupted", "active_tool_changed"); operation = null; }
            base.OnStopRunning();
        }

        protected override JobHandle OnUpdate(JobHandle deps)
        {
            if (operation == null) return deps;
            try
            {
                deps.Complete();
                if (ConstructionAccess.Allowed?.Invoke() != true || DateTime.UtcNow > deadline) throw new InvalidOperationException("construction_stopped_or_timed_out");
                if (stage == 0)
                {
                    var points = GetControlPoints(out var ready); ready.Complete(); points.Clear(); points.Add(point); points.Add(point);
                    ConstructionAccess.Field(typeof(NetToolSystem), this, "m_Prefab", prefab);
                    applyMode = ApplyMode.Clear;
                    deps = (JobHandle)ConstructionAccess.Call(typeof(NetToolSystem), this, "SnapControlPoints", deps, remove);
                    deps = (JobHandle)ConstructionAccess.Call(typeof(NetToolSystem), this, "UpdateCourse", deps, remove);
                    ConstructionAccess.Results[operation]["status"] = "validating"; stage = 1; return deps;
                }
                if (stage == 1)
                {
                    applyMode = ApplyMode.None;
                    if (++frames < 4) return deps;
                    var errors = ConstructionAccess.Errors(EntityManager);
                    if (errors.Count > 0) { ConstructionAccess.Results[operation]["placementErrors"] = errors; throw new InvalidOperationException("game_rejected_upgrade"); }
                    if (!GetAllowApply()) { if (frames < 30) return deps; throw new InvalidOperationException("no_valid_upgrade_preview"); }
                    InspectPreview();
                    if (previewOnly) { ConstructionAccess.Results[operation]["previewOnly"] = true; Finish("complete"); return deps; }
                    applyMode = ApplyMode.Apply; stage = 2; frames = 0;
                    ConstructionAccess.Results[operation]["status"] = "applying"; return deps;
                }
                applyMode = ApplyMode.Clear;
                if (++frames < 5) return deps;
                if (!EntityManager.Exists(target) || EntityManager.HasComponent<Deleted>(target)) throw new InvalidOperationException("target_replaced_or_removed_inspect_before_retrying");
                var after = CurrentFlags(EntityManager, target);
                ConstructionAccess.Results[operation]["flagsAfter"] = FlagsJson(after);
                string problem = Verify?.Invoke(after);
                if (problem != null) throw new InvalidOperationException("upgrade_not_observed: " + problem);
                ConstructionAccess.Results[operation]["applied"] = true;
                Finish("complete");
            }
            catch (Exception e) { Finish("failed", e.Message); }
            return deps;
        }

        // The game regenerates geometry around an upgraded node or edge, so the preview legitimately
        // contains neighbouring network entities (a neighbourhood whitelist rejected every real
        // upgrade in live test 3). The safety property enforced instead: nothing is deleted, no new
        // network is created, and no entity other than the target changes its Upgraded flags.
        private void InspectPreview()
        {
            int cost = 0; bool targetSeen = false; string violation = null; var touched = new JArray(); int touchedTotal = 0;
            var result = ConstructionAccess.Results[operation];
            using (var q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>()))
            using (var es = q.ToEntityArray(Allocator.Temp))
                foreach (var e in es)
                {
                    var t = EntityManager.GetComponentData<Temp>(e);
                    if ((t.m_Flags & TempFlags.Cancel) != 0) continue;
                    cost = checked(cost + Math.Max(0, t.m_Cost));
                    bool tempIsNetwork = EntityManager.HasComponent<Edge>(e) || EntityManager.HasComponent<Game.Net.Node>(e);
                    if (t.m_Original == Entity.Null)
                    {
                        // Owned helper objects (props, lanes) may be generated; a new top-level edge or node may not.
                        if (tempIsNetwork && !EntityManager.HasComponent<Owner>(e) && (t.m_Flags & TempFlags.Create) != 0) violation ??= "upgrade_preview_would_create_network";
                        continue;
                    }
                    bool network = EntityManager.HasComponent<Edge>(t.m_Original) || EntityManager.HasComponent<Game.Net.Node>(t.m_Original);
                    if (!network) continue;
                    var before = CurrentFlags(EntityManager, t.m_Original);
                    var after = EntityManager.HasComponent<Upgraded>(e) ? EntityManager.GetComponentData<Upgraded>(e).m_Flags : before;
                    bool flagsChange = !after.Equals(before);
                    ++touchedTotal;
                    if (touched.Count < 32)
                    {
                        var row = NativeBuild.Id(t.m_Original); row["tempFlags"] = t.m_Flags.ToString();
                        row["kind"] = EntityManager.HasComponent<Edge>(t.m_Original) ? "edge" : "node";
                        row["upgradedChanges"] = flagsChange; if (flagsChange) row["flagsPreview"] = FlagsJson(after);
                        touched.Add(row);
                    }
                    if ((t.m_Flags & TempFlags.Delete) != 0) violation ??= "upgrade_preview_would_delete_network";
                    if (t.m_Original == target)
                    {
                        targetSeen = true; result["flagsPreview"] = FlagsJson(after);
                    }
                    else if (flagsChange) violation ??= "upgrade_preview_changes_another_entity: " + t.m_Original.Index + ":" + t.m_Original.Version;
                }
            result["previewTouched"] = touched; result["previewTouchedTotal"] = touchedTotal; result["previewCost"] = cost;
            if (violation != null) throw new InvalidOperationException(violation);
            if (!targetSeen) throw new InvalidOperationException("target_not_in_upgrade_preview");
            if (cost > maxCost || cost > World.GetExistingSystemManaged<CitySystem>().moneyAmount) throw new InvalidOperationException("cost_exceeds_budget");
        }

        private void Finish(string status, string error = null)
        {
            applyMode = ApplyMode.Clear; ConstructionAccess.Finish(operation, status, error); operation = null;
            World.GetExistingSystemManaged<ToolSystem>().activeTool = World.GetExistingSystemManaged<DefaultToolSystem>();
        }
    }

    public sealed partial class Mod
    {
        private static readonly string[] IntersectionControls = { "traffic_lights", "all_way_stop", "default" };
        private static readonly string[] TurnMovements = { "left", "right", "straight" };

        // Upgrade prefabs are matched by the flags they set, not by display name.
        private static NetPrefab UpgradePrefab(World w, CompositionFlags expected, bool nodeUpgrade)
        {
            var em = w.EntityManager; var ps = w.GetExistingSystemManaged<PrefabSystem>();
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<PlaceableNetData>()))
            using (var es = q.ToEntityArray(Allocator.Temp))
                foreach (var e in es.ToArray().OrderBy(e => e.Index))
                {
                    var data = em.GetComponentData<PlaceableNetData>(e);
                    if ((data.m_PlacementFlags & PlacementFlags.IsUpgrade) == 0) continue;
                    if (((data.m_PlacementFlags & PlacementFlags.NodeUpgrade) != 0) != nodeUpgrade) continue;
                    if (!data.m_SetUpgradeFlags.Equals(expected) || IsPrefabLocked(em, e)) continue;
                    if (ps.TryGetPrefab<NetPrefab>(e, out var p)) return p;
                }
            throw new InvalidOperationException("upgrade_prefab_unavailable_or_locked");
        }

        private JObject SetIntersectionControl(JObject args)
        {
            var w = RequireCity(); RequireControl(); CheckBuildTool(w); var em = w.EntityManager;
            var node = new Entity { Index = RequiredInt(args, "index"), Version = RequiredInt(args, "version") };
            if (!em.Exists(node) || !em.HasComponent<Game.Net.Node>(node) || em.HasComponent<Temp>(node)) throw new ArgumentException("live_network_node_required");
            string control = (string)args["control"];
            if (!IntersectionControls.Contains(control)) throw new ArgumentException("control_must_be_one_of: " + string.Join(",", IntersectionControls));
            int roads = em.HasBuffer<ConnectedEdge>(node) ? em.GetBuffer<ConnectedEdge>(node, true).ToNativeArray(Allocator.Temp).ToArray().Count(c => em.HasComponent<Road>(c.m_Edge)) : 0;
            if (roads < 3) throw new ArgumentException("intersection_controls_need_3_or_more_road_edges");
            bool preview = (bool?)args["previewOnly"] == true;
            int budget = args["maxCost"] == null ? 100000 : RequiredInt(args, "maxCost"); if (budget < 0 || budget > 1000000) throw new ArgumentException("invalid_budget");
            var general = BridgeUpgradeTool.CurrentFlags(em, node).m_General;
            bool lights = (general & CompositionFlags.General.TrafficLights) != 0, stop = (general & CompositionFlags.General.AllWayStop) != 0;
            var lightsFlags = new CompositionFlags { m_General = CompositionFlags.General.TrafficLights };
            var stopFlags = new CompositionFlags { m_General = CompositionFlags.General.RemoveTrafficLights | CompositionFlags.General.AllWayStop };
            NetPrefab prefab; bool remove;
            switch (control)
            {
                case "traffic_lights": if (lights) return Unchanged(node, control); prefab = UpgradePrefab(w, lightsFlags, true); remove = false; break;
                case "all_way_stop": if (stop) return Unchanged(node, control); prefab = UpgradePrefab(w, stopFlags, true); remove = false; break;
                default:
                    if (lights) { prefab = UpgradePrefab(w, lightsFlags, true); remove = true; }
                    else if (stop || (general & CompositionFlags.General.RemoveTrafficLights) != 0) { prefab = UpgradePrefab(w, stopFlags, true); remove = true; }
                    else return Unchanged(node, control);
                    break;
            }
            var position = em.GetComponentData<Game.Net.Node>(node).m_Position;
            var tool = w.GetExistingSystemManaged<BridgeUpgradeTool>();
            tool.Verify = after =>
            {
                bool l = (after.m_General & CompositionFlags.General.TrafficLights) != 0, s = (after.m_General & CompositionFlags.General.AllWayStop) != 0;
                if (control == "traffic_lights" && !(l && !s)) return "traffic_lights_not_set";
                if (control == "all_way_stop" && !(s && !l)) return "all_way_stop_not_set";
                if (control == "default" && (l || s)) return "upgrade_still_present";
                return null;
            };
            tool.Begin(prefab, new ControlPoint { m_OriginalEntity = node, m_Position = position, m_HitPosition = position, m_Rotation = quaternion.identity }, node, remove, preview, budget);
            var status = ConstructionAccess.Status(ConstructionAccess.Active); status["requested"] = control; status["removeUpgrade"] = remove;
            return status;
        }

        private JObject SetTurnRestriction(JObject args)
        {
            var w = RequireCity(); RequireControl(); CheckBuildTool(w); var em = w.EntityManager;
            var edgeEntity = new Entity { Index = RequiredInt(args, "edgeIndex"), Version = RequiredInt(args, "edgeVersion") };
            var node = new Entity { Index = RequiredInt(args, "nodeIndex"), Version = RequiredInt(args, "nodeVersion") };
            if (!em.Exists(edgeEntity) || !em.HasComponent<Edge>(edgeEntity) || !em.HasComponent<Road>(edgeEntity) || em.HasComponent<Temp>(edgeEntity)) throw new ArgumentException("live_road_edge_required");
            var edge = em.GetComponentData<Edge>(edgeEntity);
            if (node != edge.m_Start && node != edge.m_End) throw new ArgumentException("node_must_be_an_end_of_the_edge");
            string movement = (string)args["movement"];
            if (!TurnMovements.Contains(movement)) throw new ArgumentException("movement_must_be_one_of: " + string.Join(",", TurnMovements));
            if (args["forbid"]?.Type != JTokenType.Boolean) throw new ArgumentException("boolean_required: forbid");
            bool forbid = (bool)args["forbid"], preview = (bool?)args["previewOnly"] == true;
            int budget = args["maxCost"] == null ? 100000 : RequiredInt(args, "maxCost"); if (budget < 0 || budget > 1000000) throw new ArgumentException("invalid_budget");
            var bit = movement == "left" ? CompositionFlags.Side.ForbidLeftTurn : movement == "right" ? CompositionFlags.Side.ForbidRightTurn : CompositionFlags.Side.ForbidStraight;
            // The native tool stores the ban on the edge side that corresponds to the chosen end:
            // start -> Left, end -> Right for right-hand traffic; swapped for left-hand traffic.
            bool atEnd = node == edge.m_End;
            bool leftHand = w.GetExistingSystemManaged<CityConfigurationSystem>().leftHandTraffic;
            bool useRight = atEnd != leftHand;
            var before = BridgeUpgradeTool.CurrentFlags(em, edgeEntity);
            bool present = ((useRight ? before.m_Right : before.m_Left) & bit) != 0;
            if (present == forbid) return Unchanged(edgeEntity, (forbid ? "forbid_" : "allow_") + movement);
            var prefab = UpgradePrefab(w, new CompositionFlags { m_Right = bit }, false);
            var curve = em.GetComponentData<Curve>(edgeEntity).m_Bezier;
            float t = atEnd ? 0.85f : 0.15f, u = 1 - t;
            float3 position = u * u * u * curve.a + 3 * u * u * t * curve.b + 3 * u * t * t * curve.c + t * t * t * curve.d;
            var tool = w.GetExistingSystemManaged<BridgeUpgradeTool>();
            tool.Verify = after =>
            {
                bool set = ((useRight ? after.m_Right : after.m_Left) & bit) != 0;
                if (set != forbid) return forbid ? "ban_not_set_on_expected_end" : "ban_still_present";
                var otherBefore = useRight ? before.m_Left : before.m_Right; var otherAfter = useRight ? after.m_Left : after.m_Right;
                if ((otherBefore & bit) != (otherAfter & bit)) return "opposite_end_changed_unexpectedly";
                return null;
            };
            tool.Begin(prefab, new ControlPoint { m_OriginalEntity = edgeEntity, m_Position = position, m_HitPosition = position, m_CurvePosition = t, m_Rotation = quaternion.identity }, edgeEntity, !forbid, preview, budget);
            var status = ConstructionAccess.Status(ConstructionAccess.Active);
            status["requested"] = (forbid ? "forbid_" : "allow_") + movement; status["node"] = NativeBuild.Id(node); status["side"] = useRight ? "right" : "left";
            return status;
        }

        private static JObject Unchanged(Entity target, string requested) => new JObject
        {
            ["status"] = "complete", ["changed"] = false, ["requested"] = requested, ["target"] = NativeBuild.Id(target),
            ["note"] = "Already in the requested state; no tool operation was started."
        };
    }
}
