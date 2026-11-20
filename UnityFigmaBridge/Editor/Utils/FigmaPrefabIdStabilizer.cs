using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityFigmaBridge.Editor.Reporting;
using UnityFigmaBridge.Runtime.UI;
using Object = UnityEngine.Object;

namespace UnityFigmaBridge.Editor.Utils
{
    public static class FigmaPrefabIdStabilizer
    {
        private static readonly Regex HeaderPattern = new(@"^(--- !u!\d+ &)(-?\d+)", RegexOptions.Multiline);
        private static readonly Regex DocumentSplitPattern = new(@"(?m)^(?=--- !u!)");
        private static readonly Regex LocalReferencePattern = new(@"\{fileID: (-?\d+)\}");
        private static readonly Regex ExternalReferencePattern = new(@"\{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: (\d+)\}");

        private class ObjectEntry
        {
            public long OldId;
            public string Key;
            public bool FromInstance;
            public long InstanceOldId;
            public string SourceGuid;
            public long SourceOldId;
        }

        private class PrefabFile
        {
            public string Path;
            public string Guid;
            public readonly List<ObjectEntry> Objects = new();
            public readonly Dictionary<long, string> InstanceKeys = new();
            public readonly Dictionary<long, long> Map = new();
            public bool Valid = true;
            public bool Computed;
        }

        public static List<string> Run(IEnumerable<string> prefabPaths)
        {
            var files = new Dictionary<string, PrefabFile>();
            foreach (var path in prefabPaths.Where(path => !string.IsNullOrEmpty(path) && File.Exists(path)).Distinct())
            {
                var file = Collect(path);
                if (file != null) files[file.Guid] = file;
            }
            foreach (var file in files.Values) Compute(file, files, new HashSet<string>());

            var originals = new Dictionary<string, string>();
            var changed = new List<PrefabFile>();
            foreach (var file in files.Values)
            {
                if (!file.Valid) continue;
                var text = File.ReadAllText(file.Path);
                var rewritten = Rewrite(text, file, files);
                if (rewritten == text) continue;
                originals[file.Path] = text;
                changed.Add(file);
            }
            if (changed.Count == 0) return new List<string>();

            var backupFolder = System.IO.Path.Combine("Library", "FigmaBridge", "IdBackup");
            Directory.CreateDirectory(backupFolder);
            foreach (var pair in originals) File.WriteAllText(System.IO.Path.Combine(backupFolder, pair.Key.Replace('/', '_')), pair.Value);

            foreach (var file in changed) File.WriteAllText(file.Path, Rewrite(originals[file.Path], file, files));
            ImportInDependencyOrder(changed, files);

            var problem = Validate(changed);
            if (problem == null) return changed.Select(file => file.Path).ToList();

            foreach (var pair in originals) File.WriteAllText(pair.Key, pair.Value);
            ImportInDependencyOrder(changed, files);
            FigmaImportReport.Warning($"Internal ids of the imported prefabs could not be made stable ({problem}); the files were left as Unity wrote them");
            return new List<string>();
        }

        private static PrefabFile Collect(string path)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) return null;
            var file = new PrefabFile { Path = path, Guid = AssetDatabase.AssetPathToGUID(path) };
            var gameObjectKeys = new Dictionary<GameObject, string>();
            var usedKeys = new HashSet<string>();
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var gameObject = transform.gameObject;
                var key = gameObject == root ? "root" : GameObjectKey(gameObject, root);
                if (!usedKeys.Add(key))
                {
                    key = $"{key}@{PathOf(gameObject.transform, root.transform)}";
                    for (var suffix = 2; !usedKeys.Add(key); suffix++) key = $"{key}#{suffix}";
                }
                gameObjectKeys[gameObject] = key;
            }

            foreach (var pair in gameObjectKeys)
            {
                AddObject(file, pair.Key, pair.Value, root, gameObjectKeys);
                var typeCounts = new Dictionary<Type, int>();
                foreach (var component in pair.Key.GetComponents<Component>())
                {
                    if (component == null) continue;
                    var type = component.GetType();
                    typeCounts.TryGetValue(type, out var index);
                    typeCounts[type] = index + 1;
                    AddObject(file, component, $"{pair.Value}|{type.FullName}#{index}", root, gameObjectKeys);
                }
            }
            return file;
        }

        private static void AddObject(PrefabFile file, Object target, string key, GameObject root, Dictionary<GameObject, string> gameObjectKeys)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out var guid, out long localId) || guid != file.Guid || localId == 0) return;
            var entry = new ObjectEntry { OldId = localId, Key = key };
            var source = PrefabUtility.GetCorrespondingObjectFromSource(target);
            var gameObject = target is Component component ? component.gameObject : target as GameObject;
            if (source != null && gameObject != null && gameObject != root && PrefabUtility.IsPartOfPrefabInstance(target))
            {
                var instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
                var handle = instanceRoot != null ? PrefabUtility.GetPrefabInstanceHandle(instanceRoot) : null;
                if (handle == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(handle, out var handleGuid, out long handleId) || handleGuid != file.Guid ||
                    !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out var sourceGuid, out long sourceId) || ((handleId ^ sourceId) & long.MaxValue) != localId)
                {
                    file.Valid = false;
                    return;
                }
                entry.FromInstance = true;
                entry.InstanceOldId = handleId;
                entry.SourceGuid = sourceGuid;
                entry.SourceOldId = sourceId;
                if (!file.InstanceKeys.ContainsKey(handleId))
                    file.InstanceKeys[handleId] = gameObjectKeys.TryGetValue(instanceRoot, out var instanceKey) ? instanceKey : PathOf(instanceRoot.transform, root.transform);
            }
            file.Objects.Add(entry);
        }

        private static string GameObjectKey(GameObject gameObject, GameObject root)
        {
            var nodeObject = gameObject.GetComponent<FigmaNodeObject>();
            if (nodeObject != null && !string.IsNullOrEmpty(nodeObject.NodeId)) return $"node:{nodeObject.NodeId}";
            return $"path:{PathOf(gameObject.transform, root.transform)}";
        }

        private static string PathOf(Transform transform, Transform root)
        {
            var parts = new List<string>();
            for (var current = transform; current != null && current != root; current = current.parent)
                parts.Add($"{current.name}[{current.GetSiblingIndex()}]");
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static long Hash(string text)
        {
            using var md5 = MD5.Create();
            var value = BitConverter.ToInt64(md5.ComputeHash(Encoding.UTF8.GetBytes(text)), 0) & long.MaxValue;
            return value == 0 ? 1 : value;
        }

        private static void Compute(PrefabFile file, Dictionary<string, PrefabFile> files, HashSet<string> visiting)
        {
            if (file.Computed || !file.Valid || !visiting.Add(file.Guid)) return;
            foreach (var sourceGuid in file.Objects.Where(entry => entry.FromInstance).Select(entry => entry.SourceGuid).Distinct())
            {
                if (files.TryGetValue(sourceGuid, out var source)) Compute(source, files, visiting);
            }
            for (var salt = 0; salt < 8; salt++)
            {
                file.Map.Clear();
                var instanceIds = file.InstanceKeys.ToDictionary(pair => pair.Key, pair => Hash($"{file.Guid}|instance|{pair.Value}|{salt}"));
                foreach (var pair in instanceIds) file.Map[pair.Key] = pair.Value;
                foreach (var entry in file.Objects)
                {
                    long newId;
                    if (entry.FromInstance)
                    {
                        var sourceId = entry.SourceOldId;
                        if (files.TryGetValue(entry.SourceGuid, out var source) && source.Valid && source.Map.TryGetValue(sourceId, out var mappedSource)) sourceId = mappedSource;
                        newId = (instanceIds[entry.InstanceOldId] ^ sourceId) & long.MaxValue;
                    }
                    else
                    {
                        newId = Hash($"{file.Guid}|{entry.Key}|{salt}");
                    }
                    file.Map[entry.OldId] = newId;
                }
                if (file.Map.Values.Distinct().Count() == file.Map.Count && !file.Map.Values.Contains(0)) break;
            }
            if (file.Map.Values.Distinct().Count() != file.Map.Count) file.Valid = false;
            file.Computed = true;
            visiting.Remove(file.Guid);
        }

        private static string Rewrite(string text, PrefabFile file, Dictionary<string, PrefabFile> files)
        {
            text = HeaderPattern.Replace(text, match => long.TryParse(match.Groups[2].Value, out var id) && file.Map.TryGetValue(id, out var mapped) ? match.Groups[1].Value + mapped : match.Value);
            text = LocalReferencePattern.Replace(text, match => long.TryParse(match.Groups[1].Value, out var id) && file.Map.TryGetValue(id, out var mapped) ? $"{{fileID: {mapped}}}" : match.Value);
            text = ExternalReferencePattern.Replace(text, match =>
            {
                if (!long.TryParse(match.Groups[1].Value, out var id) || !files.TryGetValue(match.Groups[2].Value, out var target) || !target.Valid) return match.Value;
                return target.Map.TryGetValue(id, out var mapped) ? $"{{fileID: {mapped}, guid: {match.Groups[2].Value}, type: {match.Groups[3].Value}}}" : match.Value;
            });
            return SortDocuments(text);
        }

        private static string SortDocuments(string text)
        {
            var parts = DocumentSplitPattern.Split(text);
            if (parts.Length < 3) return text;
            var documents = parts.Skip(1).Select(part => (id: DocumentId(part), body: part.EndsWith("\n") ? part : part + "\n")).ToList();
            if (documents.Any(document => document.id == null)) return text;
            return parts[0] + string.Concat(documents.OrderBy(document => document.id.Value).Select(document => document.body));
        }

        private static long? DocumentId(string document)
        {
            var match = HeaderPattern.Match(document);
            return match.Success && long.TryParse(match.Groups[2].Value, out var id) ? id : null;
        }

        private static void ImportInDependencyOrder(List<PrefabFile> changed, Dictionary<string, PrefabFile> files)
        {
            var ordered = new List<PrefabFile>();
            var done = new HashSet<string>();
            void Visit(PrefabFile file)
            {
                if (!done.Add(file.Guid)) return;
                foreach (var sourceGuid in file.Objects.Where(entry => entry.FromInstance).Select(entry => entry.SourceGuid).Distinct())
                {
                    if (files.TryGetValue(sourceGuid, out var source) && changed.Contains(source)) Visit(source);
                }
                if (changed.Contains(file)) ordered.Add(file);
            }
            foreach (var file in changed) Visit(file);
            foreach (var file in ordered) AssetDatabase.ImportAsset(file.Path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }

        private static string Validate(List<PrefabFile> changed)
        {
            foreach (var file in changed)
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(file.Path);
                if (root == null) return $"{System.IO.Path.GetFileName(file.Path)} did not load";
                var expected = new HashSet<long>(file.Objects.Select(entry => file.Map[entry.OldId]));
                var found = new HashSet<long>();
                var names = new Dictionary<long, string>();
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    foreach (var target in new Object[] { transform.gameObject }.Concat(transform.GetComponents<Component>()))
                    {
                        if (target == null) continue;
                        if (PrefabUtility.IsPartOfPrefabInstance(target) && PrefabUtility.IsPrefabAssetMissing(target)) return $"{System.IO.Path.GetFileName(file.Path)} lost a nested prefab";
                        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out var guid, out long id) && guid == file.Guid)
                        {
                            found.Add(id);
                            names[id] = $"{transform.name}:{target.GetType().Name}";
                        }
                    }
                }
                if (!expected.SetEquals(found))
                {
                    var unexpected = found.Except(expected).Take(4).Select(id => names.TryGetValue(id, out var name) ? name : id.ToString());
                    var keys = file.Objects.Where(entry => !found.Contains(file.Map[entry.OldId])).Take(4).Select(entry => entry.Key + (entry.FromInstance ? " (instance)" : ""));
                    return $"{System.IO.Path.GetFileName(file.Path)} has {found.Except(expected).Count()} unexpected ids [{string.Join(", ", unexpected)}], missing [{string.Join(", ", keys)}]";
                }
            }
            return null;
        }
    }
}
