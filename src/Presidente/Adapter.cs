using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Colossal.UI.Binding;
using Game.UI;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace PresidenteBridge
{
    internal sealed class Adapter
    {
        internal const string SupportedHash = "C6955717F9959F9F5664A451FF89D0C6176BFCB74158653A96578D9BD139DAD4";
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        readonly HashSet<string> allowed = new HashSet<string>(KnownActions.Allowed, StringComparer.Ordinal);
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        readonly Dictionary<string, JObject> operations = new Dictionary<string, JObject>();
        Assembly assembly;
        string hash;
        string city;
        sealed class Entry { public object Owner; public BindingBase Binding; public string Id; public Delegate Callback; public Action<IJsonWriter> Writer; }
        sealed class Pending { public string City, Action, Digest; public DateTime Expires; public JArray Arguments, Reads, Expectations; }

        internal static object Field(object obj, string name)
        {
            for (var type = obj.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, Fields | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(obj);
            }
            return null;
        }
        void EnsureVersion()
        {
            var found = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "HomelandSecurity");
            if (found == null) throw new InvalidOperationException("presidente_not_loaded");
            if (assembly != found)
            {
                assembly = found;
                // The game loads mods from memory, so Assembly.Location is empty; ask the mod manager for the file it loaded.
                var path = found.Location;
                if (string.IsNullOrEmpty(path))
                    foreach (var info in Game.SceneFlow.GameManager.instance.modManager)
                        if (info.asset != null && info.asset.assembly == found) { path = info.asset.path; break; }
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) throw new InvalidOperationException("presidente_assembly_path_unknown");
                using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
            if (hash != SupportedHash) throw new InvalidOperationException("unsupported_presidente_build: " + hash);
        }
        List<Entry> Entries(World world)
        {
            var result = new List<Entry>();
            foreach (var system in world.Systems)
            {
                if (system.GetType().Assembly != assembly || !(system is UISystemBase)) continue;
                var bindings = Field(system, "m_Bindings") as IEnumerable;
                if (bindings == null) continue;
                foreach (var value in bindings)
                {
                    var binding = value as BindingBase;
                    if (binding == null) continue;
                    result.Add(new Entry { Owner = system, Binding = binding, Id = binding.group + "." + binding.name,
                        Callback = Field(binding, "m_Callback") as Delegate, Writer = Field(binding, "m_WriterDelegate") as Action<IJsonWriter> });
                }
            }
            return result.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        }
        static string Required(JObject args, string name)
        {
            if (args[name]?.Type != JTokenType.String || string.IsNullOrEmpty((string)args[name])) throw new ArgumentException("missing_" + name);
            return (string)args[name];
        }
        static Type[] ParameterTypes(Delegate d) => d.GetType().GetMethod("Invoke").GetParameters().Select(p => p.ParameterType).ToArray();
        JObject Snapshot(List<Entry> entries, JArray readIds)
        {
            if (readIds == null || readIds.Count < 1 || readIds.Count > 8 || readIds.Any(id => id.Type != JTokenType.String)) throw new ArgumentException("bindings_requires_1_to_8_ids");
            var data = new JObject();
            foreach (string id in readIds)
            {
                var matches = entries.Where(e => e.Id == id && e.Writer != null).ToList();
                if (matches.Count != 1) throw new ArgumentException("binding_missing_or_ambiguous: " + id);
                var writer = new JsonCapture();
                matches[0].Writer(writer);
                data[id] = writer.Finish();
                if (data.ToString(Newtonsoft.Json.Formatting.None).Length > 3000000) throw new InvalidOperationException("snapshot_too_large_select_fewer_bindings");
            }
            return data;
        }
        static JObject Page(JArray items, JObject args)
        {
            int offset = (int?)args["offset"] ?? 0, limit = (int?)args["limit"] ?? 100;
            if (offset < 0 || limit < 1 || limit > 200) throw new ArgumentException("invalid_page_1_to_200");
            return new JObject { ["items"] = new JArray(items.Skip(offset).Take(limit)), ["total"] = items.Count, ["offset"] = offset,
                ["nextOffset"] = offset + limit < items.Count ? new JValue(offset + limit) : JValue.CreateNull() };
        }
        internal JObject Dispatch(string command, JObject args, World world, string cityId, Action requireControl)
        {
            EnsureVersion();
            if (city != cityId) { city = cityId; pending.Clear(); operations.Clear(); }
            foreach (var key in pending.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToArray()) pending.Remove(key);
            if (command == "get_presidente_operation")
            {
                if (!operations.TryGetValue(Required(args, "id"), out var op)) throw new ArgumentException("unknown_operation");
                return (JObject)op.DeepClone();
            }
            if (command == "get_presidente_systems" || command == "inspect_presidente_system") return Inspect(command, args, world);
            var entries = Entries(world);
            if (command == "get_presidente_catalog")
            {
                string filter = (string)args["filter"] ?? "";
                var items = new JArray(entries.Where(e => e.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).Select(e => new JObject {
                    ["id"] = e.Id, ["system"] = e.Owner.GetType().FullName, ["kind"] = e.Writer != null ? "read" : e.Callback != null ? "action" : "unsupported",
                    ["callable"] = e.Callback != null && allowed.Contains(e.Id),
                    ["parameters"] = e.Callback == null ? new JArray() : new JArray(ParameterTypes(e.Callback).Select(t => t.Name)),
                    ["reason"] = e.Callback != null && !allowed.Contains(e.Id) ? "not_in_reviewed_action_allowlist" : "" }));
                var page = Page(items, args); page["presidenteSha256"] = hash; page["citySession"] = city; page["adapterVersion"] = "0.1.0"; return page;
            }
            if (command == "get_presidente_state")
            {
                var data = Snapshot(entries, args["bindings"] as JArray);
                return new JObject { ["citySession"] = city, ["data"] = data, ["digest"] = Contracts.Hash(data), ["capturedUtc"] = DateTime.UtcNow.ToString("O"), ["complete"] = true };
            }
            if (command == "preview_presidente_action")
            {
                string action = Required(args, "action");
                var entry = FindAction(entries, action);
                var values = args["arguments"] as JArray ?? new JArray();
                Contracts.Arguments(values, ParameterTypes(entry.Callback));
                var reads = args["bindings"] as JArray;
                if (reads == null) reads = new JArray(entries.Where(e => e.Owner == entry.Owner && e.Writer != null).Select(e => e.Id).Take(8));
                var before = Snapshot(entries, reads);
                var expectations = args["expect"] as JArray ?? new JArray();
                if (expectations.Count > 20) throw new ArgumentException("too_many_expectations");
                foreach (var item in expectations)
                {
                    var e = item as JObject;
                    if (e == null || e["equals"] == null || !reads.Values<string>().Contains(Required(e, "binding"))) throw new ArgumentException("expectation_binding_not_captured");
                    before[(string)e["binding"]].SelectToken(Required(e, "path"), false);
                }
                string token = Guid.NewGuid().ToString("N");
                if (pending.Count >= 32) throw new InvalidOperationException("too_many_pending_previews");
                pending[token] = new Pending { City = city, Action = action, Arguments = (JArray)values.DeepClone(), Reads = (JArray)reads.DeepClone(), Expectations = (JArray)expectations.DeepClone(), Digest = Contracts.Hash(before), Expires = DateTime.UtcNow.AddMinutes(5) };
                return new JObject { ["status"] = "preview", ["token"] = token, ["action"] = action, ["arguments"] = values.DeepClone(), ["before"] = before,
                    ["expiresUtc"] = pending[token].Expires.ToString("O"), ["preconditionsChecked"] = "types_version_city_readback_only",
                    ["note"] = "No action executed. Presidente eligibility checks run on execution. Supply expect conditions for explicit postcondition verification." };
            }
            if (command == "execute_presidente_action")
            {
                requireControl();
                if ((bool?)args["apply"] != true) throw new ArgumentException("apply_true_required");
                string token = Required(args, "token");
                if (!pending.TryGetValue(token, out var plan) || plan.City != city || plan.Expires < DateTime.UtcNow) throw new ArgumentException("preview_missing_expired_or_wrong_city");
                var entry = FindAction(entries, plan.Action);
                var before = Snapshot(entries, plan.Reads);
                if (Contracts.Hash(before) != plan.Digest) { pending.Remove(token); throw new InvalidOperationException("stale_preview_read_again"); }
                var values = Contracts.Arguments(plan.Arguments, ParameterTypes(entry.Callback));
                // Consume before invocation. An uncertain result can never replay the callback with this token.
                pending.Remove(token);
                var op = new JObject { ["id"] = token, ["action"] = plan.Action, ["arguments"] = plan.Arguments.DeepClone(), ["citySession"] = city, ["status"] = "invoking", ["before"] = before };
                if (operations.Count >= 128) operations.Remove(operations.Keys.First());
                operations[token] = op;
                try
                {
                    entry.Callback.DynamicInvoke(values);
                    var after = Snapshot(entries, plan.Reads);
                    op["after"] = after;
                    op["observedStateChanged"] = !JToken.DeepEquals(before, after);
                    op["status"] = plan.Expectations.Count == 0 ? "invoked_unverified" : Contracts.Matches(after, plan.Expectations) ? "postconditions_met" : "postconditions_not_met";
                    op["note"] = "UI handlers can reject or catch errors internally. Invocation alone does not mean the requested policy was applied; inspect messages and after state.";
                }
                catch (Exception error) { op["status"] = "outcome_unknown"; op["error"] = error.GetBaseException().Message; op["note"] = "Do not replay. Inspect state before making a new plan."; }
                op["completedUtc"] = DateTime.UtcNow.ToString("O");
                return (JObject)op.DeepClone();
            }
            throw new ArgumentException("unknown_presidente_command");
        }
        Entry FindAction(List<Entry> entries, string id)
        {
            if (!allowed.Contains(id)) throw new ArgumentException("action_not_allowlisted");
            var found = entries.Where(e => e.Id == id && e.Callback != null).ToList();
            if (found.Count != 1) throw new ArgumentException("action_missing_or_ambiguous");
            return found[0];
        }
        JObject Inspect(string command, JObject args, World world)
        {
            var systems = world.Systems.Where(s => s.GetType().Assembly == assembly).OrderBy(s => s.GetType().FullName).ToArray();
            if (command == "get_presidente_systems") return Page(new JArray(systems.Select(s => new JObject { ["system"] = s.GetType().FullName, ["enabled"] = s.Enabled })), args);
            var name = Required(args, "system");
            var system = systems.FirstOrDefault(s => s.GetType().FullName == name);
            if (system == null) throw new ArgumentException("system_not_found");
            var fields = system.GetType().GetFields(Fields | BindingFlags.DeclaredOnly).OrderBy(f => f.Name).ToArray();
            var requested = (string)args["field"];
            if (requested == null) return Page(new JArray(fields.Select(f => new JObject { ["field"] = f.Name, ["type"] = f.FieldType.FullName })), args);
            var target = fields.FirstOrDefault(f => f.Name == requested);
            if (target == null) throw new ArgumentException("field_not_found");
            int offset = (int?)args["offset"] ?? 0, limit = (int?)args["limit"] ?? 50, depth = (int?)args["depth"] ?? 2;
            if (offset < 0 || limit < 1 || limit > 200 || depth < 0 || depth > 4) throw new ArgumentException("inspection_bounds_invalid");
            var value = target.GetValue(system);
            if (value is IEnumerable sequence && !(value is string))
            {
                var items = new JArray(); int index = 0; bool more = false; var enumerator = sequence.GetEnumerator();
                try { while (enumerator.MoveNext()) { if (index++ < offset) continue; if (items.Count == limit) { more = true; break; } int budget = 1000; items.Add(Describe(enumerator.Current, depth, ref budget)); } }
                finally { (enumerator as IDisposable)?.Dispose(); }
                return new JObject { ["system"] = name, ["field"] = requested, ["items"] = items, ["offset"] = offset, ["nextOffset"] = more ? new JValue(offset + limit) : JValue.CreateNull(), ["citySession"] = city };
            }
            int scalarBudget = 5000;
            return new JObject { ["system"] = name, ["field"] = requested, ["value"] = Describe(value, depth, ref scalarBudget), ["citySession"] = city };
        }
        static JToken Describe(object value, int depth, ref int budget)
        {
            if (--budget < 0) return new JObject { ["truncated"] = "value_budget" };
            if (value == null) return JValue.CreateNull();
            var type = value.GetType();
            if (value is string text) return new JValue(text.Length > 4096 ? text.Substring(0,4096) + " [truncated]" : text);
            if (type.IsPrimitive || type.IsEnum || value is decimal) return type.IsEnum ? new JValue(value.ToString()) : JToken.FromObject(value);
            if (value is Entity entity) return new JObject { ["index"] = entity.Index, ["version"] = entity.Version };
            if (depth == 0 || value is ComponentSystemBase || value is Delegate || value is World || value is UnityEngine.Object) return new JObject { ["type"] = type.FullName, ["truncated"] = "depth_or_reference" };
            if (value is IEnumerable sequence)
            {
                var items = new JArray(); int count = 0;
                foreach (var item in sequence) { if (count++ >= 30) { items.Add(new JObject { ["truncated"] = "nested_collection_limit" }); break; } items.Add(Describe(item, depth - 1, ref budget)); }
                return items;
            }
            if (!(type.Namespace ?? "").StartsWith("HomelandSecurity", StringComparison.Ordinal) && !type.IsValueType) return new JObject { ["type"] = type.FullName };
            var result = new JObject();
            foreach (var field in type.GetFields(Fields).Take(80)) result[field.Name] = Describe(field.GetValue(value), depth - 1, ref budget);
            return result;
        }
    }
}
