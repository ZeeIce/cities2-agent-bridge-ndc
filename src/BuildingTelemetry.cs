using System;
using System.Collections.Generic;
using System.Linq;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.UI;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        // Measurements collected per building. Capacity fields stay zero when the building has
        // no such capacity; "measured" lists only what this building actually supports.
        private sealed class BuildingSample
        {
            public Entity Entity; public string Prefab, Category; public float3 Position; public Entity District;
            public int? Level; public int? Condition; public bool Abandoned, UnderConstruction, SubBuilding;
            public float? Efficiency; public float? ServiceUsage;
            public int Properties, Households, Residents, Companies, Employees, MaxWorkers, Students, StudentCapacity, Patients, PatientCapacity;
            public long RentTotal; public int RentCount; public int? DesignUpkeep;
            public JArray Issues = new JArray(); public JArray Measured = new JArray();
        }

        private static string BuildingCategoryOf(EntityManager em, Entity e, Entity prefab) =>
            BuildingMetrics.Category(em.HasComponent<ResidentialProperty>(e), em.HasComponent<CommercialProperty>(e),
                em.HasComponent<IndustrialProperty>(e), em.HasComponent<OfficeProperty>(e),
                prefab != Entity.Null && em.HasComponent<ServiceObjectData>(prefab));

        private static T CombinedPrefabData<T>(EntityManager em, Entity building, Entity prefab, Func<T, T, T> combine) where T : unmanaged, IComponentData
        {
            // Capacities stack with installed upgrades exactly as the prefab data's ICombineData does.
            T value = em.HasComponent<T>(prefab) ? em.GetComponentData<T>(prefab) : default;
            if (em.HasBuffer<InstalledUpgrade>(building))
                foreach (var upgrade in em.GetBuffer<InstalledUpgrade>(building, true))
                    if (em.Exists(upgrade.m_Upgrade) && em.HasComponent<PrefabRef>(upgrade.m_Upgrade))
                    {
                        var up = em.GetComponentData<PrefabRef>(upgrade.m_Upgrade).m_Prefab;
                        if (em.HasComponent<T>(up)) value = combine(value, em.GetComponentData<T>(up));
                    }
            return value;
        }

        private BuildingSample SampleBuilding(World w, Entity e, bool cheapOnly = false)
        {
            var em = w.EntityManager; var s = new BuildingSample { Entity = e };
            Entity prefab = em.HasComponent<PrefabRef>(e) ? em.GetComponentData<PrefabRef>(e).m_Prefab : Entity.Null;
            s.Prefab = prefab == Entity.Null ? null : w.GetExistingSystemManaged<PrefabSystem>().GetPrefabName(prefab);
            s.Category = BuildingCategoryOf(em, e, prefab);
            if (em.HasComponent<Game.Objects.Transform>(e)) s.Position = em.GetComponentData<Game.Objects.Transform>(e).m_Position;
            if (em.HasComponent<Game.Areas.CurrentDistrict>(e)) s.District = em.GetComponentData<Game.Areas.CurrentDistrict>(e).m_District;
            s.SubBuilding = em.HasComponent<Owner>(e);
            if (cheapOnly) return s;
            if (prefab != Entity.Null && em.HasComponent<SpawnableBuildingData>(prefab)) { s.Level = em.GetComponentData<SpawnableBuildingData>(prefab).m_Level; s.Measured.Add("level"); }
            if (em.HasComponent<BuildingCondition>(e)) { s.Condition = em.GetComponentData<BuildingCondition>(e).m_Condition; s.Measured.Add("condition"); }
            s.Abandoned = em.HasComponent<Abandoned>(e); s.UnderConstruction = em.HasComponent<Game.Objects.UnderConstruction>(e);
            if (em.HasBuffer<Efficiency>(e)) { s.Efficiency = BuildingUtils.GetEfficiency(em.GetBuffer<Efficiency>(e, true)); s.Measured.Add("efficiency"); }
            if (em.HasComponent<ServiceUsage>(e)) { s.ServiceUsage = em.GetComponentData<ServiceUsage>(e).m_Usage; s.Measured.Add("serviceUsage"); }
            if (prefab != Entity.Null && em.HasComponent<ConsumptionData>(prefab)) { s.DesignUpkeep = em.GetComponentData<ConsumptionData>(prefab).m_Upkeep; s.Measured.Add("designUpkeep"); }
            if (prefab != Entity.Null && em.HasComponent<BuildingPropertyData>(prefab))
            {
                s.Properties = em.GetComponentData<BuildingPropertyData>(prefab).m_ResidentialProperties;
                if (s.Properties > 0) s.Measured.Add("residentialProperties");
            }
            if (em.HasBuffer<Renter>(e))
            {
                s.Measured.Add("renters");
                foreach (var r in em.GetBuffer<Renter>(e, true))
                {
                    var renter = r.m_Renter; if (!em.Exists(renter)) continue;
                    if (em.HasComponent<PropertyRenter>(renter)) { s.RentTotal += em.GetComponentData<PropertyRenter>(renter).m_Rent; ++s.RentCount; }
                    if (em.HasComponent<Game.Citizens.Household>(renter))
                    {
                        ++s.Households;
                        if (em.HasBuffer<Game.Citizens.HouseholdCitizen>(renter)) s.Residents += em.GetBuffer<Game.Citizens.HouseholdCitizen>(renter, true).Length;
                    }
                    else if (em.HasComponent<Game.Companies.CompanyData>(renter))
                    {
                        ++s.Companies;
                        if (em.HasComponent<Game.Companies.WorkProvider>(renter)) s.MaxWorkers += em.GetComponentData<Game.Companies.WorkProvider>(renter).m_MaxWorkers;
                        if (em.HasBuffer<Game.Companies.Employee>(renter)) s.Employees += em.GetBuffer<Game.Companies.Employee>(renter, true).Length;
                    }
                }
            }
            // City service buildings employ directly rather than through renter companies.
            if (em.HasComponent<Game.Companies.WorkProvider>(e)) { s.MaxWorkers += em.GetComponentData<Game.Companies.WorkProvider>(e).m_MaxWorkers; s.Measured.Add("workplaces"); }
            if (em.HasBuffer<Game.Companies.Employee>(e)) s.Employees += em.GetBuffer<Game.Companies.Employee>(e, true).Length;
            if (prefab != Entity.Null && em.HasComponent<SchoolData>(prefab))
            {
                s.StudentCapacity = CombinedPrefabData<SchoolData>(em, e, prefab, (a, b) => { a.Combine(b); return a; }).m_StudentCapacity;
                s.Students = em.HasBuffer<Student>(e) ? em.GetBuffer<Student>(e, true).Length : 0; s.Measured.Add("students");
            }
            if (prefab != Entity.Null && em.HasComponent<HospitalData>(prefab))
            {
                s.PatientCapacity = CombinedPrefabData<HospitalData>(em, e, prefab, (a, b) => { a.Combine(b); return a; }).m_PatientCapacity;
                s.Patients = em.HasBuffer<Patient>(e) ? em.GetBuffer<Patient>(e, true).Length : 0; s.Measured.Add("patients");
            }
            foreach (var issue in Shortages(em, e)) s.Issues.Add((string)issue["type"] + "_shortage");
            if (prefab != Entity.Null && em.HasComponent<BuildingData>(prefab) && (em.GetComponentData<BuildingData>(prefab).m_Flags & Game.Prefabs.BuildingFlags.RequireRoad) != 0)
            {
                var road = em.GetComponentData<Building>(e).m_RoadEdge;
                if (road == Entity.Null || !em.Exists(road)) s.Issues.Add("no_road_connection");
            }
            if (s.Abandoned) s.Issues.Add("abandoned");
            if (s.Efficiency != null && s.Efficiency < 0.5f && !s.UnderConstruction) s.Issues.Add("low_efficiency");
            return s;
        }

        private JObject DistrictRef(World w, Entity district)
        {
            if (district == Entity.Null || !w.EntityManager.Exists(district)) return null;
            var row = NativeBuild.Id(district);
            try { row["name"] = w.GetExistingSystemManaged<NameSystem>()?.GetRenderedLabelName(district); } catch (Exception) { row["name"] = null; }
            return row;
        }

        private JObject BuildingJson(World w, BuildingSample s, bool detailed)
        {
            var row = NativeBuild.Id(s.Entity);
            row["prefab"] = s.Prefab; row["category"] = s.Category; row["position"] = Vector(s.Position);
            row["district"] = DistrictRef(w, s.District);
            row["level"] = s.Level; row["condition"] = s.Condition; row["abandoned"] = s.Abandoned; row["underConstruction"] = s.UnderConstruction;
            row["subBuilding"] = s.SubBuilding;
            row["efficiency"] = s.Efficiency == null ? null : (JToken)Math.Round(s.Efficiency.Value, 4);
            row["serviceUsage"] = s.ServiceUsage == null ? null : (JToken)Math.Round(s.ServiceUsage.Value, 4);
            var use = new JObject();
            if (s.Properties > 0) { use["housing"] = BuildingMetrics.Measure(s.Households, s.Properties, "households", "properties"); use["residents"] = s.Residents; }
            if (s.MaxWorkers > 0) use["employment"] = BuildingMetrics.Measure(s.Employees, s.MaxWorkers, "employees", "maxWorkers");
            if (s.Companies > 0) use["companies"] = s.Companies;
            if (s.StudentCapacity > 0) use["education"] = BuildingMetrics.Measure(s.Students, s.StudentCapacity, "students", "capacity");
            if (s.PatientCapacity > 0) use["healthcare"] = BuildingMetrics.Measure(s.Patients, s.PatientCapacity, "patients", "capacity");
            if (s.RentCount > 0) use["averageRentRaw"] = Math.Round(s.RentTotal / (double)s.RentCount, 2);
            row["utilization"] = use;
            row["designUpkeepRaw"] = s.DesignUpkeep;
            row["issues"] = s.Issues; row["measured"] = s.Measured;
            if (detailed)
            {
                var em = w.EntityManager; var ps = w.GetExistingSystemManaged<PrefabSystem>();
                var b = em.GetComponentData<Building>(s.Entity);
                row["roadEdge"] = NativeBuild.Id(b.m_RoadEdge); row["buildingFlags"] = b.m_Flags.ToString();
                if (em.HasBuffer<Efficiency>(s.Entity))
                    row["efficiencyFactors"] = new JArray(em.GetBuffer<Efficiency>(s.Entity, true).ToNativeArray(Allocator.Temp).ToArray()
                        .Select(f => new JObject { ["factor"] = f.m_Factor.ToString(), ["multiplier"] = Math.Round(f.m_Efficiency, 4) }));
                if (em.HasBuffer<InstalledUpgrade>(s.Entity))
                    row["upgrades"] = new JArray(em.GetBuffer<InstalledUpgrade>(s.Entity, true).ToNativeArray(Allocator.Temp).ToArray()
                        .Where(u => em.Exists(u.m_Upgrade)).Select(u => { var r = NativeBuild.Id(u.m_Upgrade); r["prefab"] = em.HasComponent<PrefabRef>(u.m_Upgrade) ? ps.GetPrefabName(em.GetComponentData<PrefabRef>(u.m_Upgrade).m_Prefab) : null; return r; }));
                if (em.HasBuffer<Renter>(s.Entity))
                {
                    var renters = new JArray();
                    foreach (var r in em.GetBuffer<Renter>(s.Entity, true))
                    {
                        if (renters.Count >= 64 || !em.Exists(r.m_Renter)) continue;
                        var rr = NativeBuild.Id(r.m_Renter);
                        rr["kind"] = em.HasComponent<Game.Citizens.Household>(r.m_Renter) ? "household" : em.HasComponent<Game.Companies.CompanyData>(r.m_Renter) ? "company" : "other";
                        if (em.HasComponent<PrefabRef>(r.m_Renter)) rr["prefab"] = ps.GetPrefabName(em.GetComponentData<PrefabRef>(r.m_Renter).m_Prefab);
                        if (em.HasComponent<PropertyRenter>(r.m_Renter)) rr["rentRaw"] = em.GetComponentData<PropertyRenter>(r.m_Renter).m_Rent;
                        renters.Add(rr);
                    }
                    row["renters"] = renters; row["rentersTruncated"] = em.GetBuffer<Renter>(s.Entity, true).Length > 64;
                }
                row["serviceDataRaw"] = Details(w, s.Entity);
            }
            return row;
        }

        private sealed class AreaFilter
        {
            public bool Active; public float2 Center; public float Radius; public float4 Box; public bool IsBox;
            public static AreaFilter From(JObject args, float maxRadius)
            {
                var f = new AreaFilter();
                if (args["bbox"] is JObject b)
                {
                    f.Active = f.IsBox = true;
                    f.Box = new float4(RequiredFloat(b, "minX"), RequiredFloat(b, "minZ"), RequiredFloat(b, "maxX"), RequiredFloat(b, "maxZ"));
                    if (f.Box.x >= f.Box.z || f.Box.y >= f.Box.w) throw new ArgumentException("bbox_min_must_be_below_max");
                }
                else if (args["x"] != null || args["z"] != null || args["radius"] != null)
                {
                    f.Active = true; f.Center = new float2(RequiredFloat(args, "x"), RequiredFloat(args, "z")); f.Radius = RequiredFloat(args, "radius");
                    if (f.Radius <= 0 || f.Radius > maxRadius) throw new ArgumentException("radius_must_be_0_to_" + maxRadius);
                }
                return f;
            }
            public bool Contains(float3 p) => !Active || (IsBox ? p.x >= Box.x && p.x <= Box.z && p.z >= Box.y && p.z <= Box.w : math.distance(p.xz, Center) <= Radius);
            public bool Touches(float3 a, float3 b, float slack) => !Active || (IsBox
                ? math.max(a.x, b.x) >= Box.x - slack && math.min(a.x, b.x) <= Box.z + slack && math.max(a.z, b.z) >= Box.y - slack && math.min(a.z, b.z) <= Box.w + slack
                : math.distance((a.xz + b.xz) * 0.5f, Center) <= Radius + slack);
        }

        private static HashSet<string> StringSet(JToken token)
        {
            if (token == null) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (token.Type == JTokenType.String) set.Add((string)token);
            else if (token is JArray a) foreach (var v in a) set.Add((string)v);
            else throw new ArgumentException("expected_string_or_array");
            return set;
        }

        private IEnumerable<Entity> CityBuildings(EntityManager em, bool includeSubBuildings)
        {
            foreach (var e in NativeBuild.Buildings(em).OrderBy(e => e.Index).ThenBy(e => e.Version))
            {
                if (!em.HasComponent<Game.Objects.Transform>(e) || em.HasComponent<PrefabData>(e) || em.HasComponent<Native>(e)) continue;
                if (!includeSubBuildings && em.HasComponent<Owner>(e)) continue;
                yield return e;
            }
        }

        private JObject BuildingGetDetails(JObject args)
        {
            var w = RequireCity(); var e = new Entity { Index = RequiredInt(args, "index"), Version = RequiredInt(args, "version") };
            if (!w.EntityManager.Exists(e) || !w.EntityManager.HasComponent<Building>(e)) throw new ArgumentException("live_building_entity_required");
            var row = BuildingJson(w, SampleBuilding(w, e), true); row["citySession"] = citySession;
            return row;
        }

        private JObject BuildingQuery(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var page = new QueryPage(args, 256, 2048);
            var area = AreaFilter.From(args, 14000); var categories = StringSet(args["category"]);
            if (categories != null) foreach (var c in categories) if (!BuildingMetrics.Categories.Contains(c.ToLowerInvariant())) throw new ArgumentException("unknown_category: " + c);
            string prefabFilter = (string)args["filter"] ?? "";
            Entity district = args["district"] is JObject d ? new Entity { Index = RequiredInt(d, "index"), Version = RequiredInt(d, "version") } : Entity.Null;
            bool problemsOnly = (bool?)args["problemsOnly"] == true; bool? abandoned = (bool?)args["abandoned"];
            string utilization = (string)args["utilization"]; int? minLevel = (int?)args["minLevel"];
            bool needFull = problemsOnly || abandoned != null || utilization != null || minLevel != null;
            var rows = new JArray(); int total = 0;
            foreach (var e in CityBuildings(em, (bool?)args["includeSubBuildings"] == true))
            {
                var s = SampleBuilding(w, e, cheapOnly: true);
                if (!area.Contains(s.Position)) continue;
                if (categories != null && !categories.Contains(s.Category)) continue;
                if (district != Entity.Null && s.District != district) continue;
                if (prefabFilter.Length > 0 && (s.Prefab ?? "").IndexOf(prefabFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (needFull || page.Contains(total))
                {
                    s = SampleBuilding(w, e);
                    if (problemsOnly && s.Issues.Count == 0) continue;
                    if (abandoned != null && s.Abandoned != abandoned) continue;
                    if (minLevel != null && (s.Level ?? 0) < minLevel) continue;
                    if (utilization != null && !UtilizationMatches(s, utilization)) continue;
                }
                if (page.Contains(total)) rows.Add(BuildingJson(w, s, false));
                ++total;
            }
            var result = page.Result("buildings", rows, total); result["citySession"] = citySession;
            return result;
        }

        private static bool UtilizationMatches(BuildingSample s, string status)
        {
            foreach (var r in new[] { BuildingMetrics.Ratio(s.Households, s.Properties), BuildingMetrics.Ratio(s.Employees, s.MaxWorkers),
                BuildingMetrics.Ratio(s.Students, s.StudentCapacity), BuildingMetrics.Ratio(s.Patients, s.PatientCapacity) })
                if (BuildingMetrics.UtilizationStatus(r) == status) return true;
            return false;
        }

        private JObject BuildingAnalyzeUtilization(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var area = AreaFilter.From(args, 14000);
            Entity district = args["district"] is JObject d ? new Entity { Index = RequiredInt(d, "index"), Version = RequiredInt(d, "version") } : Entity.Null;
            int listLimit = Math.Min(Math.Max((int?)args["listLimit"] ?? 20, 1), 100);
            var totals = BuildingMetrics.Categories.ToDictionary(c => c, c => new long[8]); // count, households, properties, employees, maxWorkers, abandoned, residents, issues
            var under = new List<(double Ratio, JObject Row)>(); var over = new List<(double Ratio, JObject Row)>();
            long students = 0, studentCapacity = 0, patients = 0, patientCapacity = 0;
            foreach (var e in CityBuildings(em, false))
            {
                var s = SampleBuilding(w, e);
                if (!area.Contains(s.Position) || (district != Entity.Null && s.District != district)) continue;
                var t = totals[s.Category]; t[0]++; t[1] += s.Households; t[2] += s.Properties; t[3] += s.Employees; t[4] += s.MaxWorkers; if (s.Abandoned) t[5]++; t[6] += s.Residents; if (s.Issues.Count > 0) t[7]++;
                students += s.Students; studentCapacity += s.StudentCapacity; patients += s.Patients; patientCapacity += s.PatientCapacity;
                if (s.UnderConstruction) continue;
                foreach (var (used, cap, kind) in new[] { ((long)s.Households, (long)s.Properties, "housing"), (s.Employees, s.MaxWorkers, "employment"), (s.Students, s.StudentCapacity, "education"), (s.Patients, s.PatientCapacity, "healthcare") })
                {
                    var ratio = BuildingMetrics.Ratio(used, cap); if (ratio == null) continue;
                    string status = BuildingMetrics.UtilizationStatus(ratio);
                    if (status != "underused" && status != "overloaded") continue;
                    var row = NativeBuild.Id(e); row["prefab"] = s.Prefab; row["category"] = s.Category; row["metric"] = kind; row["used"] = used; row["capacity"] = cap; row["ratio"] = Math.Round(ratio.Value, 4); row["position"] = Vector(s.Position);
                    (status == "underused" ? under : over).Add((ratio.Value, row));
                }
            }
            var byCategory = new JObject();
            foreach (var kv in totals.Where(kv => kv.Value[0] > 0))
            {
                var t = kv.Value;
                byCategory[kv.Key] = new JObject
                {
                    ["buildings"] = t[0], ["withIssues"] = t[7], ["abandoned"] = t[5], ["residents"] = t[6],
                    ["housing"] = BuildingMetrics.Measure(t[1], t[2], "households", "properties"),
                    ["employment"] = BuildingMetrics.Measure(t[3], t[4], "employees", "maxWorkers")
                };
            }
            return new JObject
            {
                ["byCategory"] = byCategory,
                ["services"] = new JObject { ["education"] = BuildingMetrics.Measure(students, studentCapacity, "students", "capacity"), ["healthcare"] = BuildingMetrics.Measure(patients, patientCapacity, "patients", "capacity") },
                ["underused"] = new JArray(under.OrderBy(u => u.Ratio).Take(listLimit).Select(u => u.Row)), ["underusedTotal"] = under.Count,
                ["overloaded"] = new JArray(over.OrderByDescending(o => o.Ratio).Take(listLimit).Select(o => o.Row)), ["overloadedTotal"] = over.Count,
                ["thresholds"] = new JObject { ["underusedBelow"] = BuildingMetrics.UnderusedBelow, ["overloadedAbove"] = BuildingMetrics.OverloadedAbove },
                ["notes"] = new JArray("Households/properties and employees/maxWorkers are counted from native renter, household and employee buffers.",
                    "Service capacities combine the prefab with installed upgrades; efficiency multipliers are not applied to capacity.",
                    "Buildings under construction are counted but excluded from under/overloaded lists."),
                ["citySession"] = citySession
            };
        }

        // Zoning land-use summary keyed by zone prefab name rather than raw zone indices.
        private JObject ZoningSummary(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var ps = w.GetExistingSystemManaged<PrefabSystem>(); var area = AreaFilter.From(args, 14000);
            var zones = new Dictionary<int, (string Name, string Area)>();
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<ZoneData>(), ComponentType.ReadOnly<PrefabData>()))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var z in es)
            {
                var data = em.GetComponentData<ZoneData>(z);
                string areaName = data.IsOffice() ? "office" : data.m_AreaType.ToString().ToLowerInvariant();
                zones[data.m_ZoneType.m_Index] = (ps.GetPrefabName(z), areaName);
            }
            var byZone = new Dictionary<string, long[]>(); // cells, occupied, empty, blocked
            var byArea = new Dictionary<string, long[]>();
            long blocks = 0;
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Zones.Block>(), ComponentType.ReadOnly<Game.Zones.Cell>() }, None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() } }))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var b in es)
            {
                if (!area.Contains(em.GetComponentData<Game.Zones.Block>(b).m_Position)) continue;
                ++blocks;
                foreach (var cell in em.GetBuffer<Game.Zones.Cell>(b, true))
                {
                    if ((cell.m_State & Game.Zones.CellFlags.Visible) == 0) continue;
                    int index = cell.m_Zone.m_Index;
                    var (name, areaName) = zones.TryGetValue(index, out var z) ? z : (index == 0 ? "Unzoned" : "zone_" + index, index == 0 ? "unzoned" : "unknown");
                    bool blocked = (cell.m_State & Game.Zones.CellFlags.Blocked) != 0, occupied = (cell.m_State & Game.Zones.CellFlags.Occupied) != 0;
                    foreach (var (key, map) in new[] { (name, byZone), (areaName, byArea) })
                    {
                        if (!map.TryGetValue(key, out var c)) map[key] = c = new long[4];
                        c[0]++; if (occupied) c[1]++; else if (blocked) c[3]++; else c[2]++;
                    }
                }
            }
            JObject Rows(Dictionary<string, long[]> map) => new JObject(map.OrderBy(kv => kv.Key).Select(kv => new JProperty(kv.Key, new JObject
            { ["cells"] = kv.Value[0], ["occupied"] = kv.Value[1], ["emptyBuildable"] = kv.Value[2], ["blocked"] = kv.Value[3] })));
            return new JObject { ["byArea"] = Rows(byArea), ["byZone"] = Rows(byZone), ["blocks"] = blocks,
                ["meaning"] = "Visible cells only. emptyBuildable = zoned or unzoned cell neither occupied nor blocked.", ["citySession"] = citySession };
        }
    }
}
