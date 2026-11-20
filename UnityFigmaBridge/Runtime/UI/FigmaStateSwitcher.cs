using System;
using UnityEngine;

namespace UnityFigmaBridge.Runtime.UI
{
    public class FigmaStateSwitcher : MonoBehaviour, IFigmaStateful
    {
        public FigmaStatefulButton.StateVisual[] StateVisuals = Array.Empty<FigmaStatefulButton.StateVisual>();

        [SerializeField] private string m_BaseState = FigmaStatefulButton.NormalState;

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

        public string State
        {
            get => BaseState;
            set => BaseState = value;
        }

        private void OnEnable() => Refresh();

        public void Refresh()
        {
            var target = FigmaStateVisuals.Find(StateVisuals, m_BaseState) ?? FigmaStateVisuals.Find(StateVisuals, FigmaStatefulButton.NormalState);
            FigmaStateVisuals.Show(StateVisuals, target);
        }
    }
}
