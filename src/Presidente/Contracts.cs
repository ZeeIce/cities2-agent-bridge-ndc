using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PresidenteBridge
{
    public static class Contracts
    {
        public static string Hash(JToken value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
        }
        public static object[] Arguments(JArray values, Type[] types)
        {
            if (values == null || values.Count != types.Length) throw new ArgumentException("argument_count_mismatch");
            return values.Select((v, i) => ConvertArgument(v, types[i])).ToArray();
        }
        static object ConvertArgument(JToken value, Type type)
        {
            if (type == typeof(string)) { if (value.Type != JTokenType.String || ((string)value).Length > 4096) throw new ArgumentException("expected_string_max_4096"); return (string)value; }
            if (type == typeof(bool)) { if (value.Type != JTokenType.Boolean) throw new ArgumentException("expected_boolean"); return (bool)value; }
            if (type == typeof(int)) { if (value.Type != JTokenType.Integer) throw new ArgumentException("expected_integer"); return checked((int)(long)value); }
            if (type == typeof(float)) { if (value.Type != JTokenType.Float && value.Type != JTokenType.Integer) throw new ArgumentException("expected_number"); var n = (double)value; if (double.IsNaN(n) || double.IsInfinity(n) || n > float.MaxValue || n < -float.MaxValue) throw new ArgumentException("expected_finite_float"); return (float)n; }
            throw new ArgumentException("unsupported_argument_type");
        }
        public static bool Matches(JObject state, JArray expectations)
        {
            if (expectations == null || expectations.Count == 0) return false;
            foreach (JObject e in expectations)
            {
                var binding = (string)e["binding"];
                var path = (string)e["path"];
                if (binding == null || path == null || e["equals"] == null) throw new ArgumentException("expectation_requires_binding_path_equals");
                var data = state[binding];
                if (data == null || !JToken.DeepEquals(data.SelectToken(path, false), e["equals"])) return false;
            }
            return true;
        }
    }
}
