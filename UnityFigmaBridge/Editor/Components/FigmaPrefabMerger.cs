using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityFigmaBridge.Editor.Reporting;
using UnityFigmaBridge.Editor.Utils;
using UnityFigmaBridge.Runtime.UI;
using Object = UnityEngine.Object;

namespace UnityFigmaBridge.Editor.Components
{
    public static class FigmaPrefabMerger
    {
        private static string SnapshotFolder => $"{FigmaPaths.FigmaAssetsRootFolder}/__MergeSnapshot";

        private static readonly HashSet<Type> ImporterOwnedTypes = new()
        {
            typeof(Transform), typeof(RectTransform), typeof(CanvasRenderer),
            typeof(FigmaNodeObject), typeof(FigmaComponentNodeMarker), typeof(FigmaImage), typeof(Image), typeof(RawImage),
            typeof(TextMeshProUGUI), typeof(TMP_SubMeshUI), typeof(LayoutElement), typeof(ContentSizeFitter), typeof(CanvasGroup),
            typeof(RectMask2D), typeof(Mask), typeof(ScrollRect), typeof(HorizontalLayoutGroup), typeof(VerticalLayoutGroup),
            typeof(GridLayoutGroup), typeof(Shadow), typeof(Outline), typeof(Button), typeof(Toggle), typeof(ToggleGroup),
            typeof(Slider), typeof(TMP_InputField), typeof(FigmaStatefulButton), typeof(FigmaStatefulToggle), typeof(FigmaStateSwitcher),
            typeof(FigmaPrototypeFlowButton), typeof(FigmaLocalizedText), typeof(FigmaScreenRefs), typeof(SafeArea), typeof(AspectRatioFitter), typeof(FigmaScreenFitter), typeof(FigmaPageRoot),
        };

        public static Dictionary<string, string> SnapshotEditedPrefabs(FigmaBridgeRegistry registry)
        {
            var snapshots = new Dictionary<string, string>();
            foreach (var pair in registry.generatedHashes)
            {
                if (!pair.Key.EndsWith(".prefab") || !File.Exists(pair.Key)) continue;
                if (FigmaBridgeRegistry.HashFile(pair.Key) == pair.Value && !registry.carriedPrefabs.Contains(pair.Key)) continue;
                if (!AssetDatabase.IsValidFolder(SnapshotFolder)) AssetDatabase.CreateFolder(FigmaPaths.FigmaAssetsRootFolder, "__MergeSnapshot");
                var snapshotPath = $"{SnapshotFolder}/{Guid.NewGuid():N}.prefab";
                if (AssetDatabase.CopyAsset(pair.Key, snapshotPath)) snapshots[pair.Key] = snapshotPath;
            }
            return snapshots;
        }

        public static List<string> MergeAll(Dictionary<string, string> snapshots, IEnumerable<string> regeneratedPaths)
        {
            var carried = new List<string>();
            if (snapshots == null || snapshots.Count == 0) return carried;
            var regenerated = new HashSet<string>(regeneratedPaths.Where(path => !string.IsNullOrEmpty(path)));
            foreach (var pair in snapshots)
            {
                if (!regenerated.Contains(pair.Key) || !File.Exists(pair.Key)) continue;
                try
                {
                    if (Merge(pair.Key, pair.Value)) carried.Add(pair.Key);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    FigmaImportReport.Warning($"Could not carry over Unity changes in {Path.GetFileNameWithoutExtension(pair.Key)}: {exception.Message}", null, pair.Key);
                }
            }
            CleanUp();
            return carried;
        }

        public static void CleanUp()
        {
            if (AssetDatabase.IsValidFolder(SnapshotFolder)) AssetDatabase.DeleteAsset(SnapshotFolder);
        }

        private static string NodeIdOf(GameObject gameObject)
        {
            var nodeObject = gameObject.GetComponent<FigmaNodeObject>();
            return nodeObject != null ? nodeObject.NodeId : null;
        }

        private static bool IsInsideNestedInstance(Transform transform, Transform root)
        {
            for (var current = transform.parent; current != null && current != root; current = current.parent)
            {
                if (PrefabUtility.IsAnyPrefabInstanceRoot(current.gameObject)) return true;
            }
            return false;
        }

        private static bool Merge(string targetPath, string snapshotPath)
        {
            var target = PrefabUtility.LoadPrefabContents(targetPath);
            var previous = PrefabUtility.LoadPrefabContents(snapshotPath);
            try
            {
                var newById = new Dictionary<string, GameObject>();
                foreach (var transform in target.GetComponentsInChildren<Transform>(true))
                {
                    var id = NodeIdOf(transform.gameObject);
                    if (!string.IsNullOrEmpty(id) && !newById.ContainsKey(id)) newById[id] = transform.gameObject;
                }

                var oldToNew = new Dictionary<Object, Object>();
                var copiedComponents = new List<Component>();
                var restoredComponents = 0;
                var restoredObjects = 0;

                foreach (var transform in previous.GetComponentsInChildren<Transform>(true))
                {
                    var id = NodeIdOf(transform.gameObject);
                    if (string.IsNullOrEmpty(id) || !newById.TryGetValue(id, out var newObject)) continue;
                    oldToNew[transform.gameObject] = newObject;
                    foreach (var oldComponent in transform.GetComponents<Component>())
                    {
                        if (oldComponent == null) continue;
                        var newComponent = MatchingComponent(oldComponent, newObject);
                        if (newComponent != null) oldToNew[oldComponent] = newComponent;
                    }
                }

                foreach (var transform in previous.GetComponentsInChildren<Transform>(true))
                {
                    if (IsInsideNestedInstance(transform, previous.transform)) continue;
                    var id = NodeIdOf(transform.gameObject);
                    if (string.IsNullOrEmpty(id) || !newById.TryGetValue(id, out var newObject)) continue;
                    foreach (var oldComponent in transform.GetComponents<Component>())
                    {
                        if (oldComponent == null || ImporterOwnedTypes.Contains(oldComponent.GetType())) continue;
                        var destination = ComponentForCopy(oldComponent, newObject);
                        if (destination == null) continue;
                        EditorUtility.CopySerialized(oldComponent, destination);
                        oldToNew[oldComponent] = destination;
                        copiedComponents.Add(destination);
                        restoredComponents++;
                    }
                }

                foreach (var transform in previous.GetComponentsInChildren<Transform>(true))
                {
                    if (transform == previous.transform || transform.GetComponent<FigmaNodeObject>() != null) continue;
                    if (transform.GetComponent<TMP_SubMeshUI>() != null || (transform.gameObject.hideFlags & HideFlags.HideInHierarchy) != 0) continue;
                    var parent = transform.parent;
                    if (parent == null || IsInsideNestedInstance(transform, previous.transform)) continue;
                    var parentId = NodeIdOf(parent.gameObject);
                    if (string.IsNullOrEmpty(parentId) || !newById.TryGetValue(parentId, out var newParent)) continue;
                    var clone = Object.Instantiate(transform.gameObject, newParent.transform, false);
                    clone.name = transform.gameObject.name;
                    clone.transform.SetSiblingIndex(Mathf.Min(transform.GetSiblingIndex(), newParent.transform.childCount - 1));
                    MapHierarchy(transform, clone.transform, oldToNew, copiedComponents);
                    restoredObjects++;
                }

                foreach (var component in copiedComponents) RemapReferences(component, oldToNew);

                if (restoredComponents + restoredObjects == 0) return false;
                PrefabUtility.SaveAsPrefabAsset(target, targetPath);
                FigmaImportReport.Info($"Carried over Unity changes in {Path.GetFileNameWithoutExtension(targetPath)}: {restoredComponents} components, {restoredObjects} objects", null, targetPath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(previous);
                PrefabUtility.UnloadPrefabContents(target);
            }
        }

        private static Component[] ExactComponents(GameObject gameObject, Type type) =>
            gameObject.GetComponents(type).Where(component => component != null && component.GetType() == type).ToArray();

        private static Component ComponentForCopy(Component oldComponent, GameObject newObject)
        {
            var type = oldComponent.GetType();
            var index = Array.IndexOf(ExactComponents(oldComponent.gameObject, type), oldComponent);
            var existing = ExactComponents(newObject, type);
            if (index >= 0 && index < existing.Length) return existing[index];
            return newObject.AddComponent(type);
        }

        private static Component MatchingComponent(Component oldComponent, GameObject newObject)
        {
            var type = oldComponent.GetType();
            var oldIndex = Array.IndexOf(ExactComponents(oldComponent.gameObject, type), oldComponent);
            var candidates = ExactComponents(newObject, type);
            return oldIndex >= 0 && oldIndex < candidates.Length ? candidates[oldIndex] : null;
        }

        private static void MapHierarchy(Transform source, Transform clone, Dictionary<Object, Object> oldToNew, List<Component> copied)
        {
            oldToNew[source.gameObject] = clone.gameObject;
            var sourceComponents = source.GetComponents<Component>();
            var cloneComponents = clone.GetComponents<Component>();
            for (var i = 0; i < Mathf.Min(sourceComponents.Length, cloneComponents.Length); i++)
            {
                if (sourceComponents[i] == null || cloneComponents[i] == null) continue;
                oldToNew[sourceComponents[i]] = cloneComponents[i];
                copied.Add(cloneComponents[i]);
            }
            for (var i = 0; i < Mathf.Min(source.childCount, clone.childCount); i++)
                MapHierarchy(source.GetChild(i), clone.GetChild(i), oldToNew, copied);
        }

        private static void RemapReferences(Component component, Dictionary<Object, Object> oldToNew)
        {
            if (component == null) return;
            var serializedObject = new SerializedObject(component);
            var property = serializedObject.GetIterator();
            var changed = false;
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference || property.objectReferenceValue == null) continue;
                if (!oldToNew.TryGetValue(property.objectReferenceValue, out var replacement)) continue;
                property.objectReferenceValue = replacement;
                changed = true;
            }
            if (changed) serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
