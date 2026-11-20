using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityFigmaBridge.Runtime.UI
{
    public class FigmaFlowMap : ScriptableObject
    {
        [Serializable]
        public class Link
        {
            public string Screen;
            public string Element;
            public string Trigger;
            public string Action;
            public string Destination;
        }

        public List<Link> Links = new();

        public IEnumerable<Link> LinksFrom(string screen)
        {
            foreach (var link in Links)
            {
                if (string.Equals(link.Screen, screen, StringComparison.OrdinalIgnoreCase)) yield return link;
            }
        }
    }
}
