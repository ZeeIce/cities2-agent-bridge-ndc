using System;
using System.Collections.Generic;
using System.Linq;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;
using CarLane = Game.Net.CarLane;
using ParkingLane = Game.Net.ParkingLane;
using PedestrianLane = Game.Net.PedestrianLane;
using TrackLane = Game.Net.TrackLane;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        private sealed class RoadSample
        {
            public Entity Edge, Start, End; public string Prefab; public float Length; public float3 A, D;
            public int Forward, Backward, PublicOnly, Parking, Pedestrian, Track; public float SpeedLimit;
            public bool HighwayRules, Separated, Divided; public int Carriageways; public List<string> CompositionFlags = new List<string>(); public RoadClass Class;
            public float[] Flow; public float MinFlow; public float? Width;
            public JArray EdgeUpgrades = new JArray();
        }

        private static IEnumerable<Entity> RoadEdges(EntityManager em)
        {
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<Road>() }, None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Owner>() } }))
            using (var es = q.ToEntityArray(Allocator.Temp)) return es.ToArray().OrderBy(e => e.Index).ThenBy(e => e.Version).ToArray();
        }

        private static List<string> FlagNames<T>(T value) where T : Enum =>
            Enum.GetValues(typeof(T)).Cast<T>().Where(f => Convert.ToUInt64(f) != 0 && value.HasFlag(f)).Select(f => f.ToString()).ToList();

        private RoadSample SampleRoad(World w, Entity e)
        {
            var em = w.EntityManager; var s = new RoadSample { Edge = e };
            var edge = em.GetComponentData<Edge>(e); var curve = em.GetComponentData<Curve>(e);
            s.Start = edge.m_Start; s.End = edge.m_End; s.Length = curve.m_Length; s.A = curve.m_Bezier.a; s.D = curve.m_Bezier.d;
            var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab; s.Prefab = w.GetExistingSystemManaged<PrefabSystem>().GetPrefabName(prefab);
            if (em.HasComponent<RoadData>(prefab))
            {
                var rd = em.GetComponentData<RoadData>(prefab);
                s.HighwayRules = (rd.m_Flags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
                s.Separated = (rd.m_Flags & Game.Prefabs.RoadFlags.SeparatedCarriageways) != 0;
            }
            var carriageways = new HashSet<ushort>();
            if (em.HasBuffer<SubLane>(e))
                foreach (var sub in em.GetBuffer<SubLane>(e, true))
                {
                    var lane = sub.m_SubLane; if (!em.Exists(lane)) continue;
                    // Long edges split each physical lane into consecutive pieces along the edge
                    // (observed live: 4/4 car lanes reported on a 2/2 Medium Road). Count only the
                    // piece that touches the edge start so each physical lane is counted once.
                    if (em.HasComponent<EdgeLane>(lane)) { var d = em.GetComponentData<EdgeLane>(lane).m_EdgeDelta; if (math.min(d.x, d.y) > 0.001f) continue; }
                    if (em.HasComponent<ParkingLane>(lane)) { ++s.Parking; continue; }
                    if (em.HasComponent<PedestrianLane>(lane)) { ++s.Pedestrian; continue; }
                    if (em.HasComponent<TrackLane>(lane) && !em.HasComponent<CarLane>(lane)) { ++s.Track; continue; }
                    // Master lanes are virtual groups over their slave lanes; count physical lanes only.
                    if (!em.HasComponent<CarLane>(lane) || em.HasComponent<MasterLane>(lane) || !em.HasComponent<EdgeLane>(lane)) continue;
                    var car = em.GetComponentData<CarLane>(lane); var delta = em.GetComponentData<EdgeLane>(lane).m_EdgeDelta;
                    if (delta.y >= delta.x) ++s.Forward; else ++s.Backward;
                    if ((car.m_Flags & CarLaneFlags.PublicOnly) != 0) ++s.PublicOnly;
                    s.SpeedLimit = math.max(s.SpeedLimit, car.m_SpeedLimit);
                    carriageways.Add(car.m_CarriagewayGroup);
                }
            s.Carriageways = carriageways.Count;
            if (em.HasComponent<Composition>(e))
            {
                var composition = em.GetComponentData<Composition>(e).m_Edge;
                if (em.HasComponent<NetCompositionData>(composition))
                {
                    var data = em.GetComponentData<NetCompositionData>(composition);
                    s.Width = data.m_Width; s.CompositionFlags = FlagNames(data.m_Flags.m_General);
                }
            }
            // RoadData.SeparatedCarriageways is not set on Medium Road Divided (observed live), so a
            // median is recognised by the lanes themselves belonging to more than one carriageway.
            s.Divided = s.Separated || s.Carriageways >= 2;
            s.Class = TrafficMetrics.Classify(s.HighwayRules, s.Forward, s.Backward, s.Divided);
            var flow = NetUtils.GetTrafficFlowSpeed(em.GetComponentData<Road>(e));
            s.Flow = new[] { flow.x, flow.y, flow.z, flow.w }; s.MinFlow = TrafficMetrics.MinFlow(s.Flow);
            if (em.HasComponent<Upgraded>(e))
            {
                var up = em.GetComponentData<Upgraded>(e).m_Flags;
                foreach (var f in FlagNames(up.m_General)) s.EdgeUpgrades.Add("general:" + f);
                foreach (var f in FlagNames(up.m_Left)) s.EdgeUpgrades.Add("left:" + f);
                foreach (var f in FlagNames(up.m_Right)) s.EdgeUpgrades.Add("right:" + f);
            }
            return s;
        }

        private JObject RoadJson(RoadSample s)
        {
            var row = NativeBuild.Id(s.Edge);
            row["prefab"] = s.Prefab; row["class"] = TrafficMetrics.Name(s.Class); row["length"] = Math.Round(s.Length, 2);
            row["startNode"] = NativeBuild.Id(s.Start); row["endNode"] = NativeBuild.Id(s.End);
            row["start"] = Vector(s.A); row["end"] = Vector(s.D);
            row["lanes"] = new JObject { ["carForward"] = s.Forward, ["carBackward"] = s.Backward, ["publicTransportOnly"] = s.PublicOnly, ["parking"] = s.Parking, ["pedestrian"] = s.Pedestrian, ["trackOnly"] = s.Track };
            row["speedLimitNative"] = Math.Round(s.SpeedLimit, 3);
            row["highwayRules"] = s.HighwayRules; row["separatedCarriageways"] = s.Separated;
            row["carriageways"] = s.Carriageways; row["divided"] = s.Divided; row["compositionFlags"] = new JArray(s.CompositionFlags);
            row["widthMetres"] = s.Width == null ? null : (JToken)Math.Round(s.Width.Value, 2);
            row["flowSpeedNative"] = new JArray(s.Flow.Select(f => (JToken)Math.Round(f, 4)));
            row["minFlowSpeed"] = s.MinFlow < 0 ? null : (JToken)Math.Round(s.MinFlow, 4);
            row["flowStatus"] = TrafficMetrics.FlowStatus(s.MinFlow);
            row["upgrades"] = s.EdgeUpgrades;
            return row;
        }

        private JObject TrafficGetNetwork(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var page = new QueryPage(args, 256, 1024); var area = AreaFilter.From(args, 2000);
            if (!area.Active) throw new ArgumentException("area_required: x,z,radius<=2000 or bbox");
            var classes = StringSet(args["class"]);
            var rows = new JArray(); int total = 0;
            var lengthByClass = new Dictionary<string, double>(); var lengthByStatus = new Dictionary<string, double>();
            foreach (var e in RoadEdges(em))
            {
                var curve = em.GetComponentData<Curve>(e);
                if (!area.Touches(curve.m_Bezier.a, curve.m_Bezier.d, curve.m_Length * 0.5f)) continue;
                var s = SampleRoad(w, e); string cls = TrafficMetrics.Name(s.Class);
                if (classes != null && !classes.Contains(cls)) continue;
                lengthByClass[cls] = (lengthByClass.TryGetValue(cls, out var a) ? a : 0) + s.Length;
                string status = TrafficMetrics.FlowStatus(s.MinFlow);
                lengthByStatus[status] = (lengthByStatus.TryGetValue(status, out var b) ? b : 0) + s.Length;
                if (page.Contains(total)) rows.Add(RoadJson(s));
                ++total;
            }
            var result = page.Result("edges", rows, total);
            result["lengthByClassMetres"] = new JObject(lengthByClass.OrderBy(k => k.Key).Select(k => new JProperty(k.Key, Math.Round(k.Value, 1))));
            result["lengthByFlowStatusMetres"] = new JObject(lengthByStatus.OrderBy(k => k.Key).Select(k => new JProperty(k.Key, Math.Round(k.Value, 1))));
            result["measurement"] = new JObject { ["flowSpeedNative"] = "NetUtils.GetTrafficFlowSpeed: native ratio per sampled period, 1 = free flow", ["flowStatus"] = "bridge-derived banding of minFlowSpeed", ["class"] = "bridge-derived from lane counts and RoadData flags" };
            result["citySession"] = citySession;
            return result;
        }

        private static string Movement(CarLaneFlags f)
        {
            if ((f & (CarLaneFlags.UTurnLeft | CarLaneFlags.UTurnRight)) != 0) return "uturn";
            if ((f & CarLaneFlags.TurnLeft) != 0) return "left";
            if ((f & CarLaneFlags.TurnRight) != 0) return "right";
            if ((f & CarLaneFlags.GentleTurnLeft) != 0) return "gentle_left";
            if ((f & CarLaneFlags.GentleTurnRight) != 0) return "gentle_right";
            if ((f & CarLaneFlags.Forward) != 0) return "straight";
            return "unclassified";
        }

        private JObject Intersection(World w, Entity node, bool includeLanes)
        {
            var em = w.EntityManager;
            if (!em.Exists(node) || !em.HasComponent<Game.Net.Node>(node)) throw new ArgumentException("live_network_node_required");
            var result = NativeBuild.Id(node); result["position"] = Vector(em.GetComponentData<Game.Net.Node>(node).m_Position);
            var byIndex = new Dictionary<int, Entity>(); var edges = new JArray(); var classes = new List<RoadClass>();
            if (em.HasBuffer<ConnectedEdge>(node))
                foreach (var c in em.GetBuffer<ConnectedEdge>(node, true))
                {
                    var e = c.m_Edge; if (!em.Exists(e) || em.HasComponent<Temp>(e) || em.HasComponent<Deleted>(e)) continue;
                    byIndex[e.Index] = e;
                    if (em.HasComponent<Road>(e)) { var s = SampleRoad(w, e); classes.Add(s.Class); edges.Add(RoadJson(s)); }
                    else { var row = NativeBuild.Id(e); row["prefab"] = em.HasComponent<PrefabRef>(e) ? w.GetExistingSystemManaged<PrefabSystem>().GetPrefabName(em.GetComponentData<PrefabRef>(e).m_Prefab) : null; row["class"] = "non_road"; edges.Add(row); }
                }
            result["edges"] = edges; result["roadEdgeCount"] = classes.Count;
            int gap = 0; for (int i = 0; i < classes.Count; i++) for (int j = i + 1; j < classes.Count; j++) gap = Math.Max(gap, TrafficMetrics.HierarchyGap(classes[i], classes[j]));
            result["hierarchyGap"] = gap; result["hierarchySeverity"] = TrafficMetrics.GapSeverity(gap);
            if (em.HasComponent<TrafficLights>(node))
            {
                var t = em.GetComponentData<TrafficLights>(node);
                result["trafficLights"] = new JObject { ["state"] = t.m_State.ToString(), ["flags"] = t.m_Flags.ToString(), ["signalGroups"] = t.m_SignalGroupCount, ["currentGroup"] = t.m_CurrentSignalGroup, ["nextGroup"] = t.m_NextSignalGroup, ["timer"] = t.m_Timer };
            }
            else result["trafficLights"] = null;
            if (em.HasComponent<Upgraded>(node))
            {
                var up = em.GetComponentData<Upgraded>(node).m_Flags;
                result["nodeUpgrades"] = new JArray(FlagNames(up.m_General).Select(f => "general:" + f).Concat(FlagNames(up.m_Left).Select(f => "left:" + f)).Concat(FlagNames(up.m_Right).Select(f => "right:" + f)));
            }
            else result["nodeUpgrades"] = new JArray();
            // Realised connections: node sub-lanes join an incoming edge lane to an outgoing one.
            var movements = new Dictionary<string, JObject>(); int unresolved = 0, carLanes = 0, crossings = 0; var lanes = new JArray();
            if (em.HasBuffer<SubLane>(node))
                foreach (var sub in em.GetBuffer<SubLane>(node, true))
                {
                    var laneEntity = sub.m_SubLane; if (!em.Exists(laneEntity)) continue;
                    if (em.HasComponent<PedestrianLane>(laneEntity)) { ++crossings; continue; }
                    if (!em.HasComponent<CarLane>(laneEntity) || em.HasComponent<MasterLane>(laneEntity) || !em.HasComponent<Lane>(laneEntity)) continue;
                    ++carLanes;
                    var lane = em.GetComponentData<Lane>(laneEntity); var car = em.GetComponentData<CarLane>(laneEntity);
                    bool fromKnown = byIndex.TryGetValue(lane.m_StartNode.GetOwnerIndex(), out var from), toKnown = byIndex.TryGetValue(lane.m_EndNode.GetOwnerIndex(), out var to);
                    if (!fromKnown || !toKnown) ++unresolved;
                    string movement = Movement(car.m_Flags);
                    var controls = FlagNames(car.m_Flags & (CarLaneFlags.Yield | CarLaneFlags.Stop | CarLaneFlags.TrafficLights | CarLaneFlags.RightOfWay | CarLaneFlags.Unsafe | CarLaneFlags.Forbidden | CarLaneFlags.PublicOnly | CarLaneFlags.Roundabout));
                    string key = (fromKnown ? from.Index + ":" + from.Version : "?") + ">" + (toKnown ? to.Index + ":" + to.Version : "?") + ">" + movement;
                    if (!movements.TryGetValue(key, out var m))
                        movements[key] = m = new JObject { ["fromEdge"] = fromKnown ? NativeBuild.Id(from) : null, ["toEdge"] = toKnown ? NativeBuild.Id(to) : null, ["movement"] = movement, ["laneConnections"] = 0, ["controls"] = new JArray() };
                    m["laneConnections"] = (int)m["laneConnections"] + 1;
                    foreach (var c in controls) if (!((JArray)m["controls"]).Any(x => (string)x == c)) ((JArray)m["controls"]).Add(c);
                    if (includeLanes && lanes.Count < 256)
                        lanes.Add(new JObject { ["lane"] = NativeBuild.Id(laneEntity), ["fromEdge"] = fromKnown ? NativeBuild.Id(from) : null, ["fromLaneIndex"] = lane.m_StartNode.GetLaneIndex(), ["toEdge"] = toKnown ? NativeBuild.Id(to) : null, ["toLaneIndex"] = lane.m_EndNode.GetLaneIndex(), ["movement"] = movement, ["flags"] = car.m_Flags.ToString(), ["speedLimitNative"] = Math.Round(car.m_SpeedLimit, 3) });
                }
            result["movements"] = new JArray(movements.Values); result["carConnectionLanes"] = carLanes; result["pedestrianLanes"] = crossings;
            result["unresolvedConnections"] = unresolved;
            if (includeLanes) result["laneConnections"] = lanes;
            result["measurement"] = new JObject { ["movements"] = "Native node sub-lanes (CarLane flags). Edge ownership resolved via PathNode owner index against this node's connected edges; unresolved counts are reported, not guessed." };
            return result;
        }

        private JObject TrafficGetIntersection(JObject args)
        {
            var w = RequireCity(); var node = new Entity { Index = RequiredInt(args, "index"), Version = RequiredInt(args, "version") };
            var result = Intersection(w, node, (bool?)args["includeLanes"] == true);
            result["citySession"] = citySession; return result;
        }

        private JObject TrafficGetLaneRules(JObject args)
        {
            var w = RequireCity(); var node = new Entity { Index = RequiredInt(args, "index"), Version = RequiredInt(args, "version") };
            var intersection = Intersection(w, node, true);
            return new JObject
            {
                ["node"] = NativeBuild.Id(node),
                ["vanilla"] = new JObject { ["nodeUpgrades"] = intersection["nodeUpgrades"], ["edgeUpgrades"] = new JArray(((JArray)intersection["edges"]).OfType<JObject>().Select(e => new JObject { ["edge"] = new JObject { ["index"] = e["index"], ["version"] = e["version"] }, ["upgrades"] = e["upgrades"] ?? new JArray() })), ["trafficLights"] = intersection["trafficLights"] },
                ["realisedConnections"] = intersection["laneConnections"], ["movements"] = intersection["movements"],
                ["trafficMod"] = TrafficModNodeData(w, node),
                ["writeSupport"] = "read_only: no supported write path is implemented; see docs/IMPLEMENTATION-CHECKLIST.md",
                ["citySession"] = citySession
            };
        }

        private JObject TrafficFindBottlenecks(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var area = AreaFilter.From(args, 14000);
            int limit = Math.Min(Math.Max((int?)args["limit"] ?? 20, 1), 100);
            float threshold = args["maxFlowSpeed"] == null ? TrafficMetrics.CongestedBelow : RequiredFloat(args, "maxFlowSpeed");
            var edges = new List<(double Score, RoadSample S)>(); var nodeScores = new Dictionary<Entity, (double Sum, int Count)>(); int sampled = 0, noData = 0;
            foreach (var e in RoadEdges(em))
            {
                var curve = em.GetComponentData<Curve>(e); if (!area.Touches(curve.m_Bezier.a, curve.m_Bezier.d, curve.m_Length * 0.5f)) continue;
                var s = SampleRoad(w, e); ++sampled;
                if (s.MinFlow < 0) { ++noData; continue; }
                foreach (var n in new[] { s.Start, s.End }) { nodeScores.TryGetValue(n, out var v); nodeScores[n] = (v.Sum + (1 - s.MinFlow), v.Count + 1); }
                if (s.MinFlow < threshold) edges.Add((TrafficMetrics.BottleneckScore(s.MinFlow, s.Forward + s.Backward, s.Length), s));
            }
            var nodes = nodeScores.Where(kv => kv.Value.Count >= 3 && em.Exists(kv.Key))
                .Select(kv => (Node: kv.Key, Mean: kv.Value.Sum / kv.Value.Count, kv.Value.Count)).Where(n => 1 - n.Mean < threshold)
                .OrderByDescending(n => n.Mean).Take(limit)
                .Select(n => { var r = NativeBuild.Id(n.Node); r["position"] = em.HasComponent<Game.Net.Node>(n.Node) ? Vector(em.GetComponentData<Game.Net.Node>(n.Node).m_Position) : null; r["meanApproachCongestion"] = Math.Round(n.Mean, 4); r["roadEdges"] = n.Count; return r; });
            return new JObject
            {
                ["edges"] = new JArray(edges.OrderByDescending(x => x.Score).Take(limit).Select(x => { var r = RoadJson(x.S); r["rankScore"] = Math.Round(x.Score, 4); return r; })),
                ["edgesBelowThreshold"] = edges.Count, ["congestedIntersections"] = new JArray(nodes),
                ["sampledEdges"] = sampled, ["edgesWithoutFlowData"] = noData, ["threshold"] = threshold,
                ["cityAverages"] = CityTrafficAverages(w),
                ["measurement"] = "Edges ranked by (1 - minFlowSpeed) x sqrt(car lanes) x length factor; the score orders candidates and is not itself a measurement. Intersection congestion is the mean of (1 - minFlowSpeed) over approaching road edges.",
                ["citySession"] = citySession
            };
        }

        private static JObject CityTrafficAverages(World w)
        {
            var t = w.GetExistingSystemManaged<TrafficFlowSystem>();
            if (t == null) return new JObject { ["available"] = false };
            return new JObject { ["available"] = true, ["averageTrafficFlow"] = t.cityAverageTrafficFlow, ["averageTrafficVolume"] = t.cityAverageTrafficVolume, ["source"] = "TrafficFlowSystem city averages (native, cached by the game)" };
        }

        private (JObject Result, List<(JObject Node, string Classes, int Gap)> Gaps, List<(JObject Id, string Prefab, float MinFlow)> Severe) TrafficAnalysis(World w, AreaFilter area, int listLimit)
        {
            var em = w.EntityManager; var samples = new Dictionary<Entity, RoadSample>();
            foreach (var e in RoadEdges(em))
            {
                var curve = em.GetComponentData<Curve>(e); if (!area.Touches(curve.m_Bezier.a, curve.m_Bezier.d, curve.m_Length * 0.5f)) continue;
                samples[e] = SampleRoad(w, e);
            }
            var nodeEdges = new Dictionary<Entity, List<RoadSample>>();
            foreach (var s in samples.Values) foreach (var n in new[] { s.Start, s.End }) { if (!nodeEdges.TryGetValue(n, out var l)) nodeEdges[n] = l = new List<RoadSample>(); l.Add(s); }
            var gaps = new List<(JObject, string, int)>(); var deadEnds = new JArray(); var shortLinks = new JArray(); int intersections = 0, signalled = 0;
            foreach (var kv in nodeEdges)
            {
                var list = kv.Value; if (list.Count >= 3) ++intersections;
                if (em.HasComponent<TrafficLights>(kv.Key)) ++signalled;
                if (list.Count == 1 && !em.HasComponent<Game.Net.OutsideConnection>(kv.Key) && deadEnds.Count < listLimit) { var r = NativeBuild.Id(kv.Key); r["edge"] = NativeBuild.Id(list[0].Edge); r["class"] = TrafficMetrics.Name(list[0].Class); deadEnds.Add(r); }
                int gap = 0; for (int i = 0; i < list.Count; i++) for (int j = i + 1; j < list.Count; j++) gap = Math.Max(gap, TrafficMetrics.HierarchyGap(list[i].Class, list[j].Class));
                if (gap >= 2) gaps.Add((NativeBuild.Id(kv.Key), string.Join(",", list.Select(s => TrafficMetrics.Name(s.Class)).Distinct().OrderBy(x => x)), gap));
            }
            foreach (var s in samples.Values)
                if (s.Length < 40 && nodeEdges.TryGetValue(s.Start, out var a) && a.Count >= 3 && nodeEdges.TryGetValue(s.End, out var b) && b.Count >= 3 && shortLinks.Count < listLimit)
                { var r = NativeBuild.Id(s.Edge); r["length"] = Math.Round(s.Length, 1); r["class"] = TrafficMetrics.Name(s.Class); shortLinks.Add(r); }
            var severe = samples.Values.Where(s => s.MinFlow >= 0 && s.MinFlow < TrafficMetrics.SevereBelow).Select(s => (NativeBuild.Id(s.Edge), s.Prefab, s.MinFlow)).ToList();
            double totalKm = samples.Values.Sum(s => s.Length) / 1000.0;
            var classLength = samples.Values.GroupBy(s => TrafficMetrics.Name(s.Class)).ToDictionary(g => g.Key, g => Math.Round(g.Sum(s => s.Length), 1));
            var result = new JObject
            {
                ["roadEdges"] = samples.Count, ["roadKm"] = Math.Round(totalKm, 3),
                ["lengthByClassMetres"] = new JObject(classLength.OrderBy(k => k.Key).Select(k => new JProperty(k.Key, k.Value))),
                ["intersections"] = intersections, ["signalledNodes"] = signalled,
                ["intersectionsPerKm"] = totalKm > 0 ? Math.Round(intersections / totalKm, 3) : 0,
                ["hierarchyGaps"] = new JArray(gaps.OrderByDescending(g => g.Item3).Take(listLimit).Select(g => new JObject { ["node"] = g.Item1, ["classes"] = g.Item2, ["gap"] = g.Item3, ["severity"] = TrafficMetrics.GapSeverity(g.Item3) })),
                ["hierarchyGapTotal"] = gaps.Count,
                ["deadEnds"] = deadEnds, ["shortLinksBetweenIntersections"] = shortLinks,
                ["severeSegments"] = severe.Count, ["cityAverages"] = CityTrafficAverages(w),
                ["notes"] = new JArray("Road class is derived from measured lanes and RoadData flags; check prefab names where a custom road is ambiguous.",
                    "Dead ends exclude outside connections; cul-de-sacs in local grids are not necessarily problems.",
                    "Short links are < 40 m between two intersections and can cause queue spill-back.")
            };
            return (result, gaps, severe);
        }

        private JObject TrafficAnalyze(JObject args)
        {
            var w = RequireCity(); var area = AreaFilter.From(args, 14000);
            var (result, gaps, severe) = TrafficAnalysis(w, area, Math.Min(Math.Max((int?)args["listLimit"] ?? 25, 1), 100));
            var facts = new CityFacts { SevereEdges = severe, HierarchyGaps = gaps };
            result["recommendations"] = new JArray(AnalysisRules.Evaluate(facts, "traffic").Select(r => r.ToJson()));
            result["citySession"] = citySession;
            return result;
        }
    }
}
