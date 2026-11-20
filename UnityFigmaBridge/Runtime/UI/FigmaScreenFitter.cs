using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityFigmaBridge.Runtime.UI
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public class FigmaScreenFitter : UIBehaviour, ILayoutSelfController
    {
        public enum FitMode
        {
            Expand,
            Shrink,
            MatchWidthOrHeight,
        }

        private const float Epsilon = 0.0001f;

        [Tooltip("Size of the Figma frame this screen was designed at")]
        [SerializeField] private Vector2 m_DesignSize = new(3840f, 2160f);

        [Tooltip("Screen size of the whole design, used to scale frames smaller than a screen")]
        [SerializeField] private Vector2 m_DesignResolution = new(3840f, 2160f);

        [Tooltip("On: a full screen frame that fills its parent. Off: a smaller frame such as a dialog; only its scale is set, so it can be placed freely")]
        [SerializeField] private bool m_FillParent = true;

        [Tooltip("Expand keeps the whole design visible and gives extra space to the Figma constraints. Shrink fills the parent and crops. Match blends between fitting the width and the height")]
        [SerializeField] private FitMode m_Mode = FitMode.Expand;

        [Range(0f, 1f)]
        [SerializeField] private float m_Match = 0.5f;

        [System.NonSerialized] private RectTransform m_Rect;
        private DrivenRectTransformTracker m_Tracker;
        private bool m_Updating;

        public Vector2 DesignSize
        {
            get => m_DesignSize;
            set
            {
                m_DesignSize = value;
                UpdateRect();
            }
        }

        public Vector2 DesignResolution
        {
            get => m_DesignResolution;
            set
            {
                m_DesignResolution = value;
                UpdateRect();
            }
        }

        public bool FillParent
        {
            get => m_FillParent;
            set
            {
                m_FillParent = value;
                UpdateRect();
            }
        }

        public FitMode Mode
        {
            get => m_Mode;
            set
            {
                m_Mode = value;
                UpdateRect();
            }
        }

        public float Match
        {
            get => m_Match;
            set
            {
                m_Match = Mathf.Clamp01(value);
                UpdateRect();
            }
        }

        public float CurrentScale { get; private set; } = 1f;

        public bool IsFitting { get; private set; }

        private RectTransform RectTransform
        {
            get
            {
                if (m_Rect == null) m_Rect = GetComponent<RectTransform>();
                return m_Rect;
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            UpdateRect();
        }

        protected override void OnDisable()
        {
            m_Tracker.Clear();
            IsFitting = false;
            CurrentScale = 1f;
            base.OnDisable();
        }

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            UpdateRect();
        }

        protected override void OnRectTransformDimensionsChange()
        {
            UpdateRect();
        }

        protected virtual void Update()
        {
            if (!IsFitting) return;
            var reference = ReferenceRect();
            if (reference == null) return;
            if (Mathf.Abs(TargetScale(reference) - CurrentScale) > Epsilon) UpdateRect();
            else if (m_FillParent && !Approximately(reference.rect.size * (1f / CurrentScale), RectTransform.rect.size)) UpdateRect();
        }

        private Vector2 ReferenceDesignSize => m_FillParent ? m_DesignSize : m_DesignResolution;

        private float TargetScale(RectTransform reference)
        {
            var scale = ScaleFor(reference.rect.size, ReferenceDesignSize, m_Mode, m_Match);
            if (m_FillParent || RectTransform.parent == null || RectTransform.parent == reference) return scale;
            var referenceScale = reference.lossyScale.x;
            var parentScale = RectTransform.parent.lossyScale.x;
            if (Mathf.Abs(referenceScale) < Epsilon || Mathf.Abs(parentScale) < Epsilon) return scale;
            return scale * referenceScale / parentScale;
        }

        private RectTransform ReferenceRect()
        {
            var parent = RectTransform.parent as RectTransform;
            if (parent == null || m_FillParent) return parent;
            var canvas = GetComponentInParent<Canvas>();
            return canvas != null ? canvas.rootCanvas.transform as RectTransform : parent;
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            m_Match = Mathf.Clamp01(m_Match);
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null) UpdateRect();
            };
        }
#endif

        public void SetLayoutHorizontal()
        {
        }

        public void SetLayoutVertical()
        {
        }

        public static float ScaleFor(Vector2 parentSize, Vector2 designSize, FitMode mode, float match)
        {
            var widthScale = parentSize.x / designSize.x;
            var heightScale = parentSize.y / designSize.y;
            switch (mode)
            {
                case FitMode.Shrink:
                    return Mathf.Max(widthScale, heightScale);
                case FitMode.MatchWidthOrHeight:
                    return Mathf.Pow(2f, Mathf.Lerp(Mathf.Log(widthScale, 2f), Mathf.Log(heightScale, 2f), match));
                default:
                    return Mathf.Min(widthScale, heightScale);
            }
        }

        private bool ShouldFit(out RectTransform parent)
        {
            parent = ReferenceRect();
            var designSize = ReferenceDesignSize;
            if (!IsActive() || parent == null || designSize.x <= 0f || designSize.y <= 0f) return false;
            if (GetComponentInParent<FigmaPageRoot>(true) != null) return false;
#if UNITY_EDITOR
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject);
            if (stage != null && stage.prefabContentsRoot == gameObject) return false;
#endif
            var parentSize = parent.rect.size;
            return parentSize.x > 0f && parentSize.y > 0f;
        }

        private void UpdateRect()
        {
            if (m_Updating) return;
            m_Updating = true;
            try
            {
                m_Tracker.Clear();
                if (!ShouldFit(out var parent))
                {
                    IsFitting = false;
                    CurrentScale = 1f;
                    return;
                }
                var parentSize = parent.rect.size;
                var scale = TargetScale(parent);
                if (scale <= 0f || float.IsNaN(scale) || float.IsInfinity(scale)) return;
                var rect = RectTransform;
                var localScale = new Vector3(scale, scale, 1f);
                if (!m_FillParent)
                {
                    m_Tracker.Add(this, rect, DrivenTransformProperties.Scale);
                    if ((rect.localScale - localScale).sqrMagnitude > Epsilon * Epsilon) rect.localScale = localScale;
                    CurrentScale = scale;
                    IsFitting = true;
                    return;
                }
                m_Tracker.Add(this, rect, DrivenTransformProperties.Anchors | DrivenTransformProperties.AnchoredPosition | DrivenTransformProperties.SizeDelta | DrivenTransformProperties.Pivot | DrivenTransformProperties.Scale);
                if (rect.anchorMin != Vector2.zero) rect.anchorMin = Vector2.zero;
                if (rect.anchorMax != Vector2.one) rect.anchorMax = Vector2.one;
                var centre = new Vector2(0.5f, 0.5f);
                if (rect.pivot != centre) rect.pivot = centre;
                if (rect.anchoredPosition != Vector2.zero) rect.anchoredPosition = Vector2.zero;
                var sizeDelta = parentSize / scale - parentSize;
                if (!Approximately(rect.sizeDelta, sizeDelta)) rect.sizeDelta = sizeDelta;
                if ((rect.localScale - localScale).sqrMagnitude > Epsilon * Epsilon) rect.localScale = localScale;
                CurrentScale = scale;
                IsFitting = true;
            }
            finally
            {
                m_Updating = false;
            }
        }

        private static bool Approximately(Vector2 a, Vector2 b) => Mathf.Abs(a.x - b.x) < 0.01f && Mathf.Abs(a.y - b.y) < 0.01f;
    }
}
