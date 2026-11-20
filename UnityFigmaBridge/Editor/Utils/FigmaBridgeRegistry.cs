using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using UnityEngine;
using UnityFigmaBridge.Editor.Reporting;

namespace UnityFigmaBridge.Editor.Utils
{
    public class FigmaBridgeRegistry
    {
        public Dictionary<string, string> prefabs = new();
        public Dictionary<string, string> screenHashes = new();
        public Dictionary<string, string> generatedHashes = new();
        public List<string> carriedPrefabs = new();
        public Dictionary<string, string> reusedImages = new();

        private static string RegistryPath => $"{FigmaPaths.FigmaAssetsRootFolder}/.figmabridge-registry.json";
        private static string LegacyRegistryPath => Path.Combine("Library", "FigmaBridge", "prefab-pages.json");
        private static string BackupRoot => Path.Combine("Library", "FigmaBridge", "Backups");

        public static FigmaBridgeRegistry Load()
        {
            try
            {
                if (File.Exists(RegistryPath))
                    return JsonConvert.DeserializeObject<FigmaBridgeRegistry>(File.ReadAllText(RegistryPath)) ?? new FigmaBridgeRegistry();
                if (File.Exists(LegacyRegistryPath))
                {
                    var legacyPrefabs = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(LegacyRegistryPath));
                    return new FigmaBridgeRegistry { prefabs = legacyPrefabs ?? new Dictionary<string, string>() };
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Figma Bridge registry could not be read, starting fresh: {exception.Message}");
            }
            return new FigmaBridgeRegistry();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath));
            var sorted = new FigmaBridgeRegistry
            {
                prefabs = prefabs.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
                screenHashes = screenHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
                generatedHashes = generatedHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
                carriedPrefabs = carriedPrefabs.Distinct().OrderBy(path => path, StringComparer.Ordinal).ToList(),
                reusedImages = reusedImages.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
            };
            File.WriteAllText(RegistryPath, JsonConvert.SerializeObject(sorted, Formatting.Indented));
        }

        public static string HashFile(string path)
        {
            if (!File.Exists(path)) return null;
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        public static string HashText(string text)
        {
            using var md5 = MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty))).Replace("-", string.Empty).ToLowerInvariant();
        }

        public static string ScreenKey(string pageId, string screenName) => $"{pageId}/{screenName}";

        public void BackUpHandEditedPrefabs()
        {
            var backupFolder = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            foreach (var pair in generatedHashes.ToList())
            {
                var currentHash = HashFile(pair.Key);
                if (currentHash == null || currentHash == pair.Value) continue;
                var backupPath = Path.Combine(backupFolder, pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                File.Copy(pair.Key, backupPath, true);
                FigmaImportReport.Warning($"{Path.GetFileNameWithoutExtension(pair.Key)} was edited in Unity since the last import. Added scripts, components and objects are carried over; a backup of the edited file is in {backupPath}", null, pair.Key);
            }
        }

        public void RecordGeneratedHashes(IEnumerable<string> assetPaths)
        {
            foreach (var assetPath in assetPaths.Where(path => !string.IsNullOrEmpty(path)).Distinct())
            {
                var hash = HashFile(assetPath);
                if (hash != null) generatedHashes[assetPath] = hash;
            }
            foreach (var missing in generatedHashes.Keys.Where(path => !File.Exists(path)).ToList())
                generatedHashes.Remove(missing);
        }
    }
}
