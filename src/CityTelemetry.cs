using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Economy;
using Game.Net;
using Game.Notifications;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Game.UI.InGame;
using Game.Vehicles;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitiesIIAgentBridge
{
    // Read-only city telemetry: notification icons, statistic history, vehicle queues and transit lines.
    // Data sources and the queue-head approach follow LancerComet/cities-skylines-2-mcp (Apache-2.0).
    public sealed partial class Mod
    {
        private JObject CityGetNotifications(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var ps = w.GetExistingSystemManaged<PrefabSystem>();
            var page = new QueryPage(args, 100, 1000); var area = AreaFilter.From(args, 10000);
            string filter = (string)args["type"];
            var rows = new JArray(); var counts = new SortedDictionary<string, int>(); int total = 0, all = 0;
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Icon>(), ComponentType.ReadOnly<PrefabRef>() }, None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() } }))
            using (var icons = q.ToEntityArray(Allocator.Temp))
                foreach (var e in icons.ToArray().OrderBy(e => e.Index))
                {
                    ++all;
                    var icon = em.GetComponentData<Icon>(e);
                    string type = ps.GetPrefabName(em.GetComponentData<PrefabRef>(e).m_Prefab);
                    if (!area.Contains(icon.m_Location) || (filter != null && (type == null || type.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))) continue;
                    counts[type ?? "<unknown>"] = counts.TryGetValue(type ?? "<unknown>", out int c) ? c + 1 : 1;
                    if (page.Contains(total++))
                    {
                        var row = NativeBuild.Id(e); row["type"] = type; row["priority"] = icon.m_Priority.ToString(); row["position"] = Vector(icon.m_Location);
                        if (em.HasComponent<Owner>(e))
                        {
                            var owner = em.GetComponentData<Owner>(e).m_Owner; var direct = owner;
                            // Bottleneck icons belong to a lane; walk up to the road, junction or building that owns it.
                            for (int i = 0; i < 4 && em.Exists(owner) && !em.HasComponent<Building>(owner) && !em.HasComponent<Edge>(owner) && !em.HasComponent<Node>(owner) && em.HasComponent<Owner>(owner); i++)
                                owner = em.GetComponentData<Owner>(owner).m_Owner;
                            var t = NativeBuild.Id(owner);
                            if (em.Exists(owner) && em.HasComponent<PrefabRef>(owner)) t["prefab"] = ps.GetPrefabName(em.GetComponentData<PrefabRef>(owner).m_Prefab);
                            if (owner != direct && em.Exists(direct) && em.HasComponent<PrefabRef>(direct)) { var via = NativeBuild.Id(direct); via["prefab"] = ps.GetPrefabName(em.GetComponentData<PrefabRef>(direct).m_Prefab); t["via"] = via; }
                            t["kind"] = !em.Exists(owner) ? "missing" : em.HasComponent<Building>(owner) ? "building" : em.HasComponent<Edge>(owner) ? "road" : em.HasComponent<Node>(owner) ? "junction" : "other";
                            row["target"] = t;
                        }
                        rows.Add(row);
                    }
                }
            var result = page.Result("notifications", rows, total);
            result["countsByType"] = JObject.FromObject(counts); result["iconsInCity"] = all;
            result["note"] = "In-world warning icons. target is the entity the icon belongs to; use building_get_details or inspect_entity on it.";
            return result;
        }

        private static readonly string[] resourceStatistics = { "Service", "Processing", "Office" };

        private JObject CityGetStatistics(JObject args)
        {
            var w = RequireCity(); var stats = w.GetExistingSystemManaged<CityStatisticsSystem>();
            string typeName = (string)args["type"];
            if (typeName == null)
            {
                // No type: list every statistic series the city currently records, with its latest value.
                var rows = new JArray();
                using (var keys = stats.GetLookup().GetKeyArray(Allocator.Temp))
                    foreach (var k in keys.ToArray().OrderBy(k => (int)k.type).ThenBy(k => k.parameter))
                    {
                        var row = new JObject { ["type"] = k.type.ToString(), ["parameter"] = k.parameter, ["current"] = stats.GetStatisticValueLong(k.type, k.parameter) };
                        AddResource(row, k.type, k.parameter); rows.Add(row);
                    }
                return new JObject { ["series"] = rows, ["note"] = "Pass type (and parameter for per-resource series) to get the history." };
            }
            if (!Enum.TryParse(typeName, true, out StatisticType type) || type == StatisticType.Invalid || type == StatisticType.Count)
                throw new ArgumentException("unknown_statistic_type: valid types are " + string.Join(", ", Enum.GetNames(typeof(StatisticType)).Where(n => n != "Invalid" && n != "Count")));
            int parameter = (int?)args["parameter"] ?? 0, samples = (int?)args["samples"] ?? 64;
            if (samples < 1 || samples > 512) throw new ArgumentException("samples_must_be_1_to_512");
            using (var data = stats.GetStatisticDataArrayLong(type, parameter))
            {
                int start = Math.Max(0, data.Length - samples); var values = new JArray(); var frames = new JArray();
                for (int i = start; i < data.Length; i++) { values.Add(data[i]); frames.Add(stats.GetSampleFrameIndex(i)); }
                var result = new JObject { ["type"] = type.ToString(), ["parameter"] = parameter, ["current"] = stats.GetStatisticValueLong(type, parameter),
                    ["recorded"] = stats.GetLookup().ContainsKey(new CityStatisticsSystem.StatisticsKey(type, parameter)),
                    ["totalSamples"] = data.Length, ["frames"] = frames, ["values"] = values,
                    ["note"] = "32 samples per in-game day (262144 simulation frames per day); frames[i] is the simulation frame of values[i]. recorded=false means the game keeps no series for this type/parameter." };
                AddResource(result, type, parameter);
                return result;
            }
        }

        private static void AddResource(JObject row, StatisticType type, int parameter)
        {
            string name = type.ToString();
            // Per-company-type series are keyed by resource index.
            if (resourceStatistics.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                try { row["resource"] = EconomyUtils.GetResource(parameter).ToString(); } catch { }
        }

        private const float StoppedSpeed = 0.5f;

        private JObject TrafficGetQueues(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var ps = w.GetExistingSystemManaged<PrefabSystem>();
            var area = AreaFilter.From(args, 10000); int limit = (int?)args["limit"] ?? 20;
            if (limit < 1 || limit > 200) throw new ArgumentException("limit_must_be_1_to_200");
            Entity[] cars;
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<CarCurrentLane>(), ComponentType.ReadOnly<Game.Objects.Transform>() }, None = new[] { ComponentType.ReadOnly<ParkedCar>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() } }))
            using (var es = q.ToEntityArray(Allocator.Temp)) cars = es.ToArray();
            int n = cars.Length; var indexOf = new Dictionary<Entity, int>(n);
            for (int i = 0; i < n; i++) indexOf[cars[i]] = i;
            var kind = new string[n]; var pos = new float3[n]; var stopped = new bool[n]; var blocker = new int[n]; var why = new string[n]; var location = new Entity[n];
            for (int i = 0; i < n; i++)
            {
                var car = cars[i]; kind[i] = VehicleKind(em, car); pos[i] = em.GetComponentData<Game.Objects.Transform>(car).m_Position;
                stopped[i] = !em.HasComponent<Game.Objects.Moving>(car) || math.length(em.GetComponentData<Game.Objects.Moving>(car).m_Velocity) < StoppedSpeed;
                blocker[i] = -1; why[i] = "None";
                if (em.HasComponent<Blocker>(car))
                {
                    var b = em.GetComponentData<Blocker>(car); why[i] = b.m_Type.ToString(); var by = b.m_Blocker;
                    // A blocking trailer stands for the vehicle pulling it.
                    if (by != Entity.Null && em.HasComponent<Controller>(by)) by = em.GetComponentData<Controller>(by).m_Controller;
                    if (by != car && by != Entity.Null && indexOf.TryGetValue(by, out int bi)) blocker[i] = bi;
                }
                var lane = em.GetComponentData<CarCurrentLane>(car).m_Lane;
                location[i] = lane != Entity.Null && em.HasComponent<Owner>(lane) ? em.GetComponentData<Owner>(lane).m_Owner : Entity.Null;
            }
            var loops = new List<List<int>>(); var head = QueueMath.ResolveHeads(blocker, stopped, loops);

            var byType = new SortedDictionary<string, int[]>(); var waitingFor = new SortedDictionary<string, int>(); var destinations = new SortedDictionary<string, int>();
            var queues = new Dictionary<Entity, (int queued, int heads, Dictionary<string, int> headWhy, int headIndex)>(); var loopQueued = new int[loops.Count];
            int inArea = 0, stoppedInArea = 0, stoppedAll = 0;
            for (int i = 0; i < n; i++)
            {
                if (stopped[i]) ++stoppedAll;
                if (area.Contains(pos[i]))
                {
                    ++inArea; if (!byType.ContainsKey(kind[i])) byType[kind[i]] = new int[2]; byType[kind[i]][0]++;
                    Bump(destinations, DestinationKind(em, cars[i]));
                    if (stopped[i]) { ++stoppedInArea; byType[kind[i]][1]++; Bump(waitingFor, why[i]); }
                }
                if (!stopped[i]) continue;
                int h = head[i];
                if (h <= -2) { loopQueued[-2 - h]++; continue; }
                var at = location[h];
                if (!queues.TryGetValue(at, out var g)) g = (0, 0, new Dictionary<string, int>(), h);
                g.queued++; if (h == i) { g.heads++; g.headWhy[why[i]] = g.headWhy.TryGetValue(why[i], out int c) ? c + 1 : 1; }
                queues[at] = g;
            }
            var queueRows = new JArray(queues.Select(p => (p.Key, p.Value, Pos: LocationPosition(em, p.Key, pos[p.Value.headIndex])))
                .Where(x => area.Contains(x.Pos)).OrderByDescending(x => x.Value.queued).Take(limit)
                .Select(x => new JObject { ["location"] = LocationJson(em, ps, x.Key, x.Pos), ["vehiclesQueued"] = x.Value.queued, ["queueHeads"] = x.Value.heads, ["headWaitingFor"] = JObject.FromObject(x.Value.headWhy) }));
            var loopRows = new JArray(Enumerable.Range(0, loops.Count)
                .Select(k => (k, Locs: loops[k].Select(m => location[m]).Distinct().Select(l => (l, LocationPosition(em, l, pos[loops[k][0]]))).ToList()))
                .Where(x => x.Locs.Any(l => area.Contains(l.Item2))).OrderByDescending(x => loopQueued[x.k]).Take(limit)
                .Select(x => new JObject { ["vehiclesInLoop"] = loops[x.k].Count, ["vehiclesQueued"] = loopQueued[x.k], ["locations"] = new JArray(x.Locs.Select(l => LocationJson(em, ps, l.l, l.Item2))) }));
            return new JObject
            {
                ["drivingVehicles"] = n, ["stoppedVehicles"] = stoppedAll, ["area"] = area.Active, ["vehiclesInArea"] = inArea, ["stoppedInArea"] = stoppedInArea,
                ["byType"] = new JObject(byType.Select(p => new JProperty(p.Key, new JObject { ["total"] = p.Value[0], ["stopped"] = p.Value[1] }))),
                ["stoppedWaitingFor"] = JObject.FromObject(waitingFor), ["destinations"] = JObject.FromObject(destinations),
                ["queues"] = queueRows, ["deadlocks"] = loopRows,
                ["note"] = "Driving road vehicles only; stopped = under 0.5 m/s. waitingFor is the game's car AI blocker: Continuing = vehicle ahead, Signal = red light, Crossing = crossing traffic, Oncoming = oncoming traffic when turning, Spawn = leaving a building, None = stopped by choice (loading, parking, at a stop). Each queue is located where its head vehicle is held up; deadlocks are loops of vehicles blocking each other."
            };
        }

        private static void Bump(SortedDictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out int v) ? v + 1 : 1;

        private static string VehicleKind(EntityManager em, Entity car)
        {
            if (em.HasComponent<Game.Vehicles.PersonalCar>(car)) return "personalCar";
            if (em.HasComponent<Game.Vehicles.Taxi>(car)) return "taxi";
            if (em.HasComponent<Game.Vehicles.PublicTransport>(car)) return "bus";
            if (em.HasComponent<Game.Vehicles.DeliveryTruck>(car)) return "deliveryTruck";
            if (em.HasComponent<Game.Vehicles.CargoTransport>(car)) return "cargoTruck";
            if (em.HasComponent<Game.Vehicles.GarbageTruck>(car)) return "garbageTruck";
            if (em.HasComponent<Game.Vehicles.PostVan>(car)) return "postVan";
            if (em.HasComponent<Game.Vehicles.PoliceCar>(car) || em.HasComponent<Game.Vehicles.FireEngine>(car) || em.HasComponent<Game.Vehicles.Ambulance>(car)
                || em.HasComponent<Game.Vehicles.Hearse>(car) || em.HasComponent<Game.Vehicles.PrisonerTransport>(car)) return "emergencyOrCare";
            if (em.HasComponent<Game.Vehicles.MaintenanceVehicle>(car) || em.HasComponent<Game.Vehicles.WorkVehicle>(car)) return "workVehicle";
            return "other";
        }

        private static string DestinationKind(EntityManager em, Entity car)
        {
            if (!em.HasComponent<Target>(car)) return "none";
            var t = em.GetComponentData<Target>(car).m_Target;
            // Companies and households target through the building they rent; lanes walk up to their owner.
            if (t != Entity.Null && em.Exists(t) && em.HasComponent<PropertyRenter>(t)) t = em.GetComponentData<PropertyRenter>(t).m_Property;
            for (int i = 0; i < 4 && t != Entity.Null && em.Exists(t); i++)
            {
                if (em.HasComponent<Game.Objects.OutsideConnection>(t) || em.HasComponent<Building>(t) || !em.HasComponent<Owner>(t)) break;
                t = em.GetComponentData<Owner>(t).m_Owner;
            }
            if (t == Entity.Null || !em.Exists(t)) return "none";
            if (em.HasComponent<Game.Objects.OutsideConnection>(t)) return "outsideCity";
            if (em.HasComponent<ResidentialProperty>(t)) return "residential";
            if (em.HasComponent<CommercialProperty>(t)) return "commercial";
            if (em.HasComponent<OfficeProperty>(t)) return "office";
            if (em.HasComponent<IndustrialProperty>(t) || em.HasComponent<ExtractorProperty>(t) || em.HasComponent<StorageProperty>(t)) return "industrial";
            if (em.HasComponent<Building>(t)) return "serviceOrOtherBuilding";
            if (em.HasComponent<Edge>(t) || em.HasComponent<Node>(t)) return "street";
            return "other";
        }

        private static float3 LocationPosition(EntityManager em, Entity e, float3 fallback)
        {
            if (e == Entity.Null || !em.Exists(e)) return fallback;
            if (em.HasComponent<Node>(e)) return em.GetComponentData<Node>(e).m_Position;
            if (em.HasComponent<Curve>(e)) return MathUtils.Position(em.GetComponentData<Curve>(e).m_Bezier, 0.5f);
            return fallback;
        }

        private static JObject LocationJson(EntityManager em, PrefabSystem ps, Entity e, float3 p)
        {
            bool live = e != Entity.Null && em.Exists(e);
            string kind = !live ? "unknown" : em.HasComponent<Node>(e) ? "junction" : em.HasComponent<Edge>(e) ? "road" : em.HasComponent<Building>(e) ? "building" : "other";
            var row = e == Entity.Null ? new JObject() : NativeBuild.Id(e);
            row["kind"] = kind; row["prefab"] = live && em.HasComponent<PrefabRef>(e) ? ps.GetPrefabName(em.GetComponentData<PrefabRef>(e).m_Prefab) : null;
            if (kind == "junction") row["trafficLights"] = em.HasComponent<TrafficLights>(e);
            row["position"] = new JObject { ["x"] = Math.Round(p.x, 1), ["z"] = Math.Round(p.z, 1) };
            return row;
        }

        private JObject TransitGetLines(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var ps = w.GetExistingSystemManaged<PrefabSystem>(); var names = w.GetExistingSystemManaged<NameSystem>();
            var page = new QueryPage(args, 50, 200); string typeFilter = (string)args["type"]; bool includeStops = (bool?)args["includeStops"] ?? false;
            var rows = new JArray(); int total = 0;
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Routes.Route>(), ComponentType.ReadOnly<Game.Routes.TransportLine>(), ComponentType.ReadOnly<Game.Routes.RouteWaypoint>(), ComponentType.ReadOnly<PrefabRef>() }, None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() } }))
            using (var lines = q.ToEntityArray(Allocator.Temp))
                foreach (var line in lines.ToArray().OrderBy(e => e.Index))
                {
                    // The transportation overview panel builds its rows with this helper, so the numbers match the UI.
                    var d = TransportUIUtils.BuildTransportLine(line, em, ps);
                    string type = d.type == TransportType.Subway ? "metro" : d.type.ToString().ToLowerInvariant();
                    if (typeFilter != null && !string.Equals(type, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!page.Contains(total++)) continue;
                    var row = NativeBuild.Id(line); row["name"] = Label(names, line);
                    row["number"] = em.HasComponent<Game.Routes.RouteNumber>(line) ? em.GetComponentData<Game.Routes.RouteNumber>(line).m_Number : 0;
                    row["type"] = type; row["isCargo"] = d.isCargo; row["prefab"] = ps.GetPrefabName(em.GetComponentData<PrefabRef>(line).m_Prefab);
                    row["color"] = "#" + d.color.r.ToString("X2") + d.color.g.ToString("X2") + d.color.b.ToString("X2");
                    row["active"] = d.active; row["visible"] = d.visible; row["schedule"] = ((RouteSchedule)d.schedule).ToString();
                    row["stops"] = d.stops; row["vehicles"] = d.vehicles; row["passengersOnBoard"] = d.cargo; row["usage"] = Math.Round(d.usage, 4); row["lengthMetres"] = Math.Round(d.length, 1);
                    int waiting = 0; var stopRows = new JArray(); var wps = em.GetBuffer<Game.Routes.RouteWaypoint>(line, true);
                    for (int i = 0; i < wps.Length; i++)
                    {
                        var wp = wps[i].m_Waypoint; if (!em.Exists(wp)) continue;
                        var stop = em.HasComponent<Game.Routes.Connected>(wp) ? em.GetComponentData<Game.Routes.Connected>(wp).m_Connected : Entity.Null;
                        bool isStop = stop != Entity.Null && em.Exists(stop) && em.HasComponent<Game.Routes.TransportStop>(stop);
                        var wpWaiting = em.HasComponent<Game.Routes.WaitingPassengers>(wp) ? em.GetComponentData<Game.Routes.WaitingPassengers>(wp) : default;
                        waiting += wpWaiting.m_Count;
                        if (!includeStops || !isStop) continue;
                        var s = NativeBuild.Id(stop); s["order"] = i + 1; s["name"] = Label(names, stop);
                        if (em.HasComponent<Game.Routes.Position>(wp)) s["position"] = Vector(em.GetComponentData<Game.Routes.Position>(wp).m_Position);
                        s["waitingPassengers"] = wpWaiting.m_Count; s["averageWaitingTimeRaw"] = wpWaiting.m_AverageWaitingTime;
                        stopRows.Add(s);
                    }
                    row["waitingPassengers"] = waiting; if (includeStops) row["stopList"] = stopRows;
                    rows.Add(row);
                }
            var result = page.Result("lines", rows, total);
            result["note"] = "Figures come from the game's transportation panel helper: usage = passengers on board / capacity. The game keeps no per-line ridership history; use city_get_statistics type=PassengerCountBus etc. for city-wide series. averageWaitingTimeRaw is the game's unscaled value.";
            return result;
        }

        private static readonly System.Reflection.FieldInfo nameType = typeof(NameSystem.Name).GetField("m_NameType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            nameId = typeof(NameSystem.Name).GetField("m_NameID", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            nameArgs = typeof(NameSystem.Name).GetField("m_NameArgs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        // The name the game UI shows: NameSystem.GetName (e.g. "Bus Line 1", "Autumn Street 12") resolved
        // against the active localization. GetRenderedLabelName only covers custom names and plain IDs.
        private static string Label(NameSystem names, Entity e)
        {
            try
            {
                object name = names.GetName(e);
                var type = (NameSystem.NameType)nameType.GetValue(name); string id = (string)nameId.GetValue(name);
                if (type == NameSystem.NameType.Custom) return id;
                var dictionary = Game.SceneFlow.GameManager.instance.localizationManager.activeDictionary;
                string Localize(string key) => key != null && dictionary.TryGetValue(key, out var value) ? value : key;
                string text = Localize(id);
                if (type == NameSystem.NameType.Formatted && nameArgs.GetValue(name) is string[] args)
                    for (int i = 0; i + 1 < args.Length; i += 2) text = text.Replace("{" + args[i] + "}", Localize(args[i + 1]));
                return text;
            }
            catch { try { return names.GetRenderedLabelName(e); } catch { return null; } }
        }
    }
}
