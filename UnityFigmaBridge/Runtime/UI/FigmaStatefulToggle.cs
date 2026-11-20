using System;
using UnityEngine;
using UnityEngine.UI;

namespace UnityFigmaBridge.Runtime.UI
{
    public class FigmaStatefulToggle : Toggle, IFigmaStateful
    {
        public const string OnPrefix = "On";
        public const string OffPrefix = "Off";

        public FigmaStatefulButton.StateVisual[] StateVisuals = Array.Empty<FigmaStatefulButton.StateVisual>();

        [SerializeField] private string m_BaseState = FigmaStatefulButton.NormalState;

        private SelectionState m_LastSelectionState = SelectionState.Normal;

        public FigmaStatefulButton.StateVisual[] Visuals => StateVisuals;

        public string BaseState
        {
            get => m_BaseState;
            set
            {
                m_BaseState = string.IsNullOrEmpty(value) ? FigmaStatefulButton.NormalState : value;
                Refresh();
            }
        }

        protected override void Awake()
        {
            base.Awake();
            onValueChanged.AddListener(_ => Refresh());
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            Refresh();
        }

        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            base.DoStateTransition(state, instant);
            m_LastSelectionState = state;
            Refresh();
        }

        public void Refresh()
        {
            var prefix = isOn ? OnPrefix : OffPrefix;
            var interaction = m_LastSelectionState switch
            {
                SelectionState.Highlighted => FigmaStatefulButton.HighlightedState,
                SelectionState.Pressed => FigmaStatefulButton.PressedState,
                SelectionState.Selected => FigmaStatefulButton.SelectedState,
                SelectionState.Disabled => FigmaStatefulButton.DisabledState,
                _ => null,
            };
            GameObject target = null;
            if (interaction != null) target = FigmaStateVisuals.Find(StateVisuals, $"{prefix}/{interaction}");
            if (target == null && interaction == FigmaStatefulButton.PressedState)
                target = FigmaStateVisuals.Find(StateVisuals, $"{prefix}/{FigmaStatefulButton.HighlightedState}");
            if (target == null) target = FigmaStateVisuals.Find(StateVisuals, $"{prefix}/{m_BaseState}");
            if (target == null) target = FigmaStateVisuals.Find(StateVisuals, $"{prefix}/{FigmaStatefulButton.NormalState}");
            if (target == null) target = FigmaStateVisuals.Find(StateVisuals, prefix);
            FigmaStateVisuals.Show(StateVisuals, target);
        }
    }
}
