using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityFigmaBridge.Runtime.UI
{
    [DisallowMultipleComponent]
    public class FigmaScreenRefs : MonoBehaviour
    {
        [Serializable]
        public class Entry
        {
            public string Key;
            public GameObject Target;
        }

        public List<Entry> Entries = new();

        private Dictionary<string, GameObject> m_Lookup;

        private Dictionary<string, GameObject> Lookup
        {
            get
            {
                if (m_Lookup != null) return m_Lookup;
                m_Lookup = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in Entries)
                {
                    if (entry.Target != null && !string.IsNullOrEmpty(entry.Key)) m_Lookup[entry.Key] = entry.Target;
                }
                return m_Lookup;
            }
        }

        public bool Has(string key) => Lookup.ContainsKey(key);

        public GameObject Get(string key)
        {
            if (Lookup.TryGetValue(key, out var target)) return target;
            Debug.LogWarning($"{name} has no element called '{key}'", this);
            return null;
        }

        public T Get<T>(string key) where T : Component
        {
            var target = Get(key);
            if (target == null) return null;
            var component = target.GetComponent<T>();
            if (component == null) Debug.LogWarning($"'{key}' on {name} has no {typeof(T).Name}", target);
            return component;
        }

        public IEnumerable<string> Keys => Lookup.Keys;
    }
}
