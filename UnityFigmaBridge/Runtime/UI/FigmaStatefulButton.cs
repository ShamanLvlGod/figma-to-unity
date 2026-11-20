using System;
using UnityEngine;
using UnityEngine.UI;

namespace UnityFigmaBridge.Runtime.UI
{
    public class FigmaStatefulButton : Button, IFigmaStateful
    {
        public const string NormalState = "Normal";
        public const string HighlightedState = "Highlighted";
        public const string PressedState = "Pressed";
        public const string SelectedState = "Selected";
        public const string DisabledState = "Disabled";

        [Serializable]
        public class StateVisual
        {
            public string Name;
            public GameObject Target;
        }

        public StateVisual[] StateVisuals = Array.Empty<StateVisual>();

        [SerializeField] private string m_BaseState = NormalState;

        public string BaseState
        {
            get => m_BaseState;
            set
            {
                m_BaseState = string.IsNullOrEmpty(value) ? NormalState : value;
                ShowState(currentSelectionState);
            }
        }

        public FigmaStatefulButton.StateVisual[] Visuals => StateVisuals;

        public bool HasState(string stateName) => FindState(stateName) != null;

        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            base.DoStateTransition(state, instant);
            ShowState(state);
        }

        private void ShowState(SelectionState state)
        {
            var interactionTarget = state switch
            {
                SelectionState.Highlighted => FindState(HighlightedState),
                SelectionState.Pressed => FindState(PressedState) ?? FindState(HighlightedState),
                SelectionState.Selected => FindState(SelectedState),
                SelectionState.Disabled => FindState(DisabledState),
                _ => null
            };
            var target = interactionTarget ?? FindState(m_BaseState) ?? FindState(NormalState);
            if (target == null) return;
            foreach (var visual in StateVisuals)
            {
                if (visual.Target == null) continue;
                var shouldBeActive = visual.Target == target;
                if (visual.Target.activeSelf != shouldBeActive) visual.Target.SetActive(shouldBeActive);
            }
        }

        private GameObject FindState(string stateName)
        {
            foreach (var visual in StateVisuals)
            {
                if (visual.Target != null && string.Equals(visual.Name, stateName, StringComparison.OrdinalIgnoreCase))
                    return visual.Target;
            }
            return null;
        }
    }
}
