using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityFigmaBridge.Editor.FigmaApi;
using UnityFigmaBridge.Editor.Reporting;
using UnityFigmaBridge.Editor.Utils;
using UnityFigmaBridge.Runtime.UI;
using Object = UnityEngine.Object;

namespace UnityFigmaBridge.Editor.Components
{
    public enum StatefulKind
    {
        Button,
        Toggle,
        Switcher,
    }

    public class StatefulVariantEntry
    {
        public GameObject SetPrefab;
        public string StateName;
        public StatefulKind Kind;
        public bool InitialOn;
    }

    public static class StatefulComponentBuilder
    {
        private static readonly string[] StateAxisNames = { "state", "status", "interaction", "mode" };
        private static readonly string[] ToggleAxisNames = { "checked", "value", "toggle", "ison", "on", "toggled" };

        private static readonly Dictionary<string, string> StateSynonyms = new(StringComparer.OrdinalIgnoreCase)
        {
            { "default", FigmaStatefulButton.NormalState },
            { "default state", FigmaStatefulButton.NormalState },
            { "normal", FigmaStatefulButton.NormalState },
            { "idle", FigmaStatefulButton.NormalState },
            { "rest", FigmaStatefulButton.NormalState },
            { "enabled", FigmaStatefulButton.NormalState },
            { "hover", FigmaStatefulButton.HighlightedState },
            { "hover state", FigmaStatefulButton.HighlightedState },
            { "hovered", FigmaStatefulButton.HighlightedState },
            { "highlight", FigmaStatefulButton.HighlightedState },
            { "highlighted", FigmaStatefulButton.HighlightedState },
            { "over", FigmaStatefulButton.HighlightedState },
            { "pressed", FigmaStatefulButton.PressedState },
            { "press", FigmaStatefulButton.PressedState },
            { "active", FigmaStatefulButton.PressedState },
            { "down", FigmaStatefulButton.PressedState },
            { "clicked", FigmaStatefulButton.PressedState },
            { "selected", FigmaStatefulButton.SelectedState },
            { "focus", FigmaStatefulButton.SelectedState },
            { "focused", FigmaStatefulButton.SelectedState },
            { "disabled", FigmaStatefulButton.DisabledState },
            { "inactive", FigmaStatefulButton.DisabledState },
        };

        private static readonly HashSet<string> OnValues = new(StringComparer.OrdinalIgnoreCase) { "on", "true", "checked", "yes" };
        private static readonly HashSet<string> OffValues = new(StringComparer.OrdinalIgnoreCase) { "off", "false", "unchecked", "no" };

        private class VariantInfo
        {
            public Node Node;
            public GameObject Prefab;
            public string State;
            public bool? ToggleValue;
            public string GroupKey;
        }

        public static void BuildStatefulComponentSets(FigmaImportProcessData figmaImportProcessData)
        {
            var componentSets = figmaImportProcessData.NodeLookupDictionary.Values
                .Where(node => node != null && node.type == NodeType.COMPONENT_SET)
                .ToList();
            foreach (var setNode in componentSets)
            {
                try
                {
                    BuildStatefulSet(setNode, figmaImportProcessData);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    FigmaImportReport.Warning($"Component set states could not be built ({exception.Message})", setNode);
                }
            }
        }

        public static string GetStateName(string variantName) => ParseVariant(variantName).state;

        public static bool IsInteractionState(string stateName)
        {
            return stateName == FigmaStatefulButton.NormalState
                   || stateName == FigmaStatefulButton.HighlightedState
                   || stateName == FigmaStatefulButton.PressedState
                   || stateName == FigmaStatefulButton.SelectedState
                   || stateName == FigmaStatefulButton.DisabledState;
        }

        private static (string state, bool? toggleValue, string groupKey) ParseVariant(string variantName)
        {
            var pairs = (variantName ?? string.Empty).Split(',')
                .Select(part => part.Split('='))
                .Where(part => part.Length == 2)
                .Select(part => (key: part[0].Trim(), value: part[1].Trim()))
                .ToList();
            if (pairs.Count == 0) return (NormaliseState(variantName?.Trim() ?? string.Empty), null, string.Empty);

            var statePair = pairs.FirstOrDefault(pair => StateAxisNames.Contains(pair.key.ToLowerInvariant()));
            var togglePair = pairs.FirstOrDefault(pair => ToggleAxisNames.Contains(pair.key.ToLowerInvariant().Replace(" ", "")));
            if (statePair.key == null && togglePair.key == null && pairs.Count == 1) statePair = pairs[0];

            bool? toggleValue = null;
            if (togglePair.key != null)
            {
                if (OnValues.Contains(togglePair.value)) toggleValue = true;
                else if (OffValues.Contains(togglePair.value)) toggleValue = false;
            }

            var stateValue = statePair.key != null ? statePair.value : null;
            if (toggleValue == null && stateValue != null)
            {
                if (OnValues.Contains(stateValue)) { toggleValue = true; stateValue = null; }
                else if (OffValues.Contains(stateValue)) { toggleValue = false; stateValue = null; }
            }

            var state = stateValue != null ? NormaliseState(stateValue) : FigmaStatefulButton.NormalState;
            var groupKey = string.Join(", ", pairs
                .Where(pair => pair.key != statePair.key && pair.key != togglePair.key)
                .OrderBy(pair => pair.key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.key}={pair.value}"));
            return (state, toggleValue, groupKey);
        }

        private static string NormaliseState(string value)
        {
            return StateSynonyms.TryGetValue(value.Trim(), out var mapped) ? mapped : value.Trim();
        }

        private static void BuildStatefulSet(Node setNode, FigmaImportProcessData figmaImportProcessData)
        {
            if (setNode.children == null) return;
            var variants = new List<VariantInfo>();
            foreach (var variantNode in setNode.children)
            {
                if (variantNode.type != NodeType.COMPONENT) continue;
                var variantPrefab = figmaImportProcessData.ComponentData.GetComponentPrefab(variantNode.id);
                if (variantPrefab == null) continue;
                var (state, toggleValue, groupKey) = ParseVariant(variantNode.name);
                variants.Add(new VariantInfo { Node = variantNode, Prefab = variantPrefab, State = state, ToggleValue = toggleValue, GroupKey = groupKey });
            }

            var setRole = FigmaBridgeTags.GetRole(setNode);
            foreach (var group in variants.GroupBy(variant => variant.GroupKey))
            {
                var groupVariants = group.ToList();
                var isToggle = setRole == FigmaBridgeTags.RoleToggle
                               || (groupVariants.Any(v => v.ToggleValue == true) && groupVariants.Any(v => v.ToggleValue == false));
                StatefulKind kind;
                if (isToggle)
                {
                    kind = StatefulKind.Toggle;
                }
                else
                {
                    var states = groupVariants.Select(v => v.State).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (states.Count < 2 || !states.Contains(FigmaStatefulButton.NormalState)) continue;
                    var hasInteractionState = states.Any(state => state != FigmaStatefulButton.NormalState && IsInteractionState(state));
                    kind = hasInteractionState || setRole == FigmaBridgeTags.RoleButton ? StatefulKind.Button : StatefulKind.Switcher;
                }
                BuildGroupPrefab(setNode, group.Key, groupVariants, kind, figmaImportProcessData);
            }
        }

        private static string VisualKey(VariantInfo variant, StatefulKind kind)
        {
            if (kind != StatefulKind.Toggle) return variant.State;
            var prefix = variant.ToggleValue == true ? FigmaStatefulToggle.OnPrefix : FigmaStatefulToggle.OffPrefix;
            return $"{prefix}/{variant.State}";
        }

        private static void BuildGroupPrefab(Node setNode, string groupKey, List<VariantInfo> variants, StatefulKind kind, FigmaImportProcessData figmaImportProcessData)
        {
            foreach (var variant in variants) StripSelectablesFromPrefabRoot(variant.Prefab);

            var defaultVariant = kind == StatefulKind.Toggle
                ? variants.FirstOrDefault(v => v.ToggleValue == false && v.State == FigmaStatefulButton.NormalState) ?? variants[0]
                : variants.FirstOrDefault(v => v.State == FigmaStatefulButton.NormalState) ?? variants[0];
            var defaultSize = GetNodeSize(defaultVariant.Node);

            var setName = FigmaBridgeTags.CleanName(setNode.name);
            var prefabName = string.IsNullOrEmpty(groupKey) ? setName : $"{setName}-{groupKey}";
            var root = new GameObject(prefabName, typeof(RectTransform));
            var rootTransform = (RectTransform)root.transform;
            rootTransform.pivot = new Vector2(0.5f, 0.5f);
            rootTransform.sizeDelta = defaultSize;
            root.AddComponent<FigmaNodeObject>().NodeId = setNode.id;

            var visuals = new List<FigmaStatefulButton.StateVisual>();
            foreach (var variant in variants)
            {
                var key = VisualKey(variant, kind);
                if (visuals.Any(visual => string.Equals(visual.Name, key, StringComparison.OrdinalIgnoreCase))) continue;
                var stateObject = (GameObject)PrefabUtility.InstantiatePrefab(variant.Prefab, root.transform);
                stateObject.name = key.Replace('/', ' ');
                var stateTransform = (RectTransform)stateObject.transform;
                var halfSizeDelta = (GetNodeSize(variant.Node) - defaultSize) * 0.5f;
                stateTransform.anchorMin = Vector2.zero;
                stateTransform.anchorMax = Vector2.one;
                stateTransform.pivot = new Vector2(0.5f, 0.5f);
                stateTransform.localRotation = Quaternion.identity;
                stateTransform.localScale = Vector3.one;
                stateTransform.offsetMin = -halfSizeDelta;
                stateTransform.offsetMax = halfSizeDelta;
                stateObject.SetActive(variant == defaultVariant);
                visuals.Add(new FigmaStatefulButton.StateVisual { Name = key, Target = stateObject });
            }

            switch (kind)
            {
                case StatefulKind.Button:
                    var button = root.AddComponent<FigmaStatefulButton>();
                    button.transition = Selectable.Transition.None;
                    button.StateVisuals = visuals.ToArray();
                    break;
                case StatefulKind.Toggle:
                    var toggle = root.AddComponent<FigmaStatefulToggle>();
                    toggle.transition = Selectable.Transition.None;
                    toggle.toggleTransition = Toggle.ToggleTransition.None;
                    toggle.graphic = null;
                    toggle.StateVisuals = visuals.ToArray();
                    break;
                default:
                    var switcher = root.AddComponent<FigmaStateSwitcher>();
                    switcher.StateVisuals = visuals.ToArray();
                    break;
            }

            var nameCount = figmaImportProcessData.ComponentData.GetComponentNameCount(prefabName);
            figmaImportProcessData.ComponentData.IncrementComponentNameCount(prefabName, 1);
            var setPrefabPath = FigmaPaths.GetPathForComponentPrefab(prefabName, nameCount);
            var setPrefab = PrefabUtility.SaveAsPrefabAsset(root, setPrefabPath);
            Object.DestroyImmediate(root);

            foreach (var variant in variants)
            {
                figmaImportProcessData.ComponentData.StatefulVariants[variant.Node.id] = new StatefulVariantEntry
                {
                    SetPrefab = setPrefab,
                    StateName = VisualKey(variant, kind),
                    Kind = kind,
                    InitialOn = variant.ToggleValue == true,
                };
            }
        }

        private static Vector2 GetNodeSize(Node node)
        {
            if (node.size != null) return new Vector2(node.size.x, node.size.y);
            return node.absoluteBoundingBox == null
                ? Vector2.zero
                : new Vector2(node.absoluteBoundingBox.width, node.absoluteBoundingBox.height);
        }

        private static void StripSelectablesFromPrefabRoot(GameObject prefab)
        {
            if (prefab.GetComponent<Selectable>() == null && prefab.GetComponent<FigmaPrototypeFlowButton>() == null) return;
            var assetPath = AssetDatabase.GetAssetPath(prefab);
            var prefabContents = PrefabUtility.LoadPrefabContents(assetPath);
            foreach (var flowButton in prefabContents.GetComponents<FigmaPrototypeFlowButton>())
                Object.DestroyImmediate(flowButton);
            foreach (var selectable in prefabContents.GetComponents<Selectable>())
                Object.DestroyImmediate(selectable);
            PrefabUtility.SaveAsPrefabAsset(prefabContents, assetPath);
            PrefabUtility.UnloadPrefabContents(prefabContents);
        }
    }
}
