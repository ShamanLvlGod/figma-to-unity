using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityFigmaBridge.Runtime.UI
{
    public class FigmaDesignTokens : ScriptableObject
    {
        [Serializable]
        public class ColorToken
        {
            public string Name;
            public string Mode;
            public Color Value;
        }

        [Serializable]
        public class NumberToken
        {
            public string Name;
            public string Mode;
            public float Value;
        }

        [Serializable]
        public class TextToken
        {
            public string Name;
            public string Mode;
            public string Value;
        }

        [Serializable]
        public class TextStyleToken
        {
            public string Name;
            public string FontFamily;
            public string FontStyle;
            public float FontSize;
            public float LetterSpacing;
            public float LineHeight;
        }

        public List<string> Modes = new();
        public List<ColorToken> Colors = new();
        public List<NumberToken> Numbers = new();
        public List<TextToken> Texts = new();
        public List<TextStyleToken> TextStyles = new();

        public Color GetColor(string name, string mode = null) => Find(Colors, name, mode)?.Value ?? Color.magenta;

        public float GetNumber(string name, string mode = null) => Find(Numbers, name, mode)?.Value ?? 0f;

        public string GetText(string name, string mode = null) => Find(Texts, name, mode)?.Value;

        private static T Find<T>(List<T> tokens, string name, string mode) where T : class
        {
            T fallback = null;
            foreach (var token in tokens)
            {
                var (tokenName, tokenMode) = token switch
                {
                    ColorToken color => (color.Name, color.Mode),
                    NumberToken number => (number.Name, number.Mode),
                    TextToken text => (text.Name, text.Mode),
                    _ => (null, null),
                };
                if (!string.Equals(tokenName, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (mode == null || string.Equals(tokenMode, mode, StringComparison.OrdinalIgnoreCase)) return token;
                fallback ??= token;
            }
            return fallback;
        }
    }
}
