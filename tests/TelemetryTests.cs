using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CitiesIIAgentBridge;
using Newtonsoft.Json.Linq;

// Offline checks for the Unity-free telemetry, analysis and integration logic added in 0.6.0.
static class TelemetryTests
{
    static int passed;
    static void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); ++passed; Console.WriteLine("PASS: " + label); }
    static void Throws(Action action, string expected, string label)
    {
        try { action(); } catch (Exception e) when (e.Message.Contains(expected)) { ++passed; Console.WriteLine("PASS: " + label); return; }
        throw new Exception("FAIL: " + label);
    }

    public static int Run()
    {
        passed = 0;
        Traffic(); Queues(); Buildings(); Recommendations(); Rules(); RoadBuilder(); Registry();
        return passed;
    }

    static void Queues()
    {
        // 0 <- 1 <- 2 (2 waits behind 1, 1 behind 0); 0 is stopped at a light, so 0 heads all three.
        var loops = new List<List<int>>();
        var h = QueueMath.ResolveHeads(new[] { -1, 0, 1 }, new[] { true, true, true }, loops);
        Check(h.SequenceEqual(new[] { 0, 0, 0 }) && loops.Count == 0, "queue chain resolves to its head");
        // A stopped car behind a moving car is its own head; moving cars get -1.
        h = QueueMath.ResolveHeads(new[] { -1, 0 }, new[] { false, true }, loops);
        Check(h[0] == -1 && h[1] == 1, "moving blocker ends the queue");
        // 0 -> 1 -> 2 -> 0 is a gridlock loop; 3 queues behind the loop.
        loops.Clear();
        h = QueueMath.ResolveHeads(new[] { 1, 2, 0, 2 }, new[] { true, true, true, true }, loops);
        Check(loops.Count == 1 && loops[0].Count == 3 && h.All(x => x == -2), "deadlock loop detected with its tail");
        // Out-of-range blocker indices are treated as no blocker.
        h = QueueMath.ResolveHeads(new[] { 9 }, new[] { true }, new List<List<int>>());
        Check(h[0] == 0, "invalid blocker index is ignored");
    }

    static void Traffic()
    {
        Check(TrafficMetrics.MinFlow(new[] { 0.9f, 0.4f, float.NaN, 0.7f }) == 0.4f, "min flow ignores NaN");
        Check(TrafficMetrics.MinFlow(new float[0]) == -1 && TrafficMetrics.FlowStatus(-1) == "no_data", "empty flow is no_data, not zero");
        Check(TrafficMetrics.MinFlow(new[] { 1.3f }) == 1f, "flow ratio clamps to 1");
        Check(TrafficMetrics.FlowStatus(0.2f) == "severe" && TrafficMetrics.FlowStatus(0.5f) == "congested" && TrafficMetrics.FlowStatus(0.7f) == "busy" && TrafficMetrics.FlowStatus(0.95f) == "free", "flow banding");
        Check(TrafficMetrics.Classify(true, 1, 0, false) == RoadClass.Highway, "highway rules win over lane count");
        Check(TrafficMetrics.Classify(false, 1, 1, false) == RoadClass.Local, "2-lane two-way is local");
        Check(TrafficMetrics.Classify(false, 2, 2, false) == RoadClass.Collector, "4-lane undivided is collector");
        Check(TrafficMetrics.Classify(false, 2, 2, true) == RoadClass.Arterial, "4-lane divided is arterial");
        Check(TrafficMetrics.Classify(false, 3, 3, false) == RoadClass.Arterial, "6-lane is arterial");
        Check(TrafficMetrics.Classify(false, 3, 0, false) == RoadClass.Arterial, "3-lane one-way compares as 6-lane equivalent");
        Check(TrafficMetrics.Classify(false, 1, 0, false) == RoadClass.Local, "1-lane one-way is local");
        Check(TrafficMetrics.HierarchyGap(RoadClass.Local, RoadClass.Highway) == 3 && TrafficMetrics.GapSeverity(3) == "high", "local onto highway is a high gap");
        Check(TrafficMetrics.GapSeverity(TrafficMetrics.HierarchyGap(RoadClass.Collector, RoadClass.Arterial)) == "none", "adjacent classes are fine");
        Check(TrafficMetrics.BottleneckScore(0.2f, 4, 200) > TrafficMetrics.BottleneckScore(0.2f, 2, 200), "more lanes rank higher at equal congestion");
        Check(TrafficMetrics.BottleneckScore(-1, 4, 200) == 0, "no data never ranks as a bottleneck");
    }

    static void Buildings()
    {
        Check(BuildingMetrics.Ratio(5, 0) == null && BuildingMetrics.UtilizationStatus(null) == "unknown", "zero capacity is unknown, not 0%");
        Check(BuildingMetrics.UtilizationStatus(0.1) == "underused" && BuildingMetrics.UtilizationStatus(0.5) == "partial" && BuildingMetrics.UtilizationStatus(1.0) == "healthy" && BuildingMetrics.UtilizationStatus(1.2) == "overloaded", "utilization banding");
        var m = BuildingMetrics.Measure(3, 4, "households", "properties");
        Check((long)m["households"] == 3 && (double)m["ratio"] == 0.75 && (string)m["status"] == "healthy", "measure object");
        Check(BuildingMetrics.Category(false, false, true, true, false) == "office", "office beats industrial tag");
        Check(BuildingMetrics.Category(true, true, false, false, false) == "mixed", "residential+commercial is mixed");
        Check(BuildingMetrics.Category(false, false, false, false, true) == "service", "service category");
        Check(BuildingMetrics.Categories.Contains("other"), "category list complete");
    }

    static Recommendation Valid() => new Recommendation { Id = "x", Category = "c", Problem = "p", Action = "a", ExpectedBenefit = "b", Risks = "r", Confidence = "medium", Verification = "v", Evidence = new JObject { ["n"] = 1 } };

    static void Recommendations()
    {
        var json = Valid().ToJson();
        foreach (var key in new[] { "problem", "evidence", "affectedEntities", "proposedAction", "expectedBenefit", "risksAndTradeoffs", "confidence", "verificationCriteria", "requiresApproval" })
            Check(json[key] != null, "recommendation has " + key);
        Check(((string)json["outcomeGuarantee"]).StartsWith("none"), "recommendation never claims a guaranteed outcome");
        Throws(() => { var r = Valid(); r.Risks = ""; r.ToJson(); }, "recommendation_missing_risks", "missing risks rejected");
        Throws(() => { var r = Valid(); r.Confidence = "certain"; r.ToJson(); }, "invalid_confidence", "unknown confidence rejected");
        Throws(() => { var r = Valid(); r.Evidence = new JObject(); r.ToJson(); }, "requires_evidence", "evidence required");
        Throws(() => { var r = Valid(); r.Priority = 9; r.ToJson(); }, "invalid_priority", "priority bounded");
    }

    static CityFacts FortJohnson() => new CityFacts
    {
        // Values measured live in the test city on 2026-10-17 before the power expansion.
        UnemploymentRate = 62.1, Workers = 1386, WorkingAgeEligible = 3659, JobsTotal = 1676, JobsVacant = 117,
        ElectricityCapacity = 53403, ElectricityProduction = 53403, WaterCapacity = 37058, WaterProduction = 14115,
        SewageCapacity = 460000, SewageProcessing = 14115, ResidentialDemand = new[] { 100, 0, 0 },
        CommercialDemand = 0, IndustrialDemand = 67, OfficeDemand = 0, BudgetBalance = 436389,
        EmptyZonedCells = new Dictionary<string, long> { ["residential"] = 76, ["commercial"] = 900, ["industrial"] = 50, ["office"] = 400 }
    };

    static void Rules()
    {
        var recs = AnalysisRules.Evaluate(FortJohnson());
        foreach (var r in recs) r.ToJson(); // every rule output satisfies the schema
        Check(recs.Any(r => r.Id == "electricity-headroom" && r.Priority == 1), "maxed-out power is top priority");
        Check(!recs.Any(r => r.Id == "water-headroom") && !recs.Any(r => r.Id == "sewage-headroom"), "ample water/sewage raises nothing");
        var jobs = recs.Single(r => r.Id == "employment-shortfall");
        Check(jobs.Action.Contains("industrial") && jobs.Priority == 1, "jobs shortfall points at the highest job demand");
        Check(recs.Any(r => r.Id == "zoning-residential-supply") && recs.Any(r => r.Id == "zoning-industrial-supply"), "high demand with little empty land is flagged");
        Check(!recs.Any(r => r.Id == "budget-deficit"), "positive budget raises nothing");
        Check(recs.Select(r => r.Priority).SequenceEqual(recs.Select(r => r.Priority).OrderBy(p => p)), "recommendations ordered by priority");
        var f = FortJohnson(); f.JobsVacant = 600;
        Check(!AnalysisRules.Evaluate(f).Any(r => r.Id == "employment-shortfall"), "vacant jobs mean the gap is not job quantity");
        f = FortJohnson(); f.BudgetBalance = -5;
        Check(AnalysisRules.Evaluate(f, "economy").Any(r => r.Id == "budget-deficit"), "deficit flagged");
        Check(AnalysisRules.Evaluate(f, "traffic").Count == 0, "focus filters rule families");
        f.SevereEdges.Add((new JObject { ["index"] = 1, ["version"] = 1 }, "Medium Road", 0.2f));
        var traffic = AnalysisRules.Evaluate(f, "traffic").Single();
        Check(traffic.Action.Contains("Do not assume widening"), "congestion advice does not default to widening");
        f.OverloadedServices.Add((new JObject { ["index"] = 7, ["version"] = 1 }, "ElementarySchool01", 600, 500));
        Check(AnalysisRules.Evaluate(f, "services").Single().Id == "service-overload-7", "overloaded service flagged with entity id");
    }

    const string Sample = @"{""Type"":""RoadConfig"",""Version"":5,""ID"":""rtest-1"",""OriginalID"":""rtest-1"",""Name"":""Test Avenue"",""SpeedLimit"":60,""MaxSlopeSteepness"":0.2,""Category"":""Road"",
        ""Addons"":""HasUndergroundWaterPipes, GeneratesZoningBlocks"",""Lanes"":[
        {""GroupPrefabName"":""RoadBuilder.LaneGroups.SidewalkGroupPrefab"",""GroupOptions"":{""Width"":""4m"",""Parking"":""""},""Invert"":true},
        {""GroupPrefabName"":""RoadBuilder.LaneGroups.CarGroupPrefab"",""GroupOptions"":{""Lane Width"":""3m""},""Invert"":true},
        {""GroupPrefabName"":""RoadBuilder.LaneGroups.MedianGroupPrefab"",""GroupOptions"":{""Median Width"":""2m""},""Invert"":false},
        {""GroupPrefabName"":""RoadBuilder.LaneGroups.CarGroupPrefab"",""GroupOptions"":{""Lane Width"":""3m""},""Invert"":false},
        {""GroupPrefabName"":""RoadBuilder.LaneGroups.SidewalkGroupPrefab"",""GroupOptions"":{""Width"":""4m""},""Invert"":false}]}";

    static void RoadBuilder()
    {
        var config = JObject.Parse(Sample); var s = RoadBuilderConfigs.Summarize(config);
        Check((int)s["carLanesNormal"] == 1 && (int)s["carLanesInverted"] == 1, "car lanes split by Invert flag");
        Check((double)s["estimatedWidthMetres"] == 16 && (int)s["lanesByKind"]["sidewalk"] == 2 && (int)s["lanesByKind"]["median"] == 1, "width and lane kinds summarised");
        Check(((JArray)s["addons"]).Count == 2, "addons parsed");
        var known = new HashSet<string> { "RoadBuilder.LaneGroups.SidewalkGroupPrefab", "RoadBuilder.LaneGroups.CarGroupPrefab", "RoadBuilder.LaneGroups.MedianGroupPrefab" };
        var ok = RoadBuilderConfigs.Validate(config, known);
        Check((bool)ok["valid"] && ((JArray)ok["warnings"]).Count == 0, "valid config passes");
        var bad = (JObject)config.DeepClone(); bad["Category"] = "Spaceport"; bad["SpeedLimit"] = 0; bad["Addons"] = "Teleporters";
        ((JArray)bad["Lanes"])[1]["GroupOptions"]["Lane Width"] = "wide";
        var r = RoadBuilderConfigs.Validate(bad, known); var errors = ((JArray)r["errors"]).Select(e => (string)e).ToList();
        Check(!(bool)r["valid"] && errors.Contains("category_unknown") && errors.Any(e => e.StartsWith("speed_limit")) && errors.Any(e => e.StartsWith("addon_unknown")) && errors.Any(e => e.Contains("width_unparseable")), "invalid config reports every error");
        var noCars = (JObject)config.DeepClone(); noCars["Lanes"] = new JArray(((JArray)config["Lanes"])[0].DeepClone());
        Check(((JArray)RoadBuilderConfigs.Validate(noCars, known)["errors"]).Any(e => (string)e == "road_category_requires_a_vehicle_lane"), "road without vehicle lanes rejected");
        var unknown = (JObject)config.DeepClone(); ((JArray)unknown["Lanes"])[1]["GroupPrefabName"] = "RoadBuilder.LaneGroups.HoverGroupPrefab";
        Check(((JArray)RoadBuilderConfigs.Validate(unknown, known)["warnings"]).Count == 1, "unseen lane group is a warning, not an error");
        // Optional: the player's real Road Builder files must all parse.
        string dir = Environment.GetEnvironmentVariable("CIAB_ROADBUILDER_DIR");
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            int count = 0; foreach (var file in RoadBuilderConfigs.Files(dir)) { RoadBuilderConfigs.Summarize(RoadBuilderConfigs.Load(file)); ++count; }
            Check(count > 0, $"all {count} installed Road Builder configs summarise");
        }
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "build.ps1")) && Directory.Exists(Path.Combine(d.FullName, "src"))) return d.FullName;
        throw new Exception("repository root not found");
    }

    // The command list lives in four places; any drift breaks agents silently, so fail loudly.
    static void Registry()
    {
        string root = RepoRoot();
        var dispatched = new HashSet<string>(Regex.Matches(File.ReadAllText(Path.Combine(root, "src", "Mod.cs")), @"case ""([a-z_]+)"":").Select(m => m.Groups[1].Value));
        var client = new HashSet<string>(Regex.Match(File.ReadAllText(Path.Combine(root, "bridge.ps1")), @"ValidateSet\(([^)]*)\)").Groups[1].Value.Split(',').Select(s => s.Trim().Trim('\'')));
        client.Remove("stop");
        var listed = new HashSet<string>(JArray.Parse(File.ReadAllText(Path.Combine(root, "commands.json"))).Select(t => (string)t));
        Check(dispatched.SetEquals(listed), "commands.json matches Mod.Dispatch" + Diff(dispatched, listed));
        Check(dispatched.SetEquals(client), "bridge.ps1 ValidateSet matches Mod.Dispatch" + Diff(dispatched, client));
        string mod = File.ReadAllText(Path.Combine(root, "src", "Mod.cs"));
        string caps = mod.Substring(mod.IndexOf("case \"get_capabilities\""), mod.IndexOf("case \"get_city_state\"") - mod.IndexOf("case \"get_capabilities\""));
        var advertised = new HashSet<string>(Regex.Matches(caps, @"""([a-z][a-z_]*)""").Select(m => m.Groups[1].Value).Where(dispatched.Contains));
        var missing = dispatched.Except(advertised).Except(new[] { "diagnose_connections" }).ToList();
        Check(missing.Count == 0, "get_capabilities advertises every command" + (missing.Count > 0 ? ": missing " + string.Join(",", missing) : ""));
    }

    static string Diff(HashSet<string> a, HashSet<string> b)
    {
        var x = a.Except(b).ToList(); var y = b.Except(a).ToList();
        return x.Count + y.Count == 0 ? "" : $" (only in first: {string.Join(",", x)}; only in second: {string.Join(",", y)})";
    }
}
