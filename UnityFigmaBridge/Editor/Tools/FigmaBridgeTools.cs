using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using UnityFigmaBridge.Editor.FigmaApi;
using UnityFigmaBridge.Editor.Utils;
using UnityFigmaBridge.Runtime.UI;

namespace UnityFigmaBridge.Editor.Tools
{
    public static class FigmaBridgeTools
    {
        private const int MaxAtlasSpriteSize = 2048;

        private static IEnumerable<string> ImportedImagePaths()
        {
            foreach (var folder in new[] { FigmaPaths.FigmaImageFillFolder, FigmaPaths.FigmaServerRenderedImagesFolder })
            {
                if (!AssetDatabase.IsValidFolder(folder)) continue;
                foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
                    yield return AssetDatabase.GUIDToAssetPath(guid);
            }
        }

        internal static List<string> FindUnusedImages(HashSet<string> imagePaths)
        {
            var users = AssetDatabase.FindAssets("t:Prefab t:Scene t:ScriptableObject t:Material", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !imagePaths.Contains(path))
                .ToArray();
            var used = new HashSet<string>();
            try
            {
                for (var i = 0; i < users.Length; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Figma Bridge", "Finding images that are still used", (float)i / users.Length)) return null;
                    foreach (var dependency in AssetDatabase.GetDependencies(users[i], true))
                    {
                        if (imagePaths.Contains(dependency)) used.Add(dependency);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return imagePaths.Where(path => !used.Contains(path)).OrderBy(path => path).ToList();
        }

        [MenuItem("Tools/Figma Bridge/Clean Up Unused Images")]
        private static void CleanUpUnusedImages()
        {
            var imagePaths = new HashSet<string>(ImportedImagePaths());
            if (imagePaths.Count == 0)
            {
                EditorUtility.DisplayDialog("Figma Bridge", "There are no imported Figma images yet.", "OK");
                return;
            }
            var unused = FindUnusedImages(imagePaths);
            if (unused == null) return;
            if (unused.Count == 0)
            {
                EditorUtility.DisplayDialog("Figma Bridge", "Every imported Figma image is still in use.", "OK");
                return;
            }
            var preview = string.Join("\n", unused.Take(15).Select(Path.GetFileName)) + (unused.Count > 15 ? $"\n...and {unused.Count - 15} more" : string.Empty);
            if (!EditorUtility.DisplayDialog("Move unused Figma images to the trash?", $"{unused.Count} imported images aren't used by any prefab, scene or asset:\n\n{preview}", "Move to trash", "Cancel")) return;
            var failed = new List<string>();
            AssetDatabase.MoveAssetsToTrash(unused.ToArray(), failed);
            Debug.Log($"Figma Bridge: moved {unused.Count - failed.Count} unused images to the trash");
        }

        [MenuItem("Tools/Figma Bridge/Optimise Imported Images")]
        private static void OptimiseImportedImages()
        {
            var paths = ImportedImagePaths().ToList();
            var padded = 0;
            try
            {
                for (var i = 0; i < paths.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("Figma Bridge", $"Optimising {Path.GetFileName(paths[i])}", (float)i / paths.Count);
                    var bytes = File.ReadAllBytes(paths[i]);
                    var paddedBytes = FigmaApiUtils.PadImageToMultipleOfFour(bytes);
                    if (!ReferenceEquals(paddedBytes, bytes))
                    {
                        File.WriteAllBytes(paths[i], paddedBytes);
                        AssetDatabase.ImportAsset(paths[i]);
                        padded++;
                    }
                    FigmaApiUtils.ConfigureImageImporter(paths[i], paths[i].StartsWith(FigmaPaths.FigmaImageFillFolder));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Debug.Log($"Figma Bridge: optimised {paths.Count} images ({padded} resized to a multiple of 4 so they can be compressed)");
        }

        [MenuItem("Tools/Figma Bridge/Build Sprite Atlases")]
        private static void BuildSpriteAtlases()
        {
            var screenPaths = AssetDatabase.FindAssets("t:Prefab", new[] { FigmaPaths.FigmaScreenPrefabFolder }).Select(AssetDatabase.GUIDToAssetPath).ToList();
            var imagePaths = new HashSet<string>(ImportedImagePaths());
            var shapeImages = FigmaAssetReuseTool.SpritesUsedByFigmaImages(FigmaAssetReuseTool.GeneratedPrefabPaths(), false);
            imagePaths.ExceptWith(shapeImages);
            var usage = new Dictionary<string, HashSet<string>>();
            foreach (var screenPath in screenPaths)
            {
                foreach (var dependency in AssetDatabase.GetDependencies(screenPath, true).Where(imagePaths.Contains))
                {
                    var importer = AssetImporter.GetAtPath(dependency) as TextureImporter;
                    if (importer == null) continue;
                    importer.GetSourceTextureWidthAndHeight(out var width, out var height);
                    if (width > MaxAtlasSpriteSize || height > MaxAtlasSpriteSize) continue;
                    if (!usage.TryGetValue(dependency, out var screens)) usage[dependency] = screens = new HashSet<string>();
                    screens.Add(Path.GetFileNameWithoutExtension(screenPath));
                }
            }
            var groups = usage.GroupBy(pair => pair.Value.Count == 1 ? pair.Value.First() : "Shared")
                .ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToList());
            var atlasFolder = $"{FigmaPaths.FigmaAssetsRootFolder}/Atlases";
            Directory.CreateDirectory(atlasFolder);
            var created = 0;
            foreach (var group in groups)
            {
                var atlasPath = $"{atlasFolder}/{FigmaPaths.SafeFileName(group.Key)}.spriteatlasv2";
                var atlasAsset = new SpriteAtlasAsset();
                atlasAsset.SetIncludeInBuild(true);
                atlasAsset.Add(group.Value.Select(path => (UnityEngine.Object)AssetDatabase.LoadAssetAtPath<Sprite>(path)).Where(sprite => sprite != null).ToArray());
                SpriteAtlasAsset.Save(atlasAsset, atlasPath);
                AssetDatabase.ImportAsset(atlasPath);
                if (AssetImporter.GetAtPath(atlasPath) is SpriteAtlasImporter atlasImporter)
                {
                    atlasImporter.packingSettings = new SpriteAtlasPackingSettings { enableRotation = false, enableTightPacking = false, padding = 4 };
                    atlasImporter.textureSettings = new SpriteAtlasTextureSettings { readable = false, generateMipMaps = false, sRGB = true, filterMode = FilterMode.Bilinear };
                    atlasImporter.SaveAndReimport();
                }
                created++;
            }
            Debug.Log($"Figma Bridge: built {created} sprite atlases in {atlasFolder} (images over {MaxAtlasSpriteSize} px and {shapeImages.Count} image fills drawn by FigmaImage stay separate)");
        }

        [MenuItem("Tools/Figma Bridge/Generate Screen Keys Script")]
        private static void GenerateScreenKeys()
        {
            var builder = new StringBuilder();
            builder.AppendLine("namespace FigmaBridge.Generated");
            builder.AppendLine("{");
            builder.AppendLine("    public static class FigmaScreenKeys");
            builder.AppendLine("    {");
            var screenPaths = AssetDatabase.FindAssets("t:Prefab", new[] { FigmaPaths.FigmaScreenPrefabFolder }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(path => path);
            var usedClasses = new HashSet<string>(StringComparer.Ordinal) { "FigmaScreenKeys" };
            foreach (var screenPath in screenPaths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(screenPath);
                var refs = prefab != null ? prefab.GetComponent<FigmaScreenRefs>() : null;
                if (refs == null) continue;
                var className = Identifier(prefab.name);
                for (var suffix = 2; !usedClasses.Add(className); suffix++) className = $"{Identifier(prefab.name)}{suffix}";
                builder.AppendLine($"        public static class {className}");
                builder.AppendLine("        {");
                builder.AppendLine($"            public const string PrefabPath = \"{screenPath.Replace("\\", "\\\\").Replace("\"", "\\\"")}\";");
                var used = new HashSet<string>(StringComparer.Ordinal) { "PrefabPath", className };
                foreach (var entry in refs.Entries.Where(entry => !string.IsNullOrEmpty(entry.Key)))
                {
                    var identifier = Identifier(entry.Key.Replace('/', '_'));
                    var unique = identifier;
                    for (var suffix = 2; !used.Add(unique); suffix++) unique = $"{identifier}{suffix}";
                    builder.AppendLine($"            public const string {unique} = \"{entry.Key.Replace("\\", "\\\\").Replace("\"", "\\\"")}\";");
                }
                builder.AppendLine("        }");
            }
            builder.AppendLine("    }");
            builder.AppendLine("}");
            var folder = $"{FigmaPaths.FigmaAssetsRootFolder}/Generated";
            Directory.CreateDirectory(folder);
            var path = $"{folder}/FigmaScreenKeys.cs";
            File.WriteAllText(path, builder.ToString());
            AssetDatabase.ImportAsset(path);
            Debug.Log($"Figma Bridge: wrote {path}");
        }

        private static string Identifier(string name)
        {
            var builder = new StringBuilder();
            var capitaliseNext = true;
            foreach (var character in name)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(capitaliseNext ? char.ToUpperInvariant(character) : character);
                    capitaliseNext = false;
                }
                else
                {
                    capitaliseNext = true;
                }
            }
            if (builder.Length == 0) builder.Append("Unnamed");
            if (char.IsDigit(builder[0])) builder.Insert(0, '_');
            return builder.ToString();
        }
    }
}
