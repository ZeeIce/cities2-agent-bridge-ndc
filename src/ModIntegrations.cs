using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace CitiesIIAgentBridge
{
    // Read-only adapters for third-party mods. Types are resolved by name at runtime so the
    // bridge never takes a compile-time dependency, and every adapter reports why it is
    // unavailable instead of failing the command.
    internal sealed class ModAdapter
    {
        public readonly string AssemblyName, Label; public readonly string[] RequiredTypes;
        private Assembly assembly; private bool probed; private readonly Dictionary<string, Type> types = new Dictionary<string, Type>();
        public readonly List<string> Missing = new List<string>();
        public ModAdapter(string assemblyName, string label, params string[] requiredTypes) { AssemblyName = assemblyName; Label = label; RequiredTypes = requiredTypes; }

        public bool Available { get { Probe(); return assembly != null && Missing.Count == 0; } }
        public string Version { get { Probe(); return assembly?.GetName().Version.ToString(); } }
        public bool Loaded { get { Probe(); return assembly != null; } }

        private void Probe()
        {
            if (probed) return; probed = true;
            assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
            if (assembly == null) return;
            foreach (var name in RequiredTypes)
            {
                Type t = null; try { t = assembly.GetType(name, false); } catch (Exception) { }
                if (t == null || !t.IsValueType) Missing.Add(name); else types[name] = t;
            }
        }

        public Type Get(string name) { Probe(); return types.TryGetValue(name, out var t) ? t : null; }

        public JObject Status(string writeSupport) => new JObject
        {
            ["name"] = Label, ["assembly"] = AssemblyName, ["loaded"] = Loaded, ["version"] = Version,
            ["readSupport"] = Available, ["missingTypes"] = new JArray(Missing), ["writeSupport"] = writeSupport
        };
    }

    public sealed partial class Mod
    {
        private static readonly ModAdapter TrafficAdapter = new ModAdapter("Traffic", "Traffic (krzychu124)",
            "Traffic.Components.LaneConnections.ModifiedLaneConnections", "Traffic.Components.ModifiedConnections",
            "Traffic.Components.ModifiedPriorities", "Traffic.Components.PrioritySigns.LanePriority",
            "Traffic.Components.ModifiedIntersection", "Traffic.Components.TrafficUpgrade");
        private static readonly ModAdapter RoadBuilderAdapter = new ModAdapter("RoadBuilder", "Road Builder",
            "RoadBuilder.Domain.Components.RoadBuilderNetwork", "RoadBuilder.Domain.Components.RoadBuilderPrefabData");
        private static readonly ModAdapter C2vmAdapter = new ModAdapter("C2VM.TrafficToolEssentials", "C2VM Traffic Tool Essentials");

        private static bool HasRuntimeComponent(EntityManager em, Entity e, Type t) => t != null && em.HasComponent(e, ComponentType.ReadOnly(t));

        private static JArray ReadRuntimeBuffer(EntityManager em, Entity e, Type t, int limit)
        {
            if (t == null || !typeof(IBufferElementData).IsAssignableFrom(t) || !em.HasComponent(e, ComponentType.ReadOnly(t))) return null;
            var method = typeof(Mod).GetMethod(nameof(ReadBufferRows), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(t);
            return (JArray)method.Invoke(null, new object[] { em, e, limit });
        }

        private static JArray ReadBufferRows<T>(EntityManager em, Entity e, int limit) where T : unmanaged, IBufferElementData
        {
            var rows = new JArray(); var buffer = em.GetBuffer<T>(e, true);
            for (int i = 0; i < buffer.Length && i < limit; i++) rows.Add(RawFields(buffer[i]));
            return rows;
        }

        private JObject TrafficModNodeData(World w, Entity node)
        {
            if (!TrafficAdapter.Available) return new JObject { ["available"] = false, ["status"] = TrafficAdapter.Status("not_supported") };
            var em = w.EntityManager;
            return new JObject
            {
                ["available"] = true,
                ["modifiedConnections"] = HasRuntimeComponent(em, node, TrafficAdapter.Get("Traffic.Components.ModifiedConnections")),
                ["modifiedPriorities"] = HasRuntimeComponent(em, node, TrafficAdapter.Get("Traffic.Components.ModifiedPriorities")),
                ["modifiedIntersection"] = HasRuntimeComponent(em, node, TrafficAdapter.Get("Traffic.Components.ModifiedIntersection")),
                ["modifiedLaneConnections"] = ReadRuntimeBuffer(em, node, TrafficAdapter.Get("Traffic.Components.LaneConnections.ModifiedLaneConnections"), 128),
                ["lanePriorities"] = ReadRuntimeBuffer(em, node, TrafficAdapter.Get("Traffic.Components.PrioritySigns.LanePriority"), 128),
                ["meaning"] = "Raw Traffic-mod records read by reflection. Field meanings belong to the Traffic mod and may change between its versions."
            };
        }

        private JObject IntegrationsStatus()
        {
            var result = new JObject
            {
                ["traffic"] = TrafficAdapter.Status("not_supported: the Traffic mod exposes no public API; writes would have to drive its internal tool pipeline"),
                ["roadBuilder"] = RoadBuilderAdapter.Status("not_supported: configs are read and validated only; Road Builder generates prefabs itself"),
                ["c2vmTrafficTools"] = C2vmAdapter.Status("not_supported"),
                ["roadBuilderConfigDirectory"] = RoadBuilderDirectory(), ["roadBuilderConfigDirectoryExists"] = Directory.Exists(RoadBuilderDirectory())
            };
            try
            {
                var w = RequireCity(); var em = w.EntityManager; var t = TrafficAdapter.Get("Traffic.Components.ModifiedConnections");
                if (t != null) using (var q = em.CreateEntityQuery(ComponentType.ReadOnly(t))) result["traffic"]["nodesWithModifiedConnections"] = q.CalculateEntityCount();
                var rb = RoadBuilderAdapter.Get("RoadBuilder.Domain.Components.RoadBuilderNetwork");
                if (rb != null) using (var q = em.CreateEntityQuery(ComponentType.ReadOnly(rb))) result["roadBuilder"]["edgesUsingRoadBuilderRoads"] = q.CalculateEntityCount();
            }
            catch (InvalidOperationException) { result["cityCounts"] = "no_loaded_city"; }
            return result;
        }

        private static string RoadBuilderDirectory() => Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", "RoadBuilder");

        private Dictionary<string, int> RoadBuilderUsage(World w)
        {
            // Road Builder registers each generated prefab under its config ID.
            var usage = new Dictionary<string, int>(StringComparer.Ordinal); var em = w.EntityManager; var ps = w.GetExistingSystemManaged<PrefabSystem>();
            var tag = RoadBuilderAdapter.Get("RoadBuilder.Domain.Components.RoadBuilderNetwork"); if (tag == null) return usage;
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly(tag), ComponentType.ReadOnly<PrefabRef>()))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es)
            {
                string name = ps.GetPrefabName(em.GetComponentData<PrefabRef>(e).m_Prefab) ?? "";
                usage[name] = (usage.TryGetValue(name, out var n) ? n : 0) + 1;
            }
            return usage;
        }

        private JObject RoadBuilderListConfigs(JObject args)
        {
            var page = new QueryPage(args, 100, 500); string filter = (string)args["filter"] ?? "";
            Dictionary<string, int> usage = null; if ((bool?)args["includeUsage"] == true) usage = RoadBuilderUsage(RequireCity());
            var rows = new JArray(); var errors = new JArray(); int total = 0;
            foreach (var file in RoadBuilderConfigs.Files(RoadBuilderDirectory()))
            {
                JObject summary;
                try { summary = RoadBuilderConfigs.Summarize(RoadBuilderConfigs.Load(file)); }
                catch (Exception e) { if (errors.Count < 20) errors.Add(new JObject { ["file"] = Path.GetFileName(file), ["error"] = e.GetType().Name + ": " + e.Message }); continue; }
                if (filter.Length > 0 && ((string)summary["name"] ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (page.Contains(total))
                {
                    summary.Remove("lanes"); summary["file"] = Path.GetFileName(file);
                    if (usage != null) summary["edgesInCity"] = usage.TryGetValue((string)summary["id"] ?? "", out var n) ? n : 0;
                    rows.Add(summary);
                }
                ++total;
            }
            var result = page.Result("configs", rows, total); result["errors"] = errors; result["directory"] = RoadBuilderDirectory();
            result["roadBuilderLoaded"] = RoadBuilderAdapter.Loaded;
            return result;
        }

        private JObject RoadBuilderGetConfig(JObject args)
        {
            string id = (string)args["id"]; if (string.IsNullOrEmpty(id)) throw new ArgumentException("id_required");
            foreach (var file in RoadBuilderConfigs.Files(RoadBuilderDirectory()))
            {
                var config = RoadBuilderConfigs.Load(file);
                if ((string)config["ID"] != id) continue;
                var result = new JObject { ["summary"] = RoadBuilderConfigs.Summarize(config), ["config"] = config, ["file"] = Path.GetFileName(file) };
                try { var usage = RoadBuilderUsage(RequireCity()); result["edgesInCity"] = usage.TryGetValue(id, out var n) ? n : 0; }
                catch (InvalidOperationException) { result["edgesInCity"] = null; }
                return result;
            }
            throw new ArgumentException("road_builder_config_not_found");
        }

        private JObject RoadBuilderValidateConfig(JObject args)
        {
            if (!(args["config"] is JObject config)) throw new ArgumentException("config_object_required");
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in RoadBuilderConfigs.Files(RoadBuilderDirectory()))
                try { foreach (var lane in RoadBuilderConfigs.Load(file)["Lanes"] as JArray ?? new JArray()) { var g = (string)lane["GroupPrefabName"]; if (g != null) known.Add(g); } }
                catch (Exception) { }
            var result = RoadBuilderConfigs.Validate(config, known);
            result["summary"] = RoadBuilderConfigs.Summarize(config); result["knownGroupPrefabs"] = known.Count;
            result["note"] = "Validation only. The bridge does not write Road Builder files or generate prefabs.";
            return result;
        }
    }
}
