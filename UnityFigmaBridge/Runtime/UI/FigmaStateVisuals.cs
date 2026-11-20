using System;
using UnityEngine;

namespace UnityFigmaBridge.Runtime.UI
{
    public interface IFigmaStateful
    {
        FigmaStatefulButton.StateVisual[] Visuals { get; }
        string BaseState { get; set; }
    }

    public static class FigmaStateVisuals
    {
        public static GameObject Find(FigmaStatefulButton.StateVisual[] visuals, string stateName)
        {
            if (visuals == null || string.IsNullOrEmpty(stateName)) return null;
            foreach (var visual in visuals)
            {
                if (visual.Target != null && string.Equals(visual.Name, stateName, StringComparison.OrdinalIgnoreCase))
                    return visual.Target;
            }
            return null;
        }

        public static void Show(FigmaStatefulButton.StateVisual[] visuals, GameObject target)
        {
            if (visuals == null || target == null) return;
            foreach (var visual in visuals)
            {
                if (visual.Target == null) continue;
                var shouldBeActive = visual.Target == target;
                if (visual.Target.activeSelf != shouldBeActive) visual.Target.SetActive(shouldBeActive);
            }
        }
    }
}
