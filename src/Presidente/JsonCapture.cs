using System;
using System.Collections.Generic;
using Colossal.UI.Binding;
using Newtonsoft.Json.Linq;

namespace PresidenteBridge
{
    // Captures the same structured payloads Presidente writes to its UI, without subscribing or navigating.
    public sealed class JsonCapture : IJsonWriter
    {
        readonly Stack<JContainer> stack = new Stack<JContainer>();
        string property;
        int count;
        public JToken Root { get; private set; }
        public string debugName => "PresidenteBridge.JsonCapture";
        void Add(JToken token)
        {
            if (++count > 100000) throw new InvalidOperationException("snapshot_value_limit");
            if (stack.Count == 0) { if (Root != null) throw new InvalidOperationException("multiple_roots"); Root = token; }
            else if (stack.Peek() is JObject obj) { if (property == null) throw new InvalidOperationException("missing_property"); obj[property] = token; property = null; }
            else stack.Peek().Add(token);
        }
        void Begin(JContainer token) { if (stack.Count >= 64) throw new InvalidOperationException("snapshot_depth_limit"); Add(token); stack.Push(token); }
        void End(Type type) { if (stack.Count == 0 || stack.Peek().GetType() != type || property != null) throw new InvalidOperationException("unbalanced_json"); stack.Pop(); }
        public JToken Finish() { if (stack.Count != 0 || Root == null) throw new InvalidOperationException("incomplete_json"); return Root; }
        public void TypeBegin(string typeName) { Begin(new JObject()); }
        public void TypeEnd() { End(typeof(JObject)); }
        public void MapBegin(uint size) { Begin(new JObject()); }
        public void MapEnd() { End(typeof(JObject)); }
        public void ArrayBegin(uint size) { Begin(new JArray()); }
        public void ArrayEnd() { End(typeof(JArray)); }
        public void PropertyName(string name) { if (property != null || stack.Count == 0 || !(stack.Peek() is JObject)) throw new InvalidOperationException("invalid_property"); property = name; }
        public void WriteNull() { Add(JValue.CreateNull()); }
        public void Write(bool v) { Add(new JValue(v)); }
        public void Write(int v) { Add(new JValue(v)); }
        public void Write(uint v) { Add(new JValue(v)); }
        public void Write(long v) { Add(new JValue(v)); }
        public void Write(ulong v) { Add(new JValue(v)); }
        public void Write(float v) { Write((double)v); }
        public void Write(double v) { if (double.IsNaN(v) || double.IsInfinity(v)) throw new InvalidOperationException("non_finite_value"); Add(new JValue(v)); }
        public void Write(string v) { if (v != null && v.Length > 2000000) throw new InvalidOperationException("snapshot_string_limit"); Add(v == null ? JValue.CreateNull() : new JValue(v)); }
    }
}
