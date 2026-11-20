using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.U2D;
using UnityFigmaBridge.Editor.FigmaApi;
using UnityFigmaBridge.Editor.Reporting;
using UnityFigmaBridge.Editor.Settings;

namespace UnityFigmaBridge.Editor.Utils
{
    public static class FigmaAssetReuse
    {
        private const int ChannelTolerance = 2;

        private static readonly (int weight, string[] names)[] WeightNames =
        {
            (100, new[] { "Thin", "Hairline" }),
            (200, new[] { "ExtraLight", "UltraLight" }),
            (300, new[] { "Light" }),
            (400, new[] { "Regular", "Normal", "Book", "Roman", "" }),
            (500, new[] { "Medium" }),
            (600, new[] { "SemiBold", "DemiBold" }),
            (700, new[] { "Bold" }),
            (800, new[] { "ExtraBold", "UltraBold" }),
            (900, new[] { "Black", "Heavy" }),
        };

        private static readonly HashSet<string> NewImageFillHashes = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> s_ActiveImageMap;

        private static string CacheFolder => Path.Combine("Library", "FigmaBridge", "ReusedImages");
        private static string IndexPath => Path.Combine("Library", "FigmaBridge", "project-images.json");

        private class ImageIndexEntry
        {
            public long stamp;
            public int width;
            public int height;
        }

        private static UnityFigmaBridgeSettings Settings => UnityFigmaBridgeSettingsProvider.FindUnityBridgeSettingsAsset();

        public static bool Enabled
        {
            get
            {
                var settings = Settings;
                return settings == null || settings.ReuseProjectAssets;
            }
        }

        public static bool IsGeneratedAsset(string assetPath) =>
            !string.IsNullOrEmpty(assetPath) && (assetPath == FigmaPaths.FigmaAssetsRootFolder || assetPath.StartsWith(FigmaPaths.FigmaAssetsRootFolder + "/"));

        public static string NormaliseName(string name) =>
            new string((name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        public static int WeightFromStyle(string styleName)
        {
            var normalised = NormaliseName(styleName).Replace("italic", string.Empty).Replace("oblique", string.Empty);
            foreach (var (weight, names) in WeightNames)
            {
                if (names.Any(name => NormaliseName(name) == normalised)) return weight;
            }
            return -1;
        }

        public static string StyleName(int weight)
        {
            foreach (var (value, names) in WeightNames)
            {
                if (value == weight) return names[0];
            }
            return weight.ToString();
        }

        private static bool IsItalic(string styleName)
        {
            var normalised = NormaliseName(styleName);
            return normalised.Contains("italic") || normalised.Contains("oblique");
        }

        public static TMP_FontAsset FindFontOverride(string family, int weight)
        {
            var settings = Settings;
            if (settings == null || settings.FontOverrides == null) return null;
            var overrides = settings.FontOverrides;
            var familyKey = NormaliseName(family);
            return overrides
                .Where(entry => entry != null && entry.Font != null && NormaliseName(entry.FigmaFamily) == familyKey && (entry.FigmaWeight == 0 || entry.FigmaWeight == weight))
                .OrderByDescending(entry => entry.FigmaWeight == weight)
                .Select(entry => entry.Font)
                .FirstOrDefault();
        }

        public static TMP_FontAsset FindProjectFont(string family, int weight, string characters, out string problem)
        {
            problem = null;
            var familyKey = NormaliseName(family);
            if (string.IsNullOrEmpty(familyKey)) return null;
            var matches = AssetDatabase.FindAssets("t:TMP_FontAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !IsGeneratedAsset(path))
                .Select(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>)
                .Where(font => font != null && NormaliseName(font.faceInfo.familyName) == familyKey && !IsItalic(font.faceInfo.styleName) && WeightFromStyle(font.faceInfo.styleName) == weight)
                .ToList();
            if (matches.Count == 0) return null;
            if (matches.Count > 1)
            {
                problem = $"{family} {StyleName(weight)} matches {matches.Count} project fonts ({string.Join(", ", matches.Select(AssetDatabase.GetAssetPath))}). Pick one under Font Overrides in the Figma Bridge settings; until then a separate copy is imported";
                return null;
            }
            var missing = MissingCharacters(matches[0], characters);
            if (missing.Length > 0)
            {
                problem = $"Project font {AssetDatabase.GetAssetPath(matches[0])} has no glyphs for \"{missing}\" used in Figma, so a separate copy of {family} {StyleName(weight)} is imported";
                return null;
            }
            return matches[0];
        }

        public static string MissingCharacters(TMP_FontAsset font, string characters)
        {
            if (font == null || string.IsNullOrEmpty(characters)) return string.Empty;
            var missing = new List<string>();
            var seen = new HashSet<uint>();
            for (var i = 0; i < characters.Length; i++)
            {
                uint unicode;
                if (char.IsHighSurrogate(characters[i]) && i + 1 < characters.Length && char.IsLowSurrogate(characters[i + 1]))
                {
                    unicode = (uint)char.ConvertToUtf32(characters[i], characters[i + 1]);
                    i++;
                }
                else
                {
                    unicode = characters[i];
                }
                if (unicode < 0x21 || unicode == 0xA0 || char.IsControl((char)Math.Min(unicode, 0xFFFF)) || !seen.Add(unicode)) continue;
                if (!HasGlyph(font, unicode, 0)) missing.Add(char.ConvertFromUtf32((int)unicode));
            }
            return string.Join(string.Empty, missing.Take(20));
        }

        private static bool HasGlyph(TMP_FontAsset font, uint unicode, int depth)
        {
            if (font == null || depth > 4) return false;
            if (font.characterLookupTable != null && font.characterLookupTable.ContainsKey(unicode)) return true;
            if (font.atlasPopulationMode != AtlasPopulationMode.Static && font.sourceFontFile != null &&
                FontEngine.LoadFontFace(font.sourceFontFile, (int)font.faceInfo.pointSize) == FontEngineError.Success &&
                FontEngine.TryGetGlyphIndex(unicode, out var glyphIndex) && glyphIndex != 0)
                return true;
            return font.fallbackFontAssetTable != null && font.fallbackFontAssetTable.Any(fallback => HasGlyph(fallback, unicode, depth + 1));
        }

        public static void RecordNewImageFill(string hash)
        {
            if (!string.IsNullOrEmpty(hash)) NewImageFillHashes.Add(hash);
        }

        public static bool IsReusedImageAvailable(FigmaBridgeRegistry registry, string hash)
        {
            if (registry == null || !registry.reusedImages.TryGetValue(hash, out var guid)) return false;
            return LoadProjectSprite(guid) != null;
        }

        private static Sprite LoadProjectSprite(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        public static void BeginImport(FigmaBridgeRegistry registry, FigmaFile figmaFile)
        {
            s_ActiveImageMap = registry.reusedImages;
            var newHashes = NewImageFillHashes.ToList();
            NewImageFillHashes.Clear();
            if (!Enabled || newHashes.Count == 0) return;
            var tiled = TiledImageHashes(figmaFile);
            var candidates = new ProjectImageCandidates();
            foreach (var hash in newHashes)
            {
                var importedPath = FigmaPaths.GetPathForImageFill(hash);
                if (!File.Exists(importedPath)) continue;
                var match = candidates.FindMatch(importedPath, tiled.Contains(hash));
                if (match == null) continue;
                registry.reusedImages[hash] = AssetDatabase.AssetPathToGUID(match);
                MoveToCache(hash, importedPath);
                FigmaImportReport.Info($"Using {match} from the project instead of importing a copy of the same image", null, match);
            }
        }

        public static void EndImport() => s_ActiveImageMap = null;

        public static Sprite LoadImageFillSprite(string hash)
        {
            var importedPath = FigmaPaths.GetPathForImageFill(hash);
            var map = s_ActiveImageMap;
            if (map != null && !string.IsNullOrEmpty(hash) && map.TryGetValue(hash, out var guid))
            {
                var projectSprite = LoadProjectSprite(guid);
                if (projectSprite != null) return projectSprite;
                map.Remove(hash);
                if (RestoreFromCache(hash, importedPath))
                    FigmaImportReport.Warning("A project image used in place of a Figma image is gone, so the Figma copy is used again", null, importedPath);
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(importedPath);
        }

        private static void MoveToCache(string hash, string importedPath)
        {
            Directory.CreateDirectory(CacheFolder);
            File.Copy(importedPath, Path.Combine(CacheFolder, $"{hash}.png"), true);
            AssetDatabase.DeleteAsset(importedPath);
        }

        private static bool RestoreFromCache(string hash, string importedPath)
        {
            var cached = Path.Combine(CacheFolder, $"{hash}.png");
            if (!File.Exists(cached)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(importedPath));
            File.Copy(cached, importedPath, true);
            AssetDatabase.ImportAsset(importedPath);
            FigmaApiUtils.ConfigureImageImporter(importedPath, true);
            return true;
        }

        private static HashSet<string> TiledImageHashes(FigmaFile figmaFile)
        {
            var tiled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(Node node, int depth)
            {
                if (node == null || depth > 200) return;
                if (node.fills != null)
                {
                    foreach (var fill in node.fills)
                    {
                        if (fill != null && fill.type == Paint.PaintType.IMAGE && fill.scaleMode == Paint.ScaleMode.TILE && !string.IsNullOrEmpty(fill.imageRef))
                            tiled.Add(fill.imageRef);
                    }
                }
                if (node.children == null) return;
                foreach (var child in node.children) Visit(child, depth + 1);
            }
            Visit(figmaFile?.document, 0);
            return tiled;
        }

        public class ProjectImageCandidates
        {
            private static readonly Regex GuidPattern = new(@"guid: ([0-9a-f]{32})");
            private readonly Dictionary<string, ImageIndexEntry> m_Index;
            private List<SpriteAtlas> m_Atlases;
            private HashSet<string> m_PackedPaths;

            public ProjectImageCandidates()
            {
                m_Index = BuildIndex();
            }

            public string FindMatch(string importedPath, bool tiled)
            {
                if (!TryReadPngSize(importedPath, out var importedWidth, out var importedHeight)) return null;
                var candidates = m_Index
                    .Where(pair => PaddedSize(pair.Value.width, pair.Value.height) == (importedWidth, importedHeight))
                    .Select(pair => pair.Key)
                    .Where(path => IsUsableSprite(path, tiled))
                    .ToList();
                if (candidates.Count == 0) return null;
                var imported = Decode(importedPath);
                if (imported == null) return null;
                try
                {
                    foreach (var candidatePath in candidates.OrderBy(path => path, StringComparer.Ordinal))
                    {
                        var candidate = Decode(candidatePath);
                        if (candidate == null) continue;
                        try
                        {
                            if (PixelsMatch(imported, candidate)) return candidatePath;
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(candidate);
                        }
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(imported);
                }
                return null;
            }

            private bool IsUsableSprite(string path, bool tiled)
            {
                if (AssetImporter.GetAtPath(path) is not TextureImporter importer) return false;
                if (importer.textureType != TextureImporterType.Sprite || importer.spriteImportMode != SpriteImportMode.Single) return false;
                if (tiled && importer.wrapMode != TextureWrapMode.Repeat) return false;
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null) return false;
                if (m_Atlases == null) LoadAtlases();
                if (m_Atlases.Any(atlas => atlas.CanBindTo(sprite))) return false;
                return !m_PackedPaths.Any(packed => path == packed || path.StartsWith(packed + "/"));
            }

            private void LoadAtlases()
            {
                m_Atlases = new List<SpriteAtlas>();
                m_PackedPaths = new HashSet<string>();
                foreach (var atlasPath in AssetDatabase.FindAssets("t:SpriteAtlas").Select(AssetDatabase.GUIDToAssetPath).Distinct())
                {
                    var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
                    if (atlas != null) m_Atlases.Add(atlas);
                    try
                    {
                        foreach (Match match in GuidPattern.Matches(File.ReadAllText(atlasPath)))
                        {
                            var packedPath = AssetDatabase.GUIDToAssetPath(match.Groups[1].Value);
                            if (!string.IsNullOrEmpty(packedPath) && packedPath != atlasPath) m_PackedPaths.Add(packedPath);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static (int, int) PaddedSize(int width, int height)
        {
            if (Mathf.Max(width, height) < 256 || (width % 4 == 0 && height % 4 == 0)) return (width, height);
            return ((width + 3) / 4 * 4, (height + 3) / 4 * 4);
        }

        private static Texture2D Decode(string path)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (texture.LoadImage(File.ReadAllBytes(path), false)) return texture;
            }
            catch (Exception)
            {
            }
            UnityEngine.Object.DestroyImmediate(texture);
            return null;
        }

        private static bool PixelsMatch(Texture2D imported, Texture2D candidate)
        {
            var width = candidate.width;
            var height = candidate.height;
            if (imported.width < width || imported.height < height) return false;
            var importedPixels = imported.GetPixels32();
            var candidatePixels = candidate.GetPixels32();
            var importedWidth = imported.width;
            var rowOffset = imported.height - height;
            bool Same(int x, int y)
            {
                var a = candidatePixels[y * width + x];
                var b = importedPixels[(y + rowOffset) * importedWidth + x];
                if (a.a == 0 && b.a == 0) return true;
                return Math.Abs(a.r - b.r) <= ChannelTolerance && Math.Abs(a.g - b.g) <= ChannelTolerance &&
                       Math.Abs(a.b - b.b) <= ChannelTolerance && Math.Abs(a.a - b.a) <= ChannelTolerance;
            }
            for (var sampleY = 0; sampleY < 16; sampleY++)
            {
                for (var sampleX = 0; sampleX < 16; sampleX++)
                {
                    if (!Same(sampleX * (width - 1) / 15, sampleY * (height - 1) / 15)) return false;
                }
            }
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!Same(x, y)) return false;
                }
            }
            return true;
        }

        private static bool TryReadPngSize(string path, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using var stream = File.OpenRead(path);
                var header = new byte[24];
                if (stream.Read(header, 0, 24) < 24 || header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47) return false;
                width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                return width > 0 && height > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Dictionary<string, ImageIndexEntry> BuildIndex()
        {
            Dictionary<string, ImageIndexEntry> cached = null;
            try
            {
                if (File.Exists(IndexPath)) cached = JsonConvert.DeserializeObject<Dictionary<string, ImageIndexEntry>>(File.ReadAllText(IndexPath));
            }
            catch (Exception)
            {
            }
            cached ??= new Dictionary<string, ImageIndexEntry>();
            var index = new Dictionary<string, ImageIndexEntry>();
            var paths = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !IsGeneratedAsset(path))
                .Where(path =>
                {
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    return extension == ".png" || extension == ".jpg" || extension == ".jpeg";
                })
                .Distinct();
            foreach (var path in paths)
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                var stamp = info.LastWriteTimeUtc.Ticks ^ info.Length;
                if (cached.TryGetValue(path, out var entry) && entry.stamp == stamp)
                {
                    index[path] = entry;
                    continue;
                }
                int width, height;
                if (!TryReadPngSize(path, out width, out height))
                {
                    if (AssetImporter.GetAtPath(path) is not TextureImporter importer) continue;
                    importer.GetSourceTextureWidthAndHeight(out width, out height);
                }
                if (width <= 0 || height <= 0) continue;
                index[path] = new ImageIndexEntry { stamp = stamp, width = width, height = height };
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IndexPath));
                File.WriteAllText(IndexPath, JsonConvert.SerializeObject(index));
            }
            catch (Exception)
            {
            }
            return index;
        }

        public static void MoveReusedImageToCache(string hash, string importedPath) => MoveToCache(hash, importedPath);
    }
}
