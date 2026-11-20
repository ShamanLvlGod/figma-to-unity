using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityFigmaBridge.Editor.Reporting;
using UnityFigmaBridge.Runtime.UI;

namespace UnityFigmaBridge.Editor.PrototypeFlow
{
    public static class BehaviourBindingManager
    {
        private const int MaxSearchDepthForTransforms = 3;

        private static Dictionary<string, List<Type>> s_ProjectBehaviourTypes;
        private static HashSet<System.Reflection.Assembly> s_ProjectAssemblies;

        public static void ResetTypeCache()
        {
            s_ProjectBehaviourTypes = null;
            s_ProjectAssemblies = null;
        }

        private static HashSet<System.Reflection.Assembly> GetProjectAssemblies()
        {
            if (s_ProjectAssemblies != null) return s_ProjectAssemblies;
            var projectAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Player))
            {
                if (assembly.sourceFiles.Any(sourceFile => sourceFile.Replace('\\', '/').StartsWith("Assets/")))
                    projectAssemblyNames.Add(assembly.name);
            }
            s_ProjectAssemblies = new HashSet<System.Reflection.Assembly>(AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => projectAssemblyNames.Contains(assembly.GetName().Name)));
            return s_ProjectAssemblies;
        }

        private static Dictionary<string, List<Type>> GetProjectBehaviourTypes()
        {
            if (s_ProjectBehaviourTypes != null) return s_ProjectBehaviourTypes;
            s_ProjectBehaviourTypes = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in GetProjectAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types.Where(type => type != null).ToArray();
                }
                foreach (var type in types)
                {
                    if (type.IsAbstract || type.IsGenericTypeDefinition || !type.IsSubclassOf(typeof(MonoBehaviour))) continue;
                    if (!s_ProjectBehaviourTypes.TryGetValue(type.Name, out var list))
                    {
                        list = new List<Type>();
                        s_ProjectBehaviourTypes[type.Name] = list;
                    }
                    list.Add(type);
                }
            }
            return s_ProjectBehaviourTypes;
        }

        public static Type GetTypeByName(string nameSpace, string name)
        {
            if (string.IsNullOrEmpty(name) || !GetProjectBehaviourTypes().TryGetValue(name, out var candidates)) return null;
            if (string.IsNullOrEmpty(nameSpace)) return candidates.Count == 1 ? candidates[0] : null;
            return candidates.FirstOrDefault(type => string.Equals(type.Namespace, nameSpace, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsProjectComponent(Component component)
        {
            return component != null && GetProjectAssemblies().Contains(component.GetType().Assembly);
        }

        private static void BindBehaviourToRoot(GameObject rootObject, FigmaImportProcessData importProcessData)
        {
            var matchingType = GetTypeByName(importProcessData.Settings.ScreenBindingNamespace, rootObject.name);
            if (matchingType == null) return;
            var attachedBehaviour = rootObject.GetComponent(matchingType);
            if (attachedBehaviour == null) attachedBehaviour = rootObject.AddComponent(matchingType);
            if (attachedBehaviour == null)
            {
                FigmaImportReport.Warning($"Could not attach script {matchingType.Name} to '{rootObject.name}'");
                return;
            }
            BindFieldsForComponent(rootObject, attachedBehaviour);
        }

        private static void AddSpecialBehaviours(GameObject gameObject)
        {
            if (gameObject.name.Equals("SafeArea", StringComparison.OrdinalIgnoreCase) && gameObject.GetComponent<SafeArea>() == null)
                gameObject.AddComponent<SafeArea>();
            foreach (Transform child in gameObject.transform) AddSpecialBehaviours(child.gameObject);
        }

        public static void BindFieldsForComponent(GameObject gameObject, Component component)
        {
            if (!IsProjectComponent(component)) return;
            var componentType = component.GetType();
            var serializedFields = componentType
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(field => field.GetCustomAttribute(typeof(SerializeField)) != null)
                .Concat(componentType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                .ToList();

            foreach (var field in serializedFields)
            {
                if (field.GetValue(component) is UnityEngine.Object existing && existing != null) continue;
                var matchingTransform = GetChildTransformByName(gameObject.transform, field.Name, MaxSearchDepthForTransforms);
                if (matchingTransform == null) continue;
                var fieldType = field.FieldType;
                if (fieldType == typeof(GameObject))
                {
                    field.SetValue(component, matchingTransform.gameObject);
                }
                else if (typeof(Component).IsAssignableFrom(fieldType))
                {
                    var matchingComponent = matchingTransform.GetComponent(fieldType);
                    if (matchingComponent != null) field.SetValue(component, matchingComponent);
                }
            }

            var methods = componentType.GetMethods()
                .Where(method => method.GetCustomAttributes(typeof(BindFigmaButtonPress), false).Length > 0);
            foreach (var method in methods)
            {
                var attribute = (BindFigmaButtonPress)method.GetCustomAttribute(typeof(BindFigmaButtonPress));
                var targetTransform = GetChildTransformByName(gameObject.transform, attribute.TargetButtonName, MaxSearchDepthForTransforms);
                var targetButton = targetTransform != null ? targetTransform.GetComponent<Button>() : null;
                if (targetButton == null) continue;
                if (HasPersistentListener(targetButton.onClick, component, method.Name)) continue;
                var action = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), component, method, false);
                if (action == null) continue;
                UnityEventTools.AddPersistentListener(targetButton.onClick, action);
            }
        }

        private static bool HasPersistentListener(UnityEventBase unityEvent, UnityEngine.Object target, string methodName)
        {
            for (var i = 0; i < unityEvent.GetPersistentEventCount(); i++)
            {
                if (unityEvent.GetPersistentTarget(i) == target && unityEvent.GetPersistentMethodName(i) == methodName) return true;
            }
            return false;
        }

        private static Transform GetChildTransformByName(Transform transform, string childName, int depthSearch)
        {
            for (var i = 0; i < transform.childCount; i++)
            {
                var childTransform = transform.GetChild(i);
                if (NameMatches(childTransform.name, childName)) return childTransform;
            }
            if (depthSearch <= 0) return null;
            for (var i = 0; i < transform.childCount; i++)
            {
                var found = GetChildTransformByName(transform.GetChild(i), childName, depthSearch - 1);
                if (found != null) return found;
            }
            return null;
        }

        private static bool NameMatches(string objectName, string fieldName)
        {
            var normalisedObject = Normalise(objectName);
            var normalisedField = Normalise(fieldName);
            return normalisedObject.Length > 0 && normalisedObject == normalisedField;
        }

        private static string Normalise(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var trimmed = name.StartsWith("m_") ? name.Substring(2) : name.StartsWith("_") ? name.Substring(1) : name;
            return new string(trimmed.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        public static void BindBehaviours(FigmaImportProcessData figmaImportProcessData)
        {
            ResetTypeCache();
            var prefabsToBind = figmaImportProcessData.ComponentData.AllComponentPrefabs;
            prefabsToBind.AddRange(figmaImportProcessData.ScreenPrefabs);
            foreach (var sourcePrefab in prefabsToBind.Where(prefab => prefab != null))
            {
                var prefabAssetPath = AssetDatabase.GetAssetPath(sourcePrefab);
                if (string.IsNullOrEmpty(prefabAssetPath) || !File.Exists(prefabAssetPath)) continue;
                var prefabContents = PrefabUtility.LoadPrefabContents(prefabAssetPath);
                try
                {
                    AddSpecialBehaviours(prefabContents);
                    BindBehaviourToRoot(prefabContents, figmaImportProcessData);
                    PrefabUtility.SaveAsPrefabAsset(prefabContents, prefabAssetPath);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    FigmaImportReport.Warning($"Script binding failed for {Path.GetFileNameWithoutExtension(prefabAssetPath)}: {exception.Message}", null, prefabAssetPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(prefabContents);
                }
            }
        }
    }
}
