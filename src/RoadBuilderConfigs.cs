using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CitiesIIAgentBridge
{
    // Reads Road Builder's own RoadConfig JSON files. Read-only: the bridge never writes or
    // deletes Road Builder data. Unity-free so parsing and validation are testable offline.
    internal static class RoadBuilderConfigs
    {
        private static readonly Regex Width = new Regex(@"^\s*(\d+(?:\.\d+)?)\s*m\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex Id = new Regex(@"^[A-Za-z0-9-]{1,128}$", RegexOptions.CultureInvariant);
        public static readonly string[] Categories = { "Road", "Highway", "PublicTransport", "Train", "Tram", "Subway", "Gravel", "Tiled", "RaisedSidewalk", "Fence", "Pathway", "NonAsphalt", "NonRoad" };
        public static readonly string[] Addons = { "HasUndergroundWaterPipes", "HasUndergroundElectricityCable", "RequiresUpgradeForElectricity", "GeneratesTrafficLights", "GeneratesZoningBlocks", "GrassLeft", "GrassRight", "GrassCenter", "TreesLeft", "TreesRight", "TreesCenter" };

        public static IEnumerable<string> Files(string directory) =>
            Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>();

        public static JObject Load(string path)
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidOperationException("config_file_too_large");
            return JObject.Parse(File.ReadAllText(path));
        }

        private static string GroupKind(string group)
        {
            string g = (group ?? "").ToLowerInvariant();
            foreach (var kind in new[] { "car", "bus", "tram", "train", "subway", "bike", "bicycle", "parking", "sidewalk", "median", "pedestrian", "fence", "track" })
                if (g.Contains(kind)) return kind == "bicycle" ? "bike" : kind;
            return "other";
        }

        // Structural summary. Direction is reported as Road Builder's own Invert flag rather than
        // asserting a traffic direction the config does not state explicitly.
        public static JObject Summarize(JObject config)
        {
            var lanes = config["Lanes"] as JArray ?? new JArray();
            var kinds = new JObject(); int carNormal = 0, carInverted = 0; double width = 0; bool widthKnown = lanes.Count > 0;
            var rows = new JArray();
            foreach (var lane in lanes.OfType<JObject>())
            {
                string group = (string)lane["GroupPrefabName"] ?? (string)lane["SectionPrefabName"];
                string kind = GroupKind(group); bool invert = (bool?)lane["Invert"] == true;
                kinds[kind] = ((int?)kinds[kind] ?? 0) + 1;
                if (kind == "car") { if (invert) ++carInverted; else ++carNormal; }
                double? w = null;
                if (lane["GroupOptions"] is JObject options)
                    foreach (var p in options.Properties())
                        if (p.Name.IndexOf("Width", StringComparison.OrdinalIgnoreCase) >= 0)
                        { var m = Width.Match((string)p.Value ?? ""); if (m.Success) w = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture); }
                if (w == null) widthKnown = false; else width += w.Value;
                rows.Add(new JObject { ["kind"] = kind, ["group"] = group, ["invert"] = invert, ["widthMetres"] = w == null ? JValue.CreateNull() : new JValue(w.Value), ["options"] = lane["GroupOptions"]?.DeepClone() });
            }
            return new JObject
            {
                ["id"] = (string)config["ID"], ["originalId"] = (string)config["OriginalID"], ["name"] = (string)config["Name"],
                ["type"] = (string)config["Type"], ["version"] = config["Version"]?.DeepClone(), ["category"] = (string)config["Category"],
                ["speedLimit"] = config["SpeedLimit"]?.DeepClone(), ["maxSlopeSteepness"] = config["MaxSlopeSteepness"]?.DeepClone(),
                ["addons"] = new JArray(((string)config["Addons"] ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim())),
                ["laneCount"] = lanes.Count, ["lanesByKind"] = kinds,
                ["carLanesNormal"] = carNormal, ["carLanesInverted"] = carInverted,
                ["estimatedWidthMetres"] = widthKnown ? new JValue(width) : JValue.CreateNull(),
                ["widthNote"] = "Sum of lane-group width options; Road Builder may add edge offsets. Derived, not a native measurement.",
                ["lanes"] = rows
            };
        }

        // Validates a proposed config against Road Builder's observed schema and the group names
        // present in the player's existing configs. Passing is necessary, not sufficient: Road
        // Builder's own generator remains the authority.
        public static JObject Validate(JObject config, ISet<string> knownGroups)
        {
            var errors = new JArray(); var warnings = new JArray();
            if ((string)config["Type"] != "RoadConfig") errors.Add("type_must_be_RoadConfig");
            string name = (string)config["Name"];
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64) errors.Add("name_required_max_64");
            string id = (string)config["ID"];
            if (id != null && !Id.IsMatch(id)) errors.Add("id_invalid_characters");
            string category = (string)config["Category"];
            if (category == null || !Categories.Contains(category)) errors.Add("category_unknown");
            foreach (var a in ((string)config["Addons"] ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()))
                if (!Addons.Contains(a)) errors.Add("addon_unknown: " + a);
            var speed = config["SpeedLimit"];
            if (speed == null || (speed.Type != JTokenType.Integer && speed.Type != JTokenType.Float) || (double)speed <= 0 || (double)speed > 300) errors.Add("speed_limit_must_be_1_to_300");
            var lanes = config["Lanes"] as JArray;
            if (lanes == null || lanes.Count == 0) errors.Add("lanes_required");
            else
            {
                if (lanes.Count > 40) errors.Add("too_many_lanes_max_40");
                int index = 0;
                foreach (var token in lanes)
                {
                    if (!(token is JObject lane)) { errors.Add($"lane_{index}_must_be_object"); ++index; continue; }
                    string group = (string)lane["GroupPrefabName"], section = (string)lane["SectionPrefabName"];
                    if (group == null && section == null) errors.Add($"lane_{index}_needs_group_or_section");
                    else if (group != null && knownGroups != null && knownGroups.Count > 0 && !knownGroups.Contains(group)) warnings.Add($"lane_{index}_group_not_seen_in_existing_configs: {group}");
                    if (lane["GroupOptions"] is JObject options)
                        foreach (var p in options.Properties())
                            if (p.Name.IndexOf("Width", StringComparison.OrdinalIgnoreCase) >= 0 && !string.IsNullOrEmpty((string)p.Value) && !Width.IsMatch((string)p.Value))
                                errors.Add($"lane_{index}_width_unparseable: {p.Value}");
                    ++index;
                }
                var summary = Summarize(config);
                bool roadLike = category == "Road" || category == "Highway" || category == "PublicTransport";
                if (roadLike && (int)summary["carLanesNormal"] + (int)summary["carLanesInverted"] == 0 && ((int?)summary["lanesByKind"]["bus"] ?? 0) == 0)
                    errors.Add("road_category_requires_a_vehicle_lane");
                if (category == "Highway" && ((int?)summary["lanesByKind"]["sidewalk"] ?? 0) > 0) warnings.Add("highway_with_sidewalk");
            }
            return new JObject { ["valid"] = errors.Count == 0, ["errors"] = errors, ["warnings"] = warnings,
                ["authority"] = "Structural check only; Road Builder's generator decides final validity." };
        }
    }
}
