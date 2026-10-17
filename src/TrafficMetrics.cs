using System;

namespace CitiesIIAgentBridge
{
    internal enum RoadClass { Local = 0, Collector = 1, Arterial = 2, Highway = 3 }

    // Unity-free so classification rules can be tested offline. Inputs are measured lane
    // counts and native prefab flags, never prefab display names.
    internal static class TrafficMetrics
    {
        // Thresholds on NetUtils.GetTrafficFlowSpeed (1 = free flow). The banding is the
        // bridge's derived interpretation; the raw native ratios are always reported alongside.
        public const float SevereBelow = 0.35f, CongestedBelow = 0.6f, BusyBelow = 0.85f;

        public static float MinFlow(float[] samples)
        {
            if (samples == null || samples.Length == 0) return -1;
            float min = float.MaxValue; bool any = false;
            foreach (var s in samples) if (!float.IsNaN(s) && !float.IsInfinity(s) && s >= 0) { min = Math.Min(min, s); any = true; }
            return any ? Math.Min(min, 1f) : -1;
        }

        public static string FlowStatus(float minFlow) =>
            minFlow < 0 ? "no_data" : minFlow < SevereBelow ? "severe" : minFlow < CongestedBelow ? "congested" : minFlow < BusyBelow ? "busy" : "free";

        // One-way roads are compared by their two-way equivalent so a 3-lane one-way
        // arterial is not mistaken for a local street.
        public static RoadClass Classify(bool highwayRules, int forwardLanes, int backwardLanes, bool separatedCarriageways)
        {
            if (highwayRules) return RoadClass.Highway;
            forwardLanes = Math.Max(0, forwardLanes); backwardLanes = Math.Max(0, backwardLanes);
            bool oneWay = forwardLanes == 0 || backwardLanes == 0;
            int equivalent = oneWay ? 2 * Math.Max(forwardLanes, backwardLanes) : forwardLanes + backwardLanes;
            if (equivalent >= 6 || (separatedCarriageways && equivalent >= 4)) return RoadClass.Arterial;
            if (equivalent >= 4) return RoadClass.Collector;
            return RoadClass.Local;
        }

        public static string Name(RoadClass value) => value.ToString().ToLowerInvariant();

        // A junction skipping two or more levels (local onto arterial/highway) breaks hierarchy.
        public static int HierarchyGap(RoadClass a, RoadClass b) => Math.Abs((int)a - (int)b);
        public static string GapSeverity(int gap) => gap >= 3 ? "high" : gap == 2 ? "medium" : "none";

        // Congestion weighted by capacity and exposure; used only for ranking, never reported as a measurement.
        public static double BottleneckScore(float minFlow, int carLanes, float lengthMetres)
        {
            if (minFlow < 0) return 0;
            double congestion = 1.0 - Math.Min(1f, Math.Max(0f, minFlow));
            return congestion * Math.Sqrt(Math.Max(1, carLanes)) * Math.Min(1.0, Math.Max(0f, lengthMetres) / 100.0);
        }
    }
}
