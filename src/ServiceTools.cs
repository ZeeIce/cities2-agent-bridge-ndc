using System;
using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using System.Reflection;

namespace CitiesIIAgentBridge
{
    internal static class NativeBuild
    {
        internal static HashSet<Entity> Buildings(EntityManager em)
        {
            var result = new HashSet<Entity>();
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Buildings.Building>() }, None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() } }))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es) result.Add(e);
            return result;
        }
        internal static int Cost(World w, int maximum)
        {
            int cost = 0;
            using (var q = w.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>()))
            using (var ts = q.ToComponentDataArray<Temp>(Allocator.Temp)) foreach (var t in ts) if ((t.m_Flags & TempFlags.Cancel) == 0) cost = checked(cost + Math.Max(0, t.m_Cost));
            if (cost > maximum || cost > w.GetExistingSystemManaged<CitySystem>().moneyAmount) throw new InvalidOperationException("cost_exceeds_budget");
            return cost;
        }
        internal static JObject Id(Entity e) => new JObject { ["index"] = e.Index, ["version"] = e.Version };
    }

    public class BridgeObjectTool : ObjectToolSystem
    {
        public override string toolID => "CitiesIIAgentObject";
        private string operation;
        private ControlPoint point;
        private int stage, frames, budget;
        private DateTime deadline;
        private HashSet<Entity> before;
        private Entity moved;
        private Entity expectedPrefab;
        private ControlPoint[] candidates;
        private int candidateIndex;
        private bool previewOnly, allowDemolition, applied;
        private float maxSnapDistance;
        // Set for net objects placed onto an intersection node (roundabouts) instead of buildings.
        private Entity nodeTarget;
        // Set for asset stamps (prebuilt intersections/interchanges); the object tool resolves
        // Create to its own Stamp mode for Stampable prefabs.
        private bool stampMode; private HashSet<Entity> roadsBefore;
        public void BeginStamp(ObjectPrefab stamp, ControlPoint p, int maxCost, bool preview, bool demolish, float snapDistance)
        {
            Begin(stamp, p, maxCost, Entity.Null, preview, null, demolish, snapDistance);
            stampMode = true; roadsBefore = ConstructionAccess.Roads(EntityManager);
            ConstructionAccess.Results[operation]["kind"] = "asset_stamp";
        }
        public void BeginNodeObject(ObjectPrefab netObject, ControlPoint p, int maxCost, Entity node, bool preview, bool demolish)
        {
            Begin(netObject, p, maxCost, Entity.Null, preview, null, demolish, 4);
            nodeTarget = node; ConstructionAccess.Results[operation]["kind"] = "node_object"; ConstructionAccess.Results[operation]["node"] = NativeBuild.Id(node);
        }
        public void Begin(ObjectPrefab building, ControlPoint p, int maxCost, Entity move, bool preview = false, ControlPoint[] alternatives = null, bool demolish = false, float snapDistance = 32)
        {
            nodeTarget = Entity.Null; stampMode = false; roadsBefore = null;
            var requestedPrefab = World.GetExistingSystemManaged<PrefabSystem>().GetEntity(building);
            if (move != Entity.Null && (!EntityManager.HasComponent<PrefabRef>(move) || EntityManager.GetComponentData<PrefabRef>(move).m_Prefab != requestedPrefab))
                throw new InvalidOperationException("relocation_prefab_mismatch");
            operation = ConstructionAccess.Begin(move == Entity.Null ? "building" : "relocate");
            point = p; budget = maxCost; stage = frames = 0; moved = move;
            before = NativeBuild.Buildings(EntityManager); deadline = DateTime.UtcNow.AddSeconds(30);
            expectedPrefab = requestedPrefab;
            candidates = alternatives ?? new[] { p }; candidateIndex = 0; previewOnly = preview; allowDemolition = demolish; applied = false; maxSnapDistance = snapDistance;
            ConstructionAccess.Results[operation]["attempts"] = new JArray();
            prefab = building; mode = move == Entity.Null ? Mode.Create : Mode.Move;
            if (move != Entity.Null) StartMoving(move);
            World.GetExistingSystemManaged<ToolSystem>().activeTool = this;
        }
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
                if (ConstructionAccess.Allowed?.Invoke() != true || DateTime.UtcNow > deadline) throw new InvalidOperationException("stopped_or_timed_out");
                if (stage == 0)
                {
                    // ObjectToolSystem.OnUpdate normally resets move/upgrade state and updates
                    // m_Prefab. We bypass that input-driven method, so do it before every preview.
                    ObjectPlacementState.Prepare(typeof(ObjectToolSystem), this, Entity.Null, moved,
                        World.GetExistingSystemManaged<PrefabSystem>().GetPrefab<ObjectPrefab>(expectedPrefab));
                    point = candidates[candidateIndex];
                    GetAvailableSnapMask(out m_SnapOnMask,out m_SnapOffMask);
                    m_SnapOffMask &= ~(Snap.NetSide | Snap.Shoreline | Snap.ExistingGeometry);
                    // SnapJob reads its own NativeReference<Rotation>, not just ControlPoint.m_Rotation.
                    var rotationField = typeof(ObjectToolSystem).GetField("m_Rotation",BindingFlags.NonPublic|BindingFlags.Instance);
                    var reference = rotationField.GetValue(this); var valueProperty = reference.GetType().GetProperty("Value");
                    var rotation = valueProperty.GetValue(reference); var rotationType = rotation.GetType();
                    rotationType.GetField("m_Rotation").SetValue(rotation,point.m_Rotation);
                    rotationType.GetField("m_ParentRotation").SetValue(rotation,quaternion.identity);
                    rotationType.GetField("m_IsAligned").SetValue(rotation,false);
                    rotationType.GetField("m_IsSnapped").SetValue(rotation,false);
                    valueProperty.SetValue(reference,rotation);
                    var points = GetControlPoints(out var ready); ready.Complete(); points.Clear(); points.Add(point);
                    applyMode = ApplyMode.Clear;
                    deps = (JobHandle)ConstructionAccess.Call(typeof(ObjectToolSystem), this, "SnapControlPoint", deps);
                    deps = (JobHandle)ConstructionAccess.Call(typeof(ObjectToolSystem), this, "UpdateDefinitions", deps);
                    stage = 1; ConstructionAccess.Results[operation]["status"] = "validating"; return deps;
                }
                if (stage == 1)
                {
                    applyMode = ApplyMode.None; if (++frames < 4) return deps;
                    var errors = ConstructionAccess.Errors(EntityManager);
                    if (errors.Count > 0) { if (RetryCandidate("game_rejected_placement",errors)) return deps; throw new InvalidOperationException("game_rejected_placement"); }
                    if (!GetAllowApply()) { if (frames < 30) return deps; if(RetryCandidate("invalid_building_preview",errors)) return deps; throw new InvalidOperationException("invalid_building_preview"); }
                    var points = GetControlPoints(out var ready); ready.Complete();
                    if(points.Length == 0) throw new InvalidOperationException("missing_snapped_point");
                    var snapped = points[points.Length-1];
                    if(math.distance(snapped.m_Position,point.m_Position)>maxSnapDistance) { if(RetryCandidate("snap_moved_too_far",errors)) return deps; throw new InvalidOperationException("snap_moved_too_far"); }
                    point = snapped;
                    if(!allowDemolition)
                    {
                        using(var q=EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>()))
                        using(var ts=q.ToComponentDataArray<Temp>(Allocator.Temp)) foreach(var t in ts)
                            if(t.m_Original!=Entity.Null && t.m_Original!=moved && (t.m_Flags&TempFlags.Delete)!=0 && EntityManager.HasComponent<Game.Buildings.Building>(t.m_Original))
                                throw new InvalidOperationException("would_demolish_existing_building");
                    }
                    if (stampMode) ValidateStampPreview(); else if (nodeTarget == Entity.Null) ValidateBuildingPreview(); else ValidateNodeObjectPreview();
                    ConstructionAccess.Results[operation]["previewCost"] = NativeBuild.Cost(World, budget);
                    ConstructionAccess.Results[operation]["snappedPosition"] = new JObject{["x"]=point.m_Position.x,["y"]=point.m_Position.y,["z"]=point.m_Position.z};
                    ConstructionAccess.Results[operation]["snappedRotation"] = new JArray(point.m_Rotation.value.x,point.m_Rotation.value.y,point.m_Rotation.value.z,point.m_Rotation.value.w);
                    if(previewOnly) { ConstructionAccess.Results[operation]["previewOnly"]=true; Finish("complete"); return deps; }
                    applied = true; ConstructionAccess.Results[operation]["applied"] = true;
                    applyMode = ApplyMode.Apply; stage = 2; frames = 0; ConstructionAccess.Results[operation]["status"] = "applying"; return deps;
                }
                if(stage == 3) { applyMode = ApplyMode.Clear; if(++frames<4)return deps; stage=0;frames=0;return deps; }
                applyMode = ApplyMode.Clear; if (++frames < 5) return deps;
                var created = new JArray(); foreach (var e in NativeBuild.Buildings(EntityManager))
                    if (!before.Contains(e) && EntityManager.HasComponent<PrefabRef>(e) && EntityManager.GetComponentData<PrefabRef>(e).m_Prefab == expectedPrefab && EntityManager.HasComponent<Game.Objects.Transform>(e) && math.distance(EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position,point.m_Position)<8)
                    { var row=NativeBuild.Id(e); row["roadEdge"]=NativeBuild.Id(EntityManager.GetComponentData<Game.Buildings.Building>(e).m_RoadEdge); created.Add(row); }
                ConstructionAccess.Results[operation]["createdBuildings"] = created;
                if (stampMode)
                {
                    var createdEdges = new JArray(); int count = 0;
                    foreach (var r in ConstructionAccess.Roads(EntityManager))
                        if (!roadsBefore.Contains(r)) { if (createdEdges.Count < 128) { var row = NativeBuild.Id(r); if (EntityManager.HasComponent<PrefabRef>(r)) row["prefab"] = World.GetExistingSystemManaged<PrefabSystem>().GetPrefabName(EntityManager.GetComponentData<PrefabRef>(r).m_Prefab); createdEdges.Add(row); } ++count; }
                    ConstructionAccess.Results[operation]["createdEdges"] = createdEdges; ConstructionAccess.Results[operation]["createdEdgeCount"] = count;
                    if (count == 0) throw new InvalidOperationException("stamp_network_not_observed");
                    Finish("complete"); return deps;
                }
                if (nodeTarget != Entity.Null)
                {
                    if (!EntityManager.Exists(nodeTarget)) throw new InvalidOperationException("node_replaced_inspect_before_retrying");
                    bool roundabout = EntityManager.HasComponent<Game.Net.Roundabout>(nodeTarget);
                    ConstructionAccess.Results[operation]["roundabout"] = roundabout;
                    if (roundabout) ConstructionAccess.Results[operation]["radius"] = EntityManager.GetComponentData<Game.Net.Roundabout>(nodeTarget).m_Radius;
                    if (!roundabout) throw new InvalidOperationException("node_object_not_observed");
                    Finish("complete"); return deps;
                }
                bool movedSuccessfully = moved != Entity.Null && EntityManager.Exists(moved) && EntityManager.HasComponent<PrefabRef>(moved) && EntityManager.GetComponentData<PrefabRef>(moved).m_Prefab == expectedPrefab && EntityManager.HasComponent<Game.Objects.Transform>(moved) && Unity.Mathematics.math.distance(EntityManager.GetComponentData<Game.Objects.Transform>(moved).m_Position, point.m_Position) < 16;
                if (created.Count == 0 && !movedSuccessfully) throw new InvalidOperationException("building_change_not_observed");
                Finish("complete");
            }
            catch (Exception e) { Finish("failed", e.Message); }
            return deps;
        }
        private void ValidateBuildingPreview()
        {
            var entries = new List<BuildingPreviewEntry>();
            var roots = new JArray();
            using (var q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>()))
            using (var entities = q.ToEntityArray(Allocator.Temp)) foreach (var entity in entities)
            {
                var temp = EntityManager.GetComponentData<Temp>(entity);
                var prefabEntity = EntityManager.HasComponent<PrefabRef>(entity) ? EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab : Entity.Null;
                bool originalBuilding = temp.m_Original != Entity.Null && EntityManager.HasComponent<Game.Buildings.Building>(temp.m_Original);
                bool building = EntityManager.HasComponent<Game.Buildings.Building>(entity) || EntityManager.HasComponent<BuildingData>(prefabEntity) || originalBuilding;
                if (!building || EntityManager.HasComponent<Owner>(entity) || (originalBuilding && EntityManager.HasComponent<Owner>(temp.m_Original))) continue;
                entries.Add(new BuildingPreviewEntry {
                    PrefabMatches = prefabEntity == expectedPrefab,
                    HasOriginal = temp.m_Original != Entity.Null,
                    OriginalMatches = moved != Entity.Null && temp.m_Original == moved,
                    Create = (temp.m_Flags & TempFlags.Create) != 0,
                    Modify = (temp.m_Flags & TempFlags.Modify) != 0,
                    Delete = (temp.m_Flags & TempFlags.Delete) != 0,
                    Cancel = (temp.m_Flags & TempFlags.Cancel) != 0
                });
                roots.Add(new JObject { ["entity"] = NativeBuild.Id(entity), ["prefab"] = NativeBuild.Id(prefabEntity), ["original"] = NativeBuild.Id(temp.m_Original), ["flags"] = temp.m_Flags.ToString() });
            }
            var result = ConstructionAccess.Results[operation];
            result["expectedPrefab"] = NativeBuild.Id(expectedPrefab);
            result["expectedOriginal"] = NativeBuild.Id(moved);
            result["previewBuildings"] = roots;
            var error = BuildingPreviewSafety.Validate(entries, moved != Entity.Null, allowDemolition);
            if (error != null) throw new InvalidOperationException(error);
        }
        // Building demolition is already refused above unless allowed; a node object must also
        // never delete road network, and the preview must actually involve the target node.
        private void ValidateNodeObjectPreview()
        {
            bool nodeSeen = false; var touched = new JArray();
            using (var q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>()))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es)
            {
                var t = EntityManager.GetComponentData<Temp>(e);
                if ((t.m_Flags & TempFlags.Cancel) != 0 || t.m_Original == Entity.Null) continue;
                bool network = EntityManager.HasComponent<Game.Net.Edge>(t.m_Original) || EntityManager.HasComponent<Game.Net.Node>(t.m_Original);
                if (!network) continue;
                if ((t.m_Flags & TempFlags.Delete) != 0) throw new InvalidOperationException("node_object_would_delete_network");
                if (t.m_Original == nodeTarget) nodeSeen = true;
                if (touched.Count < 32) { var row = NativeBuild.Id(t.m_Original); row["tempFlags"] = t.m_Flags.ToString(); touched.Add(row); }
            }
            ConstructionAccess.Results[operation]["previewTouched"] = touched;
            if (!nodeSeen) throw new InvalidOperationException("target_node_not_in_preview");
        }
        // A stamp must generate road network; it may modify existing roads it connects to, but may
        // delete existing network or buildings only when demolition was explicitly allowed.
        private void ValidateStampPreview()
        {
            int newNetwork = 0, modified = 0, deleted = 0; var touched = new JArray();
            using (var q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>()))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es)
            {
                var t = EntityManager.GetComponentData<Temp>(e);
                if ((t.m_Flags & TempFlags.Cancel) != 0) continue;
                bool tempNetwork = EntityManager.HasComponent<Game.Net.Edge>(e) || EntityManager.HasComponent<Game.Net.Node>(e);
                if (t.m_Original == Entity.Null) { if (tempNetwork) ++newNetwork; continue; }
                bool network = EntityManager.HasComponent<Game.Net.Edge>(t.m_Original) || EntityManager.HasComponent<Game.Net.Node>(t.m_Original);
                if (!network) continue;
                if ((t.m_Flags & TempFlags.Delete) != 0) { ++deleted; if (!allowDemolition) throw new InvalidOperationException("stamp_would_delete_existing_network"); }
                else ++modified;
                if (touched.Count < 32) { var row = NativeBuild.Id(t.m_Original); row["tempFlags"] = t.m_Flags.ToString(); touched.Add(row); }
            }
            var result = ConstructionAccess.Results[operation];
            result["previewNewNetwork"] = newNetwork; result["previewModifiedNetwork"] = modified; result["previewDeletedNetwork"] = deleted; result["previewTouched"] = touched;
            if (newNetwork == 0) throw new InvalidOperationException("stamp_preview_generated_no_network");
        }
        private bool RetryCandidate(string error,JArray errors)
        {
            var result=ConstructionAccess.Results[operation]; result["placementErrors"]=errors;
            ((JArray)result["attempts"]).Add(new JObject{["candidate"]=candidateIndex,["reason"]=error,["errors"]=errors.DeepClone()});
            if(applied || ++candidateIndex>=candidates.Length) return false;
            applyMode=ApplyMode.Clear;stage=3;frames=0;return true;
        }
        private void Finish(string status, string error = null)
        {
            applyMode = ApplyMode.Clear; ConstructionAccess.Finish(operation, status, error); operation = null;
            World.GetExistingSystemManaged<ToolSystem>().activeTool = World.GetExistingSystemManaged<DefaultToolSystem>();
        }
    }

    public class BridgeBulldozeTool : BulldozeToolSystem
    {
        public override string toolID => "CitiesIIAgentBulldoze";
        private string operation;
        private Entity target;
        private ControlPoint point;
        private int stage, frames;
        private DateTime deadline;
        public void Begin(Entity entity, ControlPoint p)
        {
            operation = ConstructionAccess.Begin("demolish"); target = entity; point = p; stage = frames = 0;
            deadline = DateTime.UtcNow.AddSeconds(20);
            World.GetExistingSystemManaged<ToolSystem>().activeTool = this;
        }
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
                if (ConstructionAccess.Allowed?.Invoke() != true || DateTime.UtcNow > deadline) throw new InvalidOperationException("stopped_or_timed_out");
                if (stage == 0)
                {
                    var points = ConstructionAccess.Read<NativeList<ControlPoint>>(typeof(BulldozeToolSystem), this, "m_ControlPoints"); points.Clear(); points.Add(point);
                    applyMode = ApplyMode.Clear;
                    deps = (JobHandle)ConstructionAccess.Call(typeof(BulldozeToolSystem), this, "UpdateDefinitions", deps);
                    stage = 1; return deps;
                }
                if (stage == 1)
                {
                    applyMode = ApplyMode.None; if (++frames < 4) return deps;
                    if (!GetAllowApply()) throw new InvalidOperationException("game_rejected_demolition");
                    using (var q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>()))
                    using (var ts = q.ToComponentDataArray<Temp>(Allocator.Temp))
                    {
                        bool found = false;
                        foreach (var t in ts) if (t.m_Original == target && (t.m_Flags & TempFlags.Delete) != 0) found = true;
                        if (!found) throw new InvalidOperationException("target_not_in_demolition_preview");
                    }
                    applyMode = ApplyMode.Apply; stage = 2; frames = 0; return deps;
                }
                applyMode = ApplyMode.Clear; if (++frames < 5) return deps;
                if (EntityManager.Exists(target) && !EntityManager.HasComponent<Deleted>(target)) throw new InvalidOperationException("demolition_not_observed");
                Finish("complete");
            }
            catch (Exception e) { Finish("failed", e.Message); }
            return deps;
        }
        private void Finish(string status, string error = null)
        {
            applyMode = ApplyMode.Clear; ConstructionAccess.Finish(operation, status, error); operation = null;
            World.GetExistingSystemManaged<ToolSystem>().activeTool = World.GetExistingSystemManaged<DefaultToolSystem>();
        }
    }
}
