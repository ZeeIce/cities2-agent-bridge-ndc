using System;
using System.Collections.Generic;
using System.Linq;
using Game.Common;
using Game.Simulation;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        private static readonly string[] AnalysisFocus = { "all", "infrastructure", "economy", "services", "traffic", "zoning" };

        // Gathers measured facts once, then applies the deterministic rules in AnalysisRules.
        private JObject CityAnalyze(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; string focus = (string)args["focus"] ?? "all";
            if (!AnalysisFocus.Contains(focus)) throw new ArgumentException("focus_must_be_one_of: " + string.Join(",", AnalysisFocus));
            var facts = new CityFacts(); var measured = new JObject();
            var management = CityManagement();
            facts.BudgetBalance = (long)management["budget"]["balanceRaw"];
            var demand = (JObject)management["demand"]; var res = (JArray)demand["residential"];
            facts.ResidentialDemand = res.Select(v => (int)v).ToArray(); facts.CommercialDemand = (int)demand["commercial"]; facts.IndustrialDemand = (int)demand["industrial"]; facts.OfficeDemand = (int)demand["office"];
            measured["demand"] = demand.DeepClone(); measured["budgetBalanceRaw"] = facts.BudgetBalance;
            var households = w.GetExistingSystemManaged<CountHouseholdDataSystem>();
            if (households != null && !households.IsCountDataNotReady())
            {
                facts.UnemploymentRate = households.UnemploymentRate; facts.Workers = households.CityWorkerCount; facts.WorkingAgeEligible = households.WorkableCitizenCount;
                measured["unemploymentRateNative"] = Math.Round(households.UnemploymentRate, 3);
            }
            var jobs = w.GetExistingSystemManaged<CountWorkplacesSystem>();
            if (jobs != null)
            {
                facts.JobsTotal = SumWorkplaces(jobs.GetTotalWorkplaces()); facts.JobsVacant = SumWorkplaces(jobs.GetFreeWorkplaces());
                measured["jobsTotal"] = facts.JobsTotal; measured["jobsVacant"] = facts.JobsVacant;
            }
            if (focus == "all" || focus == "infrastructure" || focus == "services")
            {
                foreach (var e in CityBuildings(em, true))
                {
                    if (em.HasComponent<Game.Buildings.ElectricityProducer>(e)) { var c = em.GetComponentData<Game.Buildings.ElectricityProducer>(e); facts.ElectricityCapacity += c.m_Capacity; facts.ElectricityProduction += c.m_LastProduction; }
                    if (em.HasComponent<Game.Buildings.WaterPumpingStation>(e)) { var c = em.GetComponentData<Game.Buildings.WaterPumpingStation>(e); facts.WaterCapacity += c.m_Capacity; facts.WaterProduction += c.m_LastProduction; }
                    if (em.HasComponent<Game.Buildings.SewageOutlet>(e)) { var c = em.GetComponentData<Game.Buildings.SewageOutlet>(e); facts.SewageCapacity += c.m_Capacity; facts.SewageProcessing += c.m_LastProcessed; }
                    if (em.HasComponent<Owner>(e)) continue;
                    var s = SampleBuilding(w, e);
                    if (s.Abandoned) ++facts.AbandonedBuildings;
                    if (s.StudentCapacity > 0 && s.Students > s.StudentCapacity) facts.OverloadedServices.Add((NativeBuild.Id(e), s.Prefab, s.Students, s.StudentCapacity));
                    if (s.PatientCapacity > 0 && s.Patients > s.PatientCapacity) facts.OverloadedServices.Add((NativeBuild.Id(e), s.Prefab, s.Patients, s.PatientCapacity));
                }
                measured["utilitiesRaw"] = new JObject
                {
                    ["electricity"] = new JObject { ["capacity"] = facts.ElectricityCapacity, ["production"] = facts.ElectricityProduction },
                    ["water"] = new JObject { ["capacity"] = facts.WaterCapacity, ["production"] = facts.WaterProduction },
                    ["sewage"] = new JObject { ["capacity"] = facts.SewageCapacity, ["processing"] = facts.SewageProcessing }
                };
                measured["overloadedServices"] = facts.OverloadedServices.Count; measured["abandonedBuildings"] = facts.AbandonedBuildings;
            }
            if (focus == "all" || focus == "zoning")
            {
                var zoning = ZoningSummary(new JObject());
                foreach (var p in ((JObject)zoning["byArea"]).Properties()) facts.EmptyZonedCells[p.Name] = (long)p.Value["emptyBuildable"];
                measured["zoningByArea"] = zoning["byArea"];
            }
            if (focus == "all" || focus == "traffic")
            {
                var (traffic, gaps, severe) = TrafficAnalysis(w, AreaFilter.From(new JObject(), 14000), 25);
                facts.HierarchyGaps = gaps; facts.SevereEdges = severe;
                measured["traffic"] = new JObject { ["roadKm"] = traffic["roadKm"], ["hierarchyGapTotal"] = traffic["hierarchyGapTotal"], ["severeSegments"] = traffic["severeSegments"], ["cityAverages"] = traffic["cityAverages"] };
            }
            var recommendations = AnalysisRules.Evaluate(facts, focus);
            return new JObject
            {
                ["focus"] = focus, ["measured"] = measured,
                ["recommendations"] = new JArray(recommendations.Select(r => r.ToJson())),
                ["method"] = "Deterministic rules over native measurements. Recommendations are proposals for the agent to validate with previews and to verify by simulating; none are applied by this command.",
                ["citySession"] = citySession
            };
        }

        private static long SumWorkplaces(object workplaces)
        {
            long total = 0;
            foreach (var f in workplaces.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                if (f.FieldType == typeof(int)) total += (int)f.GetValue(workplaces);
            return total;
        }
    }
}
