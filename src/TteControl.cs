using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace CitiesIIAgentBridge
{
    // Traffic Tool Essentials (C2VM) signal control. TTE has no public API, so the bridge drives the
    // same JSON handlers its own panel calls (UISystem.Call*), resolved by reflection at runtime. That
    // keeps TTE's internal state (edge info, sync groups, dynamic-phase resets) consistent with a UI edit.
    // Data model (decompiled TTE): per junction a CustomTrafficLights component, a CustomPhaseData buffer
    // (one entry per phase) and an EdgeGroupMask buffer; per approach edge and turn a Go and a Yield
    // bitmask where bit i means "green in phase i" (Yield = permissive green).
    internal sealed class TteBridge
    {
        private const string AssemblyName = "C2VM.TrafficToolEssentials";
        private const string Ns = "C2VM.TrafficToolEssentials.";
        private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public Type UiType, LightsType, PhaseType, MaskType; public object Ui; public string Error;

        public static TteBridge Open(World w)
        {
            var b = new TteBridge();
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
            if (asm == null) { b.Error = "tte_not_loaded"; return b; }
            b.UiType = asm.GetType(Ns + "Systems.UI.UISystem"); b.LightsType = asm.GetType(Ns + "Components.CustomTrafficLights");
            b.PhaseType = asm.GetType(Ns + "Components.CustomPhaseData"); b.MaskType = asm.GetType(Ns + "Components.EdgeGroupMask");
            if (b.UiType == null || b.LightsType == null || b.PhaseType == null || b.MaskType == null) { b.Error = "tte_types_changed"; return b; }
            b.Ui = w.GetExistingSystemManaged(b.UiType);
            if (b.Ui == null) b.Error = "tte_ui_system_missing";
            return b;
        }

        public string Call(string method, object payload)
        {
            var m = UiType.GetMethod(method, Any, null, new[] { typeof(string) }, null) ?? throw new InvalidOperationException("tte_method_missing: " + method);
            return (string)m.Invoke(Ui, new object[] { payload is string s ? s : JsonConvert.SerializeObject(payload) });
        }
        // A property in current TTE (public get, private set); fall back to a field for other versions.
        public Entity Selected => (Entity)(Member(Ui, "m_SelectedEntity") ?? Entity.Null);
        public void Select(Entity e)
        {
            var m = UiType.GetMethod("ChangeSelectedEntity", Any);
            // Switching straight from one junction to another only raises TTE's "unsaved" notice.
            if (Selected != Entity.Null) m.Invoke(Ui, new object[] { Entity.Null });
            if (e != Entity.Null) m.Invoke(Ui, new object[] { e });
        }
        public void HidePanel() { try { Call("CallSetMainPanelState", new { key = "", value = "0" }); } catch (Exception) { } }

        // TTE's own approach list for a node: edge, matching position and turn lane counts.
        public List<JObject> EdgeInfo(Entity node)
        {
            UiType.GetMethod("UpdateEdgeInfo", Any).Invoke(Ui, new object[] { node });
            var dict = (IDictionary)Member(Ui, "m_EdgeInfoDictionary");
            var rows = new List<JObject>();
            if (!dict.Contains(node)) return rows;
            foreach (var info in (IEnumerable)dict[node])
            {
                var t = info.GetType(); object F(string n) => t.GetField(n, Any)?.GetValue(info);
                var edge = (Entity)F("m_Edge"); var pos = F("m_Position"); var pt = pos.GetType();
                rows.Add(new JObject
                {
                    ["edge"] = NativeBuild.Id(edge),
                    ["position"] = new JObject { ["x"] = (float)pt.GetField("x").GetValue(pos), ["y"] = (float)pt.GetField("y").GetValue(pos), ["z"] = (float)pt.GetField("z").GetValue(pos) },
                    ["carLanes"] = new JObject { ["left"] = (int)F("m_CarLaneLeftCount"), ["straight"] = (int)F("m_CarLaneStraightCount"), ["right"] = (int)F("m_CarLaneRightCount"), ["uturn"] = (int)F("m_CarLaneUTurnCount") },
                    ["publicCarLanes"] = new JObject { ["left"] = (int)F("m_PublicCarLaneLeftCount"), ["straight"] = (int)F("m_PublicCarLaneStraightCount"), ["right"] = (int)F("m_PublicCarLaneRightCount"), ["uturn"] = (int)F("m_PublicCarLaneUTurnCount") },
                    ["trackLanes"] = (int)F("m_TrackLaneLeftCount") + (int)F("m_TrackLaneStraightCount") + (int)F("m_TrackLaneRightCount"),
                    ["pedestrianLanes"] = (int)F("m_PedestrianLaneStopLineCount") + (int)F("m_PedestrianLaneNonStopLineCount")
                });
            }
            return rows;
        }

        public object Component(EntityManager em, Entity e, Type t)
        {
            if (!em.HasComponent(e, ComponentType.ReadOnly(t))) return null;
            return typeof(EntityManager).GetMethods().First(m => m.Name == "GetComponentData" && m.IsGenericMethod && m.GetParameters().Length == 1)
                .MakeGenericMethod(t).Invoke(em, new object[] { e });
        }
        public List<object> Buffer(EntityManager em, Entity e, Type t)
        {
            var list = new List<object>();
            if (!em.HasComponent(e, ComponentType.ReadOnly(t))) return list;
            var buf = typeof(EntityManager).GetMethods().First(m => m.Name == "GetBuffer" && m.IsGenericMethod && m.GetParameters().Length == 2)
                .MakeGenericMethod(t).Invoke(em, new object[] { e, true });
            var bt = buf.GetType(); int n = (int)bt.GetProperty("Length").GetValue(buf); var item = bt.GetProperty("Item");
            for (int i = 0; i < n; i++) list.Add(item.GetValue(buf, new object[] { i }));
            return list;
        }
        public static object Member(object o, string name)
        {
            if (o == null) return null; var t = o.GetType();
            var f = t.GetField(name, Any); if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, Any); if (p != null) return p.GetValue(o);
            var m = t.GetMethod(name, Any, null, Type.EmptyTypes, null); return m?.Invoke(o, null);
        }
    }

    public sealed partial class Mod
    {
        private static readonly string[] TurnNames = { "left", "straight", "right", "uturn" };
        private static readonly string[] TurnFields = { "m_Left", "m_Straight", "m_Right", "m_UTurn" };

        private TteBridge RequireTte(World w) { var t = TteBridge.Open(w); if (t.Error != null) throw new InvalidOperationException(t.Error); return t; }

        private static Entity Node(JObject args) => new Entity { Index = RequiredInt(args, "index"), Version = RequiredInt(args, "version") };

        private JObject TteJunctionJson(World w, TteBridge tte, Entity node)
        {
            var em = w.EntityManager;
            if (!em.Exists(node) || !em.HasComponent<Game.Net.Node>(node)) throw new ArgumentException("node_not_found");
            var lights = tte.Component(em, node, tte.LightsType);
            var result = new JObject { ["node"] = NativeBuild.Id(node), ["position"] = Vector(em.GetComponentData<Game.Net.Node>(node).m_Position),
                ["signalled"] = em.HasComponent<Game.Net.TrafficLights>(node) };
            if (lights == null) result["pattern"] = "vanilla_default";
            else
            {
                result["pattern"] = TteBridge.Member(lights, "GetPatternOnly")?.ToString();
                result["sequential"] = JToken.FromObject(TteBridge.Member(lights, "m_UseSequentialPhases") ?? false);
                result["syncGroupId"] = JToken.FromObject(TteBridge.Member(lights, "m_SyncGroupId") ?? 0u);
                result["useSyncedCycle"] = JToken.FromObject(TteBridge.Member(lights, "m_UseSyncedCycle") ?? false);
                result["cycleOffsetSeconds"] = JToken.FromObject(TteBridge.Member(lights, "m_CycleOffsetSeconds") ?? (ushort)0);
                result["dynamicPhaseMode"] = JToken.FromObject(TteBridge.Member(lights, "m_DynamicPhaseMode") ?? 0);
            }
            var phases = tte.Buffer(em, node, tte.PhaseType);
            result["phases"] = new JArray(phases.Select((p, i) => new JObject { ["index"] = i, ["minDuration"] = JToken.FromObject(TteBridge.Member(p, "m_MinimumDuration")), ["maxDuration"] = JToken.FromObject(TteBridge.Member(p, "m_MaximumDuration")), ["options"] = TteBridge.Member(p, "m_Options")?.ToString() }));
            var masks = tte.Buffer(em, node, tte.MaskType).Select(m => JObject.FromObject(m)).ToList();
            var edges = tte.EdgeInfo(node);
            foreach (var e in edges)
            {
                var mask = masks.FirstOrDefault(m => (int)m["m_Edge"]["Index"] == (int)e["edge"]["index"] && (int)m["m_Edge"]["Version"] == (int)e["edge"]["version"]);
                var turns = new JObject();
                for (int i = 0; i < 4; i++)
                {
                    var sig = mask?["m_Car"]?[TurnFields[i]];
                    turns[TurnNames[i]] = new JObject { ["lanes"] = e["carLanes"][TurnNames[i]], ["go"] = Bits((int?)sig?["m_GoGroupMask"] ?? 0), ["yield"] = Bits((int?)sig?["m_YieldGroupMask"] ?? 0) };
                }
                e["turns"] = turns;
                var ped = mask?["m_PedestrianStopLine"];
                e["pedestrian"] = new JObject { ["go"] = Bits((int?)ped?["m_GoGroupMask"] ?? 0), ["yield"] = Bits((int?)ped?["m_YieldGroupMask"] ?? 0) };
                var p = e["position"]; var np = em.GetComponentData<Game.Net.Node>(node).m_Position;
                e["bearingDegrees"] = Math.Round((Math.Atan2((float)p["x"] - np.x, (float)p["z"] - np.z) * 180 / Math.PI + 360) % 360, 1);
            }
            result["approaches"] = new JArray(edges);
            if (lights != null && (string)result["pattern"] == "CustomPhase" && phases.Count == 0)
                result["warning"] = "custom_pattern_without_phases";
            result["unserved"] = Unserved(edges, masks.Select(m => (JObject)m).ToList());
            return result;
        }

        private static JArray Bits(int mask) => new JArray(Enumerable.Range(0, 16).Where(i => (mask & (1 << i)) != 0).Select(i => i + 1));

        // Approach turns that have car lanes but no Go or Yield in any phase stay red forever.
        private static JArray Unserved(List<JObject> edges, List<JObject> masks)
        {
            var rows = new JArray();
            foreach (var e in edges)
            {
                var mask = masks.FirstOrDefault(m => (int)m["m_Edge"]["Index"] == (int)e["edge"]["index"]);
                for (int i = 0; i < 4; i++)
                {
                    if ((int)e["carLanes"][TurnNames[i]] == 0) continue;
                    var sig = mask?["m_Car"]?[TurnFields[i]];
                    if (((int?)sig?["m_GoGroupMask"] ?? 0) == 0 && ((int?)sig?["m_YieldGroupMask"] ?? 0) == 0)
                        rows.Add(new JObject { ["edge"] = e["edge"], ["turn"] = TurnNames[i], ["lanes"] = e["carLanes"][TurnNames[i]] });
                }
            }
            return rows;
        }

        private JObject TteGetJunction(JObject args)
        {
            var w = RequireCity(); var tte = RequireTte(w); var node = Node(args);
            try { return TteJunctionJson(w, tte, node); } finally { tte.HidePanel(); }
        }

        private JObject TteListJunctions(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var tte = RequireTte(w); var area = AreaFilter.From(args, 10000);
            var rows = new JArray(); int custom = 0, empty = 0;
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly(tte.LightsType), ComponentType.ReadOnly<Game.Net.Node>()))
            using (var es = q.ToEntityArray(Unity.Collections.Allocator.Temp))
                foreach (var e in es.ToArray().OrderBy(e => e.Index))
                {
                    var pos = em.GetComponentData<Game.Net.Node>(e).m_Position; if (!area.Contains(pos)) continue;
                    var lights = tte.Component(em, e, tte.LightsType); string pattern = TteBridge.Member(lights, "GetPatternOnly")?.ToString();
                    int phases = tte.Buffer(em, e, tte.PhaseType).Count;
                    if (pattern == "CustomPhase") { ++custom; if (phases == 0) ++empty; }
                    rows.Add(new JObject { ["node"] = NativeBuild.Id(e), ["position"] = Vector(pos), ["pattern"] = pattern, ["phases"] = phases,
                        ["syncGroupId"] = JToken.FromObject(TteBridge.Member(lights, "m_SyncGroupId") ?? 0u), ["cycleOffsetSeconds"] = JToken.FromObject(TteBridge.Member(lights, "m_CycleOffsetSeconds") ?? (ushort)0),
                        ["warning"] = pattern == "CustomPhase" && phases == 0 ? "custom_pattern_without_phases" : null });
                }
            return new JObject { ["junctions"] = rows, ["customPhaseJunctions"] = custom, ["customWithoutPhases"] = empty };
        }

        private static readonly Dictionary<string, string> PatternValues = new Dictionary<string, string>
        { ["vanilla"] = "0", ["split_phasing"] = "1", ["protected_left_turns"] = "2", ["custom"] = "5" };

        private JObject TteSetPattern(JObject args)
        {
            var w = RequireCity(); RequireControl(); var tte = RequireTte(w); var node = Node(args);
            string pattern = (string)args["pattern"]; if (pattern == null || !PatternValues.ContainsKey(pattern)) throw new ArgumentException("pattern_must_be_one_of: " + string.Join(",", PatternValues.Keys));
            if (!w.EntityManager.HasComponent<Game.Net.TrafficLights>(node)) throw new ArgumentException("node_has_no_traffic_lights");
            try
            {
                var before = TteJunctionJson(w, tte, node);
                tte.Select(node);
                tte.Call("CallMainPanelUpdatePattern", new { type = "radio", isChecked = true, key = "pattern", value = PatternValues[pattern], label = "" });
                tte.Call("CallMainPanelSave", "");
                return new JObject { ["before"] = before, ["after"] = TteJunctionJson(w, tte, node) };
            }
            finally { tte.Select(Entity.Null); tte.HidePanel(); }
        }

        // phases: [{ minDuration, maxDuration?, movements: [{ edgeIndex, edgeVersion, go:[turns], yield:[turns], pedestrians:"go"|"yield" }] }]
        private JObject TteConfigureJunction(JObject args)
        {
            var w = RequireCity(); var tte = RequireTte(w); var node = Node(args); var em = w.EntityManager;
            bool preview = (bool?)args["previewOnly"] == true, allowUnserved = (bool?)args["allowUnserved"] == true;
            if (!preview) RequireControl();
            if (!em.HasComponent<Game.Net.TrafficLights>(node)) throw new ArgumentException("node_has_no_traffic_lights");
            var phases = args["phases"] as JArray; if (phases == null || phases.Count < 1 || phases.Count > 16) throw new ArgumentException("phases_must_be_1_to_16");
            try
            {
                var edges = tte.EdgeInfo(node);
                // Build one EdgeGroupMask per approach, starting from all-red.
                var masks = edges.ToDictionary(e => (int)e["edge"]["index"], e => new JObject
                {
                    ["m_Edge"] = new JObject { ["Index"] = e["edge"]["index"], ["Version"] = e["edge"]["version"] },
                    ["m_Position"] = e["position"], ["m_Options"] = 0,
                    ["m_Car"] = Turn(), ["m_PublicCar"] = Turn(), ["m_Track"] = Turn(),
                    ["m_PedestrianStopLine"] = Sig(), ["m_PedestrianNonStopLine"] = Sig(), ["m_Bicycle"] = Sig()
                });
                for (int p = 0; p < phases.Count; p++)
                {
                    int bit = 1 << p;
                    foreach (var mv in (phases[p]["movements"] as JArray) ?? new JArray())
                    {
                        int ei = RequiredInt((JObject)mv, "edgeIndex");
                        if (!masks.TryGetValue(ei, out var mask)) throw new ArgumentException("edge_not_an_approach_of_node: " + ei);
                        foreach (var kind in new[] { "go", "yield" })
                            foreach (var turn in (mv[kind] as JArray) ?? new JArray())
                            {
                                int ti = Array.IndexOf(TurnNames, (string)turn); if (ti < 0) throw new ArgumentException("turn_must_be_left_straight_right_uturn");
                                string field = kind == "go" ? "m_GoGroupMask" : "m_YieldGroupMask";
                                foreach (var group in new[] { "m_Car", "m_PublicCar" }) mask[group][TurnFields[ti]][field] = (int)mask[group][TurnFields[ti]][field] | bit;
                            }
                        string ped = (string)mv["pedestrians"];
                        if (ped == "go" || ped == "yield")
                        {
                            string field = ped == "go" ? "m_GoGroupMask" : "m_YieldGroupMask";
                            foreach (var group in new[] { "m_PedestrianStopLine", "m_PedestrianNonStopLine", "m_Bicycle" }) mask[group][field] = (int)mask[group][field] | bit;
                        }
                    }
                }
                var unserved = Unserved(edges, masks.Values.ToList());
                var plan = new JObject { ["node"] = NativeBuild.Id(node), ["approaches"] = new JArray(edges), ["masks"] = new JArray(masks.Values), ["unserved"] = unserved };
                if (unserved.Count > 0 && !allowUnserved) { plan["status"] = "rejected"; plan["error"] = "approach_turns_never_green"; return plan; }
                if (preview) { plan["status"] = "preview"; return plan; }

                tte.Select(node);
                tte.Call("CallMainPanelUpdatePattern", new { type = "radio", isChecked = true, key = "pattern", value = "5", label = "" });
                // Replace the phase list.
                int existing = tte.Buffer(em, node, tte.PhaseType).Count;
                for (int i = existing - 1; i >= 0; i--) tte.Call("CallRemoveCustomPhase", new { index = i });
                for (int p = 0; p < phases.Count; p++)
                {
                    tte.Call("CallAddCustomPhase", "");
                    int min = (int?)phases[p]["minDuration"] ?? 10, max = (int?)phases[p]["maxDuration"] ?? min;
                    if (min < 1 || min > 300 || max < min || max > 300) throw new ArgumentException("durations_must_be_1_to_300_and_max_ge_min");
                    tte.Call("CallUpdateCustomPhaseData", new { index = p, key = "MaximumDuration", value = (double)max });
                    tte.Call("CallUpdateCustomPhaseData", new { index = p, key = "MinimumDuration", value = (double)min });
                }
                var trace = new JArray();
                tte.Call("CallUpdateEdgeGroupMask", new JArray(masks.Values).ToString(Formatting.None));
                trace.Add(new JObject { ["step"] = "ui_update_edge_group_mask", ["buffer"] = MaskSnapshot(em, tte, node) });
                // TTE's handler builds entries for edges it has not seen yet from an empty template, which drops
                // the edge reference and position (CallUpdateEdgeGroupMask: new EdgeGroupMask(result, value) with
                // result = default). Write any missing or different approach masks directly.
                if (!MasksMatch(em, tte, node, masks.Values))
                {
                    WriteMasks(em, tte, node, masks.Values);
                    tte.UiType.GetMethod("UpdateEntity", TteAny).Invoke(tte.Ui, new object[] { true, true });
                    trace.Add(new JObject { ["step"] = "direct_write", ["buffer"] = MaskSnapshot(em, tte, node) });
                }
                tte.Call("CallSetPhaseMode", new { useSequentialPhases = true });
                tte.Call("CallMainPanelSave", "");
                trace.Add(new JObject { ["step"] = "saved", ["buffer"] = MaskSnapshot(em, tte, node) });
                var after = TteJunctionJson(w, tte, node);
                after["trace"] = trace;
                after["status"] = ((JArray)after["unserved"]).Count == 0 || allowUnserved ? "complete" : "complete_with_unserved_turns";
                return after;
            }
            finally { tte.Select(Entity.Null); tte.HidePanel(); }
        }

        private static readonly BindingFlags TteAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static JArray MaskSnapshot(EntityManager em, TteBridge tte, Entity node) =>
            new JArray(tte.Buffer(em, node, tte.MaskType).Select(m => { var j = JObject.FromObject(m); return new JObject { ["edge"] = j["m_Edge"], ["car"] = j["m_Car"] }; }));

        private static bool MasksMatch(EntityManager em, TteBridge tte, Entity node, IEnumerable<JObject> wanted)
        {
            var have = tte.Buffer(em, node, tte.MaskType).Select(m => JObject.FromObject(m)).ToList();
            foreach (var want in wanted)
            {
                var h = have.FirstOrDefault(m => (int)m["m_Edge"]["Index"] == (int)want["m_Edge"]["Index"] && (int)m["m_Edge"]["Version"] == (int)want["m_Edge"]["Version"]);
                if (h == null) return false;
                foreach (var g in new[] { "m_Car", "m_PublicCar", "m_PedestrianStopLine" })
                    if (!JToken.DeepEquals(h[g], want[g])) return false;
            }
            return true;
        }

        // Replace the approach masks with fully formed entries (edge, TTE's approach position, turn masks).
        private static void WriteMasks(EntityManager em, TteBridge tte, Entity node, IEnumerable<JObject> wanted)
        {
            var t = tte.MaskType;
            var buffer = typeof(EntityManager).GetMethods().First(m => m.Name == "GetBuffer" && m.IsGenericMethod && m.GetParameters().Length == 2)
                .MakeGenericMethod(t).Invoke(em, new object[] { node, false });
            var bt = buffer.GetType();
            bt.GetMethod("Clear").Invoke(buffer, null);
            var ctor = t.GetConstructor(new[] { typeof(Entity), typeof(float3) }) ?? throw new InvalidOperationException("tte_edge_group_mask_ctor_changed");
            foreach (var want in wanted)
            {
                var edge = new Entity { Index = (int)want["m_Edge"]["Index"], Version = (int)want["m_Edge"]["Version"] };
                var p = want["m_Position"]; var pos = new float3((float)p["x"], (float)p["y"], (float)p["z"]);
                object mask = ctor.Invoke(new object[] { edge, pos });
                foreach (var g in new[] { "m_Car", "m_PublicCar", "m_Track" })
                {
                    var gf = t.GetField(g); object turn = gf.GetValue(mask);
                    foreach (var tf in TurnFields)
                    {
                        var sf = turn.GetType().GetField(tf); object sig = sf.GetValue(turn);
                        SetSignal(sig, want[g][tf]); sf.SetValue(turn, sig);
                    }
                    gf.SetValue(mask, turn);
                }
                foreach (var g in new[] { "m_PedestrianStopLine", "m_PedestrianNonStopLine", "m_Bicycle" })
                {
                    var gf = t.GetField(g); object sig = gf.GetValue(mask); SetSignal(sig, want[g]); gf.SetValue(mask, sig);
                }
                bt.GetMethod("Add").Invoke(buffer, new[] { mask });
            }
        }

        private static void SetSignal(object sig, JToken value)
        {
            var st = sig.GetType();
            st.GetField("m_GoGroupMask").SetValue(sig, (ushort)(int)value["m_GoGroupMask"]);
            st.GetField("m_YieldGroupMask").SetValue(sig, (ushort)(int)value["m_YieldGroupMask"]);
        }

        private static JObject Sig() => new JObject { ["m_GoGroupMask"] = 0, ["m_YieldGroupMask"] = 0 };
        private static JObject Turn() => new JObject { ["m_Left"] = Sig(), ["m_Straight"] = Sig(), ["m_Right"] = Sig(), ["m_UTurn"] = Sig() };

        private JObject TteCreateSyncGroup(JObject args)
        {
            var w = RequireCity(); RequireControl(); var tte = RequireTte(w);
            string name = (string)args["name"] ?? "Bridge green wave"; int cycle = (int?)args["baseCycleSeconds"] ?? 60;
            if (cycle < 10 || cycle > 600) throw new ArgumentException("baseCycleSeconds_must_be_10_to_600");
            return JObject.Parse(tte.Call("CallCreateSyncGroup", new { groupName = name, baseCycleDuration = cycle }));
        }

        private JObject TteSetSync(JObject args)
        {
            var w = RequireCity(); RequireControl(); var tte = RequireTte(w); var node = Node(args);
            uint group = (uint)RequiredInt(args, "groupId"); int offset = (int?)args["offsetSeconds"] ?? 0;
            if (offset < 0 || offset > 999) throw new ArgumentException("offsetSeconds_must_be_0_to_999");
            var lights = tte.Component(w.EntityManager, node, tte.LightsType);
            if (group != 0 && (lights == null || TteBridge.Member(lights, "GetPatternOnly")?.ToString() != "CustomPhase" || tte.Buffer(w.EntityManager, node, tte.PhaseType).Count == 0))
                throw new InvalidOperationException("green_wave_requires_custom_phases: configure the junction first");
            try
            {
                tte.Select(node);
                var r = JObject.Parse(tte.Call("CallUpdateSyncSettings", new { syncGroupId = group, useSyncedCycle = group != 0, cycleOffsetSeconds = offset, syncPhaseIndex = 0 }));
                tte.Call("CallMainPanelSave", "");
                r["junction"] = TteJunctionJson(w, tte, node);
                return r;
            }
            finally { tte.Select(Entity.Null); tte.HidePanel(); }
        }
    }
}
