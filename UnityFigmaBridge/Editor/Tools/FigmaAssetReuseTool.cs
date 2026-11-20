using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityFigmaBridge.Editor.Utils;
using UnityFigmaBridge.Runtime.UI;
using Object = UnityEngine.Object;

namespace UnityFigmaBridge.Editor.Tools
{
    public static class FigmaAssetReuseTool
    {
        private static readonly string[] PresetFloatProperties = { "_UnderlayOffsetX", "_UnderlayOffsetY", "_UnderlayDilate", "_UnderlaySoftness", "_OutlineWidth", "_OutlineSoftness", "_FaceDilate" };
        private static readonly string[] PresetColorProperties = { "_UnderlayColor", "_OutlineColor", "_FaceColor" };

        public static List<string> GeneratedPrefabPaths()
        {
            var paths = new List<string>();
            foreach (var folder in new[] { FigmaPaths.FigmaComponentPrefabFolder, FigmaPaths.FigmaScreenPrefabFolder, FigmaPaths.FigmaPagePrefabFolder })
            {
                if (!AssetDatabase.IsValidFolder(folder)) continue;
                paths.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(path => path));
            }
            return paths.Distinct().ToList();
        }

        public static HashSet<string> SpritesUsedByFigmaImages(IEnumerable<string> prefabPaths, bool tiledOnly)
        {
            var used = new HashSet<string>();
            foreach (var prefabPath in prefabPaths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) continue;
                foreach (var image in prefab.GetComponentsInChildren<FigmaImage>(true))
                {
                    if (image.sprite == null || (tiledOnly && image.ScaleMode != FigmaImage.ImageScaleMode.Tile)) continue;
                    used.Add(AssetDatabase.GetAssetPath(image.sprite));
                }
            }
            return used;
        }

        public class ReusePlan
        {
            public List<string> PrefabPaths = new();
            public Dictionary<string, string> ImageMatches = new();
            public Dictionary<TMP_FontAsset, TMP_FontAsset> FontMatches = new();
            public List<string> Problems = new();
        }

        [MenuItem("Tools/Figma Bridge/Use Project Fonts and Images")]
        private static void UseProjectAssets()
        {
            var plan = Analyse();
            if (plan == null) return;
            if (plan.ImageMatches.Count == 0 && plan.FontMatches.Count == 0)
            {
                EditorUtility.DisplayDialog("Figma Bridge", "No imported font or image matches one already in the project." + ProblemText(plan.Problems), "OK");
                return;
            }
            var summary = new StringBuilder();
            foreach (var pair in plan.FontMatches) summary.AppendLine($"{pair.Key.name} -> {AssetDatabase.GetAssetPath(pair.Value)}");
            foreach (var pair in plan.ImageMatches.Take(12)) summary.AppendLine($"{Path.GetFileName(pair.Key)} -> {pair.Value}");
            if (plan.ImageMatches.Count > 12) summary.AppendLine($"...and {plan.ImageMatches.Count - 12} more images");
            if (!EditorUtility.DisplayDialog("Use project fonts and images?",
                    $"{plan.FontMatches.Count} fonts and {plan.ImageMatches.Count} images imported from Figma already exist in the project. The imported UI will use the project copies, and future imports will too.\n\n{summary}",
                    "Use project copies", "Cancel"))
                return;
            var (changedPrefabs, movedImages) = Apply(plan);
            Debug.Log($"Figma Bridge: {changedPrefabs} prefabs now use {plan.FontMatches.Count} project fonts and {plan.ImageMatches.Count} project images; {movedImages} duplicate images were moved out of the project");
            var removable = RemovableFontFiles(plan.FontMatches.Keys.ToList(), out var toTrash);
            if (removable.Count == 0)
            {
                if (plan.Problems.Count > 0) EditorUtility.DisplayDialog("Figma Bridge", "Done." + ProblemText(plan.Problems), "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Remove the Figma font copies?",
                    $"Nothing uses these Figma font copies any more:\n\n{string.Join("\n", removable.Select(Path.GetFileName))}\n\nMove them and their {toTrash.Length - removable.Count} related files to the trash?" + ProblemText(plan.Problems),
                    "Move to trash", "Keep"))
                return;
            var failed = new List<string>();
            AssetDatabase.MoveAssetsToTrash(toTrash, failed);
            Debug.Log($"Figma Bridge: moved {toTrash.Length - failed.Count} unused Figma font files to the trash");
        }

        public static ReusePlan Analyse()
        {
            var plan = new ReusePlan { PrefabPaths = GeneratedPrefabPaths() };
            try
            {
                var tiled = SpritesUsedByFigmaImages(plan.PrefabPaths, true);
                var imagePaths = AssetDatabase.IsValidFolder(FigmaPaths.FigmaImageFillFolder)
                    ? AssetDatabase.FindAssets("t:Texture2D", new[] { FigmaPaths.FigmaImageFillFolder }).Select(AssetDatabase.GUIDToAssetPath).ToList()
                    : new List<string>();
                EditorUtility.DisplayProgressBar("Figma Bridge", "Indexing project images", 0f);
                var candidates = new FigmaAssetReuse.ProjectImageCandidates();
                for (var i = 0; i < imagePaths.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Figma Bridge", $"Comparing {Path.GetFileName(imagePaths[i])} with project images", (float)i / imagePaths.Count)) return null;
                    var match = candidates.FindMatch(imagePaths[i], tiled.Contains(imagePaths[i]));
                    if (match != null) plan.ImageMatches[imagePaths[i]] = match;
                }

                EditorUtility.DisplayProgressBar("Figma Bridge", "Checking fonts", 1f);
                var charactersByFont = CharactersByFont(plan.PrefabPaths);
                var figmaFonts = AssetDatabase.IsValidFolder(FigmaPaths.FigmaFontsFolder)
                    ? AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { FigmaPaths.FigmaFontsFolder }).Select(AssetDatabase.GUIDToAssetPath).Select(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>).Where(font => font != null).ToList()
                    : new List<TMP_FontAsset>();
                foreach (var figmaFont in figmaFonts)
                {
                    var family = figmaFont.faceInfo.familyName;
                    var weight = FigmaAssetReuse.WeightFromStyle(figmaFont.faceInfo.styleName);
                    var projectFont = FigmaAssetReuse.FindFontOverride(family, weight);
                    if (projectFont == null)
                    {
                        var characters = charactersByFont.TryGetValue(figmaFont, out var used) ? used.ToString() : string.Empty;
                        projectFont = FigmaAssetReuse.FindProjectFont(family, weight, characters, out var problem);
                        if (problem != null) plan.Problems.Add(problem);
                    }
                    if (projectFont != null && projectFont != figmaFont) plan.FontMatches[figmaFont] = projectFont;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return plan;
        }

        public static (int changedPrefabs, int movedImages) Apply(ReusePlan plan)
        {
            var registry = FigmaBridgeRegistry.Load();
            var swaps = new Dictionary<Object, Object>();
            foreach (var pair in plan.ImageMatches)
            {
                var importedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(pair.Key);
                var projectSprite = AssetDatabase.LoadAssetAtPath<Sprite>(pair.Value);
                if (importedSprite != null && projectSprite != null) swaps[importedSprite] = projectSprite;
                var importedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(pair.Key);
                var projectTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(pair.Value);
                if (importedTexture != null && projectTexture != null) swaps[importedTexture] = projectTexture;
            }
            foreach (var pair in plan.FontMatches) AddFontSwaps(pair.Key, pair.Value, swaps);

            var changedPrefabs = 0;
            try
            {
                for (var i = 0; i < plan.PrefabPaths.Count; i++)
                {
                    var path = plan.PrefabPaths[i];
                    EditorUtility.DisplayProgressBar("Figma Bridge", $"Updating {Path.GetFileName(path)}", (float)i / plan.PrefabPaths.Count);
                    var pristine = registry.generatedHashes.TryGetValue(path, out var recordedHash) && recordedHash == FigmaBridgeRegistry.HashFile(path);
                    var contents = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        if (!Remap(contents, swaps)) continue;
                        PrefabUtility.SaveAsPrefabAsset(contents, path);
                        changedPrefabs++;
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                    }
                    if (pristine) registry.generatedHashes[path] = FigmaBridgeRegistry.HashFile(path);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var externallyUsed = DirectlyReferencedOutsideFigma(new HashSet<string>(plan.ImageMatches.Keys));
            var movedImages = 0;
            foreach (var pair in plan.ImageMatches)
            {
                var hash = Path.GetFileNameWithoutExtension(pair.Key);
                registry.reusedImages[hash] = AssetDatabase.AssetPathToGUID(pair.Value);
                if (externallyUsed.Contains(pair.Key)) continue;
                FigmaAssetReuse.MoveReusedImageToCache(hash, pair.Key);
                movedImages++;
            }
            registry.Save();
            return (changedPrefabs, movedImages);
        }

        private static string ProblemText(List<string> problems) =>
            problems.Count == 0 ? string.Empty : "\n\nNot matched:\n" + string.Join("\n", problems.Distinct().Take(8));

        private static Dictionary<TMP_FontAsset, StringBuilder> CharactersByFont(IEnumerable<string> prefabPaths)
        {
            var result = new Dictionary<TMP_FontAsset, StringBuilder>();
            foreach (var prefabPath in prefabPaths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) continue;
                foreach (var text in prefab.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (text.font == null) continue;
                    if (!result.TryGetValue(text.font, out var builder)) result[text.font] = builder = new StringBuilder();
                    builder.Append(text.text);
                }
            }
            return result;
        }

        private static void AddFontSwaps(TMP_FontAsset figmaFont, TMP_FontAsset projectFont, Dictionary<Object, Object> swaps)
        {
            swaps[figmaFont] = projectFont;
            if (figmaFont.material != null && projectFont.material != null) swaps[figmaFont.material] = projectFont.material;
            var atlas = figmaFont.atlasTexture;
            if (atlas == null || !AssetDatabase.IsValidFolder(FigmaPaths.FigmaFontMaterialPresetsFolder)) return;
            foreach (var presetPath in AssetDatabase.FindAssets("t:Material", new[] { FigmaPaths.FigmaFontMaterialPresetsFolder }).Select(AssetDatabase.GUIDToAssetPath))
            {
                var preset = AssetDatabase.LoadAssetAtPath<Material>(presetPath);
                if (preset == null || !preset.HasProperty("_MainTex") || preset.GetTexture("_MainTex") != atlas) continue;
                swaps[preset] = DerivedPreset(preset, projectFont);
            }
        }

        private static Material DerivedPreset(Material figmaPreset, TMP_FontAsset projectFont)
        {
            var path = $"{FigmaPaths.FigmaFontMaterialPresetsFolder}/{FigmaPaths.SafeFileName(projectFont.name)}_{figmaPreset.name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            var preset = new Material(projectFont.material) { name = Path.GetFileNameWithoutExtension(path), shader = figmaPreset.shader };
            foreach (var property in PresetFloatProperties)
            {
                if (figmaPreset.HasProperty(property) && preset.HasProperty(property)) preset.SetFloat(property, figmaPreset.GetFloat(property));
            }
            foreach (var property in PresetColorProperties)
            {
                if (figmaPreset.HasProperty(property) && preset.HasProperty(property)) preset.SetColor(property, figmaPreset.GetColor(property));
            }
            preset.shaderKeywords = figmaPreset.shaderKeywords;
            AssetDatabase.CreateAsset(preset, path);
            return preset;
        }

        public static bool Remap(GameObject root, Dictionary<Object, Object> swaps)
        {
            var changed = false;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                var serializedObject = new SerializedObject(component);
                var property = serializedObject.GetIterator();
                var componentChanged = false;
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    if (value == null || !swaps.TryGetValue(value, out var replacement)) continue;
                    property.objectReferenceValue = replacement;
                    componentChanged = true;
                }
                if (!componentChanged) continue;
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                changed = true;
            }
            return changed;
        }

        private static HashSet<string> DirectlyReferencedOutsideFigma(HashSet<string> assetPaths)
        {
            var referenced = new HashSet<string>();
            if (assetPaths.Count == 0) return referenced;
            var users = AssetDatabase.FindAssets("t:Prefab t:Scene t:ScriptableObject t:Material", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !FigmaAssetReuse.IsGeneratedAsset(path))
                .Distinct()
                .ToList();
            try
            {
                for (var i = 0; i < users.Count; i++)
                {
                    if (i % 50 == 0) EditorUtility.DisplayProgressBar("Figma Bridge", "Checking what else uses the imported copies", (float)i / users.Count);
                    foreach (var dependency in AssetDatabase.GetDependencies(users[i], false))
                    {
                        if (assetPaths.Contains(dependency)) referenced.Add(dependency);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return referenced;
        }

        private static IEnumerable<string> RelatedPresets(TMP_FontAsset font)
        {
            var atlas = font.atlasTexture;
            if (atlas == null || !AssetDatabase.IsValidFolder(FigmaPaths.FigmaFontMaterialPresetsFolder)) yield break;
            foreach (var presetPath in AssetDatabase.FindAssets("t:Material", new[] { FigmaPaths.FigmaFontMaterialPresetsFolder }).Select(AssetDatabase.GUIDToAssetPath))
            {
                var preset = AssetDatabase.LoadAssetAtPath<Material>(presetPath);
                if (preset != null && preset.HasProperty("_MainTex") && preset.GetTexture("_MainTex") == atlas) yield return presetPath;
            }
        }

        public static List<string> RemovableFontFiles(List<TMP_FontAsset> replacedFonts, out string[] toTrash)
        {
            var fontPaths = replacedFonts.Select(AssetDatabase.GetAssetPath).Where(path => !string.IsNullOrEmpty(path)).ToList();
            var related = new HashSet<string>(fontPaths);
            foreach (var font in replacedFonts)
            {
                if (font.sourceFontFile != null)
                {
                    var sourcePath = AssetDatabase.GetAssetPath(font.sourceFontFile);
                    if (FigmaAssetReuse.IsGeneratedAsset(sourcePath)) related.Add(sourcePath);
                }
                related.UnionWith(RelatedPresets(font));
            }
            var stillUsed = new HashSet<string>();
            var users = AssetDatabase.FindAssets("t:Prefab t:Scene t:ScriptableObject t:Material", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !related.Contains(path))
                .Distinct();
            foreach (var user in users)
            {
                foreach (var dependency in AssetDatabase.GetDependencies(user, false))
                {
                    if (related.Contains(dependency)) stillUsed.Add(dependency);
                }
            }
            var removableFonts = replacedFonts
                .Where(font => !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(font)) && !stillUsed.Contains(AssetDatabase.GetAssetPath(font)) && !RelatedPresets(font).Any(stillUsed.Contains))
                .ToList();
            var keptFonts = replacedFonts.Except(removableFonts).ToList();
            var keptSources = new HashSet<string>(keptFonts.Where(font => font.sourceFontFile != null).Select(font => AssetDatabase.GetAssetPath(font.sourceFontFile)));
            var trash = new List<string>();
            foreach (var font in removableFonts)
            {
                trash.Add(AssetDatabase.GetAssetPath(font));
                trash.AddRange(RelatedPresets(font));
                if (font.sourceFontFile == null) continue;
                var sourcePath = AssetDatabase.GetAssetPath(font.sourceFontFile);
                if (FigmaAssetReuse.IsGeneratedAsset(sourcePath) && !stillUsed.Contains(sourcePath) && !keptSources.Contains(sourcePath)) trash.Add(sourcePath);
            }
            toTrash = trash.Distinct().ToArray();
            return removableFonts.Select(AssetDatabase.GetAssetPath).ToList();
        }
    }
}
