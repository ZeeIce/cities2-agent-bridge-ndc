using System;
using Newtonsoft.Json.Linq;

namespace CitiesIIAgentBridge
{
    // Unity-free utilization arithmetic shared by building queries and city analysis.
    internal static class BuildingMetrics
    {
        public const double UnderusedBelow = 0.25, PartialBelow = 0.7, OverloadedAbove = 1.0;

        public static double? Ratio(long used, long capacity) =>
            capacity <= 0 || used < 0 ? (double?)null : used / (double)capacity;

        public static string UtilizationStatus(double? ratio) =>
            ratio == null ? "unknown" : ratio < UnderusedBelow ? "underused" : ratio < PartialBelow ? "partial" : ratio <= OverloadedAbove ? "healthy" : "overloaded";

        public static JObject Measure(long used, long capacity, string usedLabel, string capacityLabel)
        {
            var ratio = Ratio(used, capacity);
            return new JObject
            {
                [usedLabel] = used, [capacityLabel] = capacity,
                ["ratio"] = ratio == null ? JValue.CreateNull() : new JValue(Math.Round(ratio.Value, 4)),
                ["status"] = UtilizationStatus(ratio)
            };
        }

        public static string Category(bool residential, bool commercial, bool industrial, bool office, bool service)
        {
            // Office buildings also carry the industrial property tag in some versions; office wins.
            if (office) return "office";
            if (residential && commercial) return "mixed";
            if (residential) return "residential";
            if (commercial) return "commercial";
            if (industrial) return "industrial";
            if (service) return "service";
            return "other";
        }

        public static readonly string[] Categories = { "residential", "mixed", "commercial", "industrial", "office", "service", "other" };
    }
}
