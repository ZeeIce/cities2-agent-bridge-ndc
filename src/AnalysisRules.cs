using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CitiesIIAgentBridge
{
    // Measured inputs for the deterministic analysis rules. Null means "not measured".
    internal sealed class CityFacts
    {
        public double? UnemploymentRate;           // native percentage (0-100)
        public long Workers, WorkingAgeEligible, JobsTotal, JobsVacant;
        public long ElectricityCapacity, ElectricityProduction, WaterCapacity, WaterProduction, SewageCapacity, SewageProcessing;
        public int[] ResidentialDemand = new int[3];  // low, medium, high
        public int CommercialDemand, IndustrialDemand, OfficeDemand;
        public Dictionary<string, long> EmptyZonedCells = new Dictionary<string, long>(); // by area: residential/commercial/industrial/office
        public long BudgetBalance;
        public List<(JObject Id, string Name, long Used, long Capacity)> OverloadedServices = new List<(JObject, string, long, long)>();
        public List<(JObject Id, string Prefab, float MinFlow)> SevereEdges = new List<(JObject, string, float)>();
        public List<(JObject Node, string Classes, int Gap)> HierarchyGaps = new List<(JObject, string, int)>();
        public long AbandonedBuildings;
    }

    internal static class AnalysisRules
    {
        public const double UtilityHeadroomWarning = 0.15;

        public static List<Recommendation> Evaluate(CityFacts f, string focus = "all")
        {
            var list = new List<Recommendation>();
            bool Want(string area) => focus == "all" || focus == area;
            if (Want("infrastructure"))
            {
                Utility(list, "electricity", f.ElectricityCapacity, f.ElectricityProduction, "Add generation or storage, sited away from housing; prefer steady sources over wind alone.");
                Utility(list, "water", f.WaterCapacity, f.WaterProduction, "Add pumping capacity upstream of sewage outlets and verify groundwater/surface water quality first.");
                Utility(list, "sewage", f.SewageCapacity, f.SewageProcessing, "Add treatment/outlet capacity downstream of water intakes.");
            }
            if (Want("economy")) Employment(list, f);
            if (Want("zoning")) Zoning(list, f);
            if (Want("services")) foreach (var s in f.OverloadedServices.OrderByDescending(s => s.Used / (double)Math.Max(1, s.Capacity)).Take(10))
                list.Add(new Recommendation
                {
                    Id = "service-overload-" + (int)s.Id["index"], Category = "services", Priority = 2,
                    Problem = $"{s.Name} is over its design capacity.",
                    Evidence = new JObject { ["used"] = s.Used, ["capacity"] = s.Capacity, ["ratio"] = Math.Round(s.Used / (double)Math.Max(1, s.Capacity), 3), ["source"] = "native buffers vs prefab capacity including installed upgrades" },
                    Entities = new JArray(s.Id.DeepClone()),
                    Action = "Add an upgrade or a second facility of the same type within the overloaded catchment.",
                    ExpectedBenefit = "Lower occupancy at this facility and improved service outcomes for its catchment.",
                    Risks = "Construction and upkeep cost; a new facility may draw demand from other catchments rather than relieve this one.",
                    Confidence = "medium", Verification = "After simulating, the facility's used/capacity ratio is at or below 1.0 and no new overload appears nearby."
                });
            if (Want("traffic"))
            {
                if (f.SevereEdges.Count > 0) list.Add(new Recommendation
                {
                    Id = "traffic-severe-congestion", Category = "traffic", Priority = 2,
                    Problem = $"{f.SevereEdges.Count} road segment(s) run below {TrafficMetrics.SevereBelow:0.##} of free-flow speed in at least one sampled period.",
                    Evidence = new JObject { ["severeSegments"] = f.SevereEdges.Count, ["worst"] = new JArray(f.SevereEdges.OrderBy(e => e.MinFlow).Take(5).Select(e => new JObject { ["edge"] = e.Id.DeepClone(), ["prefab"] = e.Prefab, ["minFlowSpeed"] = Math.Round(e.MinFlow, 3) })), ["source"] = "NetUtils.GetTrafficFlowSpeed (native)" },
                    Entities = new JArray(f.SevereEdges.OrderBy(e => e.MinFlow).Take(20).Select(e => e.Id.DeepClone())),
                    Action = "Inspect the downstream intersections of these segments (traffic_get_intersection) before changing anything; fix turn conflicts, missing connections or a missing parallel route first. Do not assume widening helps.",
                    ExpectedBenefit = "Restoring flow on the worst segments; magnitude unknown until the cause is identified.",
                    Risks = "Widening can attract more traffic or move the queue to the next junction; intersection changes can reroute traffic into neighbourhoods.",
                    Confidence = "medium", Verification = "minFlowSpeed of the listed segments rises above 0.6 after a comparable simulation interval without new severe segments appearing nearby."
                });
                if (f.HierarchyGaps.Count > 0) list.Add(new Recommendation
                {
                    Id = "traffic-hierarchy-gaps", Category = "traffic", Priority = f.HierarchyGaps.Any(g => g.Gap >= 3) ? 2 : 3,
                    Problem = $"{f.HierarchyGaps.Count} junction(s) connect roads two or more hierarchy levels apart.",
                    Evidence = new JObject { ["junctions"] = f.HierarchyGaps.Count, ["examples"] = new JArray(f.HierarchyGaps.OrderByDescending(g => g.Gap).Take(5).Select(g => new JObject { ["node"] = g.Node.DeepClone(), ["classes"] = g.Classes, ["gap"] = g.Gap })), ["source"] = "lane counts and RoadData flags (derived class)" },
                    Entities = new JArray(f.HierarchyGaps.OrderByDescending(g => g.Gap).Take(20).Select(g => g.Node.DeepClone())),
                    Action = "Route the lower-class road to a collector instead of the arterial/highway, or restrict it to right-in/right-out.",
                    ExpectedBenefit = "Fewer conflicting movements on the higher-class road and less cut-through traffic on local streets.",
                    Risks = "Rerouting lengthens some trips and can overload the replacement collector; check its flow afterwards.",
                    Confidence = "medium", Verification = "traffic_analyze reports fewer hierarchy gaps and the affected arterial's minFlowSpeed does not fall."
                });
            }
            if (Want("economy") && f.BudgetBalance < 0) list.Add(new Recommendation
            {
                Id = "budget-deficit", Category = "economy", Priority = 1,
                Problem = "The city budget balance is negative.",
                Evidence = new JObject { ["balanceRaw"] = f.BudgetBalance, ["source"] = "CityServiceBudgetSystem.GetBalance" },
                Action = "Review service budgets and upkeep (get_city_management) before raising taxes; tax increases reduce demand.",
                ExpectedBenefit = "Positive balance without collapsing demand.",
                Risks = "Cutting service budgets lowers coverage and happiness; tax rises lower demand.",
                Confidence = "high", Verification = "balanceRaw is positive after the next simulated budget period and demand factors for Taxes do not worsen."
            });
            return list.OrderBy(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        }

        private static void Utility(List<Recommendation> list, string name, long capacity, long production, string action)
        {
            if (capacity <= 0) return;
            double headroom = 1.0 - production / (double)capacity;
            if (headroom >= UtilityHeadroomWarning) return;
            list.Add(new Recommendation
            {
                Id = name + "-headroom", Category = "infrastructure", Priority = headroom <= 0.02 ? 1 : 2,
                Problem = $"{char.ToUpperInvariant(name[0]) + name.Substring(1)} production is within {Math.Max(0, headroom):P0} of capacity.",
                Evidence = new JObject { ["capacityRaw"] = capacity, ["productionRaw"] = production, ["headroom"] = Math.Round(headroom, 4), ["source"] = "sum over producer components (native)" },
                Action = action,
                ExpectedBenefit = "Margin for growth and for weather/variance in production.",
                Risks = "Capital and upkeep cost; poorly sited plants add pollution or noise.",
                Confidence = "high", Verification = $"{name} headroom is at least {UtilityHeadroomWarning:P0} after the next simulation interval with no new shortages."
            });
        }

        private static void Employment(List<Recommendation> list, CityFacts f)
        {
            if (f.UnemploymentRate == null || f.UnemploymentRate < 15) return;
            double vacancyShare = f.JobsTotal > 0 ? f.JobsVacant / (double)f.JobsTotal : 0;
            if (vacancyShare > 0.1) return; // jobs exist; the gap is skills or access, not quantity
            var demand = new[] { ("industrial", f.IndustrialDemand), ("office", f.OfficeDemand), ("commercial", f.CommercialDemand) }.OrderByDescending(d => d.Item2).First();
            list.Add(new Recommendation
            {
                Id = "employment-shortfall", Category = "economy", Priority = f.UnemploymentRate >= 30 ? 1 : 2,
                Problem = "Unemployment is high and almost every job is filled: the city has too few jobs for its working population.",
                Evidence = new JObject { ["unemploymentRateNative"] = Math.Round(f.UnemploymentRate.Value, 2), ["workers"] = f.Workers, ["workingAgeEligible"] = f.WorkingAgeEligible, ["jobsTotal"] = f.JobsTotal, ["jobsVacant"] = f.JobsVacant, ["highestJobDemand"] = demand.Item1 + ":" + demand.Item2 },
                Action = $"Add {demand.Item1} zoning with its own collector access, and pause further residential zoning until vacancies recover.",
                ExpectedBenefit = "Lower unemployment and more tax base from workplaces.",
                Risks = "Industry near housing adds pollution and truck traffic; new jobs may need education levels the population lacks.",
                Confidence = "medium", Verification = "Unemployment falls and jobsTotal rises over the next intervals; no new pollution complaints in nearby homes."
            });
        }

        private static void Zoning(List<Recommendation> list, CityFacts f)
        {
            void Check(string area, int demand)
            {
                f.EmptyZonedCells.TryGetValue(area, out long empty);
                if (demand >= 60 && empty < 200) list.Add(new Recommendation
                {
                    Id = "zoning-" + area + "-supply", Category = "zoning", Priority = 3,
                    Problem = $"{area} demand is high but little {area} land is zoned and empty.",
                    Evidence = new JObject { ["demand"] = demand, ["emptyZonedCells"] = empty },
                    Action = $"Zone additional {area} land on local streets served by an existing collector.",
                    ExpectedBenefit = $"New {area} growth absorbs the demand.",
                    Risks = "Over-zoning spreads growth thinly and raises service/road upkeep; check jobs-housing balance first.",
                    Confidence = "medium", Verification = $"Empty {area} cells are taken up and {area} demand eases after simulating."
                });
                else if (demand <= 10 && empty >= 2000) list.Add(new Recommendation
                {
                    Id = "zoning-" + area + "-surplus", Category = "zoning", Priority = 4,
                    Problem = $"Large amounts of {area} land are zoned but empty while demand is low.",
                    Evidence = new JObject { ["demand"] = demand, ["emptyZonedCells"] = empty },
                    Action = $"Leave the land as reserve and investigate demand blockers (get_city_diagnostics demand factors) instead of zoning more {area}.",
                    ExpectedBenefit = "Avoids sprawl and wasted infrastructure.",
                    Risks = "Minimal; dezoning would remove future capacity.",
                    Confidence = "medium", Verification = "Demand factors identify the blocker; empty cells do not keep growing."
                });
            }
            Check("residential", f.ResidentialDemand.Max());
            Check("commercial", f.CommercialDemand);
            Check("industrial", f.IndustrialDemand);
            Check("office", f.OfficeDemand);
        }
    }
}
