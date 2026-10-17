using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CitiesIIAgentBridge
{
    // Every recommendation carries its evidence, risks and verification criteria; the schema is
    // enforced here so an incomplete recommendation can never reach an agent.
    internal sealed class Recommendation
    {
        public static readonly string[] Confidences = { "low", "medium", "high" };
        public string Id, Category, Problem, Action, ExpectedBenefit, Risks, Confidence, Verification;
        public JObject Evidence = new JObject();
        public JArray Entities = new JArray();
        public int Priority = 3;
        public bool RequiresApproval = true;

        public JObject ToJson()
        {
            foreach (var (name, value) in new[] { ("id", Id), ("category", Category), ("problem", Problem), ("action", Action),
                ("expectedBenefit", ExpectedBenefit), ("risks", Risks), ("confidence", Confidence), ("verification", Verification) })
                if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("recommendation_missing_" + name);
            if (!Confidences.Contains(Confidence)) throw new InvalidOperationException("recommendation_invalid_confidence");
            if (Priority < 1 || Priority > 5) throw new InvalidOperationException("recommendation_invalid_priority");
            if (Evidence.Count == 0) throw new InvalidOperationException("recommendation_requires_evidence");
            return new JObject
            {
                ["id"] = Id, ["category"] = Category, ["priority"] = Priority, ["problem"] = Problem,
                ["evidence"] = Evidence, ["affectedEntities"] = Entities, ["proposedAction"] = Action,
                ["expectedBenefit"] = ExpectedBenefit, ["risksAndTradeoffs"] = Risks, ["confidence"] = Confidence,
                ["verificationCriteria"] = Verification, ["requiresApproval"] = RequiresApproval,
                ["outcomeGuarantee"] = "none: expected benefit is an estimate to be verified by measurement"
            };
        }
    }
}
