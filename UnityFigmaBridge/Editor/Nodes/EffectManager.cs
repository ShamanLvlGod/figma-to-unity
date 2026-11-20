using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityFigmaBridge.Editor.FigmaApi;
using UnityFigmaBridge.Editor.Reporting;

namespace UnityFigmaBridge.Editor.Nodes
{
    public static class EffectManager
    {
        public static void ApplyAllFigmaEffectsToUnityNode(GameObject nodeGameObject, Node node, FigmaImportProcessData figmaImportProcessData)
        {
            var visibleEffects = node.effects?.Where(effect => effect != null && effect.visible).ToList();
            var dropShadow = node.type == NodeType.TEXT ? null : visibleEffects?.FirstOrDefault(effect => effect.type == Effect.EffectType.DROP_SHADOW);
            var existingShadow = nodeGameObject.GetComponents<Shadow>().FirstOrDefault(shadow => shadow.GetType() == typeof(Shadow));

            if (dropShadow != null && nodeGameObject.GetComponent<Graphic>() != null)
            {
                if (existingShadow == null) existingShadow = nodeGameObject.AddComponent<Shadow>();
                existingShadow.effectDistance = dropShadow.offset != null ? new Vector2(dropShadow.offset.x, -dropShadow.offset.y) : Vector2.zero;
                existingShadow.effectColor = dropShadow.color != null ? FigmaDataUtils.ToUnityColor(dropShadow.color) : new UnityEngine.Color(0, 0, 0, 0.25f);
            }
            else if (existingShadow != null)
            {
                Object.DestroyImmediate(existingShadow);
            }

            if (visibleEffects == null || node.figmaBridge?.flatten == true) return;
            foreach (var effect in visibleEffects.Where(effect => effect.type != Effect.EffectType.DROP_SHADOW))
                FigmaImportReport.Info($"Effect {effect.type} isn't drawn natively; flatten the layer to keep it", node);
        }
    }
}
