using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityFigmaBridge.Editor.FigmaApi;

namespace UnityFigmaBridge.Editor.Utils
{
    public static class FigmaPaths
    {
        /// <summary>
        ///  Root folder for assets
        /// </summary>
        public static string FigmaAssetsRootFolder => s_RootFolder ??= ResolveRootFolder();

        private const string DefaultRootFolder = "Assets/Figma";
        private static string s_RootFolder;

        public static void RefreshRootFolder() => s_RootFolder = null;

        public static string NormaliseRootFolder(string folder)
        {
            var normalised = (folder ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
            if (normalised != "Assets" && !normalised.StartsWith("Assets/")) return DefaultRootFolder;
            return normalised == "Assets" ? DefaultRootFolder : normalised;
        }

        private static string ResolveRootFolder()
        {
            var settings = UnityFigmaBridge.Editor.Settings.UnityFigmaBridgeSettingsProvider.FindUnityBridgeSettingsAsset();
            return NormaliseRootFolder(settings != null ? settings.OutputFolder : null);
        }
        /// <summary>
        /// Assert folder to store page prefabs)
        /// </summary>
        public static string FigmaPagePrefabFolder => $"{FigmaAssetsRootFolder}/Pages";
        /// <summary>
        /// Assert folder to store flowScreen prefabs (root level frames on pages)
        /// </summary>
        public static string FigmaScreenPrefabFolder => $"{FigmaAssetsRootFolder}/Screens";
        /// <summary>
        /// Assert folder to store compoment prefabs
        /// </summary>
        public static string FigmaComponentPrefabFolder => $"{FigmaAssetsRootFolder}/Components";
        /// <summary>
        /// Asset folder to store image fills
        /// </summary>
        public static string FigmaImageFillFolder => $"{FigmaAssetsRootFolder}/ImageFills";
        /// <summary>
        /// Asset folder to store server rendered images
        /// </summary>
        public static string FigmaServerRenderedImagesFolder => $"{FigmaAssetsRootFolder}/ServerRenderedImages";
        
        /// <summary>
        /// Asset folder to store Font material presets
        /// </summary>
        public static string FigmaFontMaterialPresetsFolder => $"{FigmaAssetsRootFolder}/FontMaterialPresets";
        
        /// <summary>
        /// Asset folder to store Font assets (TTF and generated TMP fonts)
        /// </summary>
        public static string FigmaFontsFolder => $"{FigmaAssetsRootFolder}/Fonts";
        
        
        public static string GetPathForImageFill(string imageId)
        {
            return $"{FigmaPaths.FigmaImageFillFolder}/{imageId}.png";
        }
        
        public static string GetPathForServerRenderedImage(string nodeId,
            List<ServerRenderNodeData> serverRenderNodeData)
        {
            var matchingEntry = serverRenderNodeData.FirstOrDefault((node) => node.SourceNode.id == nodeId);
            switch (matchingEntry.RenderType)
            {
                case ServerRenderType.Export:
                    return $"Assets/{matchingEntry.SourceNode.name}.png";
                default:
                    var safeNodeId = FigmaDataUtils.ReplaceUnsafeFileCharactersForNodeId(nodeId);
                    return $"{FigmaPaths.FigmaServerRenderedImagesFolder}/{safeNodeId}.png";
                   
            }
        }

        public static string GetPathForScreenPrefab(Node node,int duplicateCount)
        {
            return $"{FigmaScreenPrefabFolder}/{GetFileNameForNode(node,duplicateCount)}.prefab";
        }
        
        public static string GetPathForPagePrefab(Node node,int duplicateCount)
        {
            return $"{FigmaPagePrefabFolder}/{GetFileNameForNode(node,duplicateCount)}.prefab";
        }
        
        public static string GetPathForComponentPrefab(string nodeName,int duplicateCount)
        {
            // If name already used, create a unique name
            if (duplicateCount > 0) nodeName += $"_{duplicateCount}";
            nodeName = ReplaceUnsafeCharacters(nodeName);
            return $"{FigmaComponentPrefabFolder}/{nodeName}.prefab";
        }
        
        public static string GetFileNameForNode(Node node,int duplicateCount)
        {
            var safeNodeTitle=ReplaceUnsafeCharacters(node.name);
            // If name already used, create a unique name
            if (duplicateCount > 0) safeNodeTitle += $"_{duplicateCount}";
            return safeNodeTitle;
        }

        public static string SafeFileName(string name) => ReplaceUnsafeCharacters(name);

        private static string ReplaceUnsafeCharacters(string inputFilename)
        {
            // We want to trim spaces from start and end of filename, or we'll throw an error
            // We no longer want to use the final "/" character as this might be used by the user
            var safeFilename=inputFilename.Trim();
            return MakeValidFileName(safeFilename);
        }
        
        // From https://www.csharp-console-examples.com/general/c-replace-invalid-filename-characters/
        public static string MakeValidFileName(string name)
        {
            string invalidChars = System.Text.RegularExpressions.Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            invalidChars += ".";
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);
 
            return System.Text.RegularExpressions.Regex.Replace(name, invalidRegStr, "_");
        }

        private static readonly List<string> s_PreviouslyGeneratedPrefabs = new();

        private static void RecordPreviouslyGeneratedPrefabs()
        {
            s_PreviouslyGeneratedPrefabs.Clear();
            foreach (var folder in new[] { FigmaPagePrefabFolder, FigmaScreenPrefabFolder })
            {
                if (!Directory.Exists(folder)) continue;
                s_PreviouslyGeneratedPrefabs.AddRange(Directory.GetFiles(folder, "*.prefab").Select(path => path.Replace('\\', '/')));
            }
        }

        public static List<string> RemoveStaleGeneratedPrefabs(FigmaBridgeRegistry registry, Dictionary<GameObject, string> generatedPrefabPageIds, IEnumerable<string> importedPageIds, bool deleteStale)
        {
            var removed = new List<string>();
            var importedPages = new HashSet<string>(importedPageIds);
            var generatedPaths = new Dictionary<string, string>();
            foreach (var pair in generatedPrefabPageIds)
            {
                if (pair.Key == null) continue;
                generatedPaths[AssetDatabase.GetAssetPath(pair.Key)] = pair.Value;
            }
            foreach (var previousPath in s_PreviouslyGeneratedPrefabs)
            {
                if (!deleteStale || generatedPaths.ContainsKey(previousPath)) continue;
                if (!registry.prefabs.TryGetValue(previousPath, out var ownerPageId) || !importedPages.Contains(ownerPageId)) continue;
                AssetDatabase.DeleteAsset(previousPath);
                registry.prefabs.Remove(previousPath);
                registry.generatedHashes.Remove(previousPath);
                removed.Add(previousPath);
            }
            foreach (var pair in generatedPaths) registry.prefabs[pair.Key] = pair.Value;
            s_PreviouslyGeneratedPrefabs.Clear();
            return removed;
        }

        public static void CreateRequiredDirectories()
        {
            
            //  Create directory for pages if required 
            if (!Directory.Exists(FigmaPagePrefabFolder))
            {
                Directory.CreateDirectory(FigmaPagePrefabFolder);
            }

            
            //  Create directory for flowScreen prefabs if required 
            if (!Directory.Exists(FigmaScreenPrefabFolder))
            {
                Directory.CreateDirectory(FigmaScreenPrefabFolder);
            }
            RecordPreviouslyGeneratedPrefabs();
            
            if (!Directory.Exists(FigmaComponentPrefabFolder))
            {
                Directory.CreateDirectory(FigmaComponentPrefabFolder);
            }
            
            //  Create directory for image fills if required 
            if (!Directory.Exists(FigmaImageFillFolder))
            {
                Directory.CreateDirectory(FigmaImageFillFolder);
            }
            
            //  Create directory for server rendered images if required 
            if (!Directory.Exists(FigmaServerRenderedImagesFolder))
            {
                Directory.CreateDirectory(FigmaServerRenderedImagesFolder);
            }

            if (!Directory.Exists(FigmaFontMaterialPresetsFolder))
            {
                Directory.CreateDirectory(FigmaFontMaterialPresetsFolder);
            }
            
            if (!Directory.Exists(FigmaFontsFolder))
            {
                Directory.CreateDirectory(FigmaFontsFolder);
            }
        }
        
    }
}