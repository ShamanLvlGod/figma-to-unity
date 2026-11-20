using UnityEngine;

namespace UnityFigmaBridge.Runtime.UI
{
    [DisallowMultipleComponent]
    public class FigmaLocalizedText : MonoBehaviour
    {
        public string Key;
        [TextArea] public string SourceText;
    }
}
