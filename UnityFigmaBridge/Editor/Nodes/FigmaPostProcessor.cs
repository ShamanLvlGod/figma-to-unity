using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityFigmaBridge.Editor.FigmaApi;
using UnityFigmaBridge.Editor.Reporting;
using UnityFigmaBridge.Editor.Utils;
using UnityFigmaBridge.Runtime.UI;
using Object = UnityEngine.Object;

namespace UnityFigmaBridge.Editor.Nodes
{
    public static class FigmaBridgeImportEvents
    {
        public static event Action<string> ImportStarted;
        public static event Action<GameObject, string, bool> PrefabBuilt;
        public static event Action<FigmaImportReport> ImportCompleted;

        internal static void RaiseImportStarted(string source) => Invoke(() => ImportStarted?.Invoke(source));
        internal static void RaisePrefabBuilt(GameObject root, string assetPath, bool isScreen) => Invoke(() => PrefabBuilt?.Invoke(root, assetPath, isScreen));
        internal static void RaiseImportCompleted(FigmaImportReport report) => Invoke(() => ImportCompleted?.Invoke(report));

        private static void Invoke(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                FigmaImportReport.Warning($"An import hook failed: {exception.Message}");
            }
        }
    }

    public static class FigmaPostProcessor
    {
        private static readonly Regex DefaultNamePattern = new(@"^(Frame|Rectangle|Group|Ellipse|Vector|Text|Image|Component|Instance|Line|Polygon|Star|Union|Subtract|Intersect|Exclude|Section|Slice)\s*\d*$", RegexOptions.IgnoreCase);
        private const string LocalizationFolder = "Localization";

        private class NineSliceRequest
        {
            public string SpritePath;
            public FigmaInsets Insets;
            public float NodeWidth;
        }

        public static void Run(FigmaImportProcessData data)
        {
            var nineSliceRequests = new List<NineSliceRequest>();
            var localizationRows = new Dictionary<string, (string text, string screen)>();
            var screenPaths = new HashSet<string>(data.ScreenPrefabs.Where(prefab => prefab != null).Select(AssetDatabase.GetAssetPath));
            var componentPaths = data.ComponentData.AllComponentPrefabs.Where(prefab => prefab != null).Select(AssetDatabase.GetAssetPath);

            foreach (var assetPath in componentPaths.Concat(screenPaths).Distinct())
            {
                if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath)) continue;
                var isScreen = screenPaths.Contains(assetPath);
                var contents = PrefabUtility.LoadPrefabContents(assetPath);
                try
                {
                    ProcessPrefab(contents, isScreen, data, nineSliceRequests, localizationRows);
                    FigmaBridgeImportEvents.RaisePrefabBuilt(contents, assetPath, isScreen);
                    PrefabUtility.SaveAsPrefabAsset(contents, assetPath);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    FigmaImportReport.Warning($"Post-processing failed for {Path.GetFileNameWithoutExtension(assetPath)}: {exception.Message}", null, assetPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }

            ApplyNineSlices(nineSliceRequests);
            WriteLocalization(localizationRows);
            WriteFlowMap(data);
            WriteDesignTokens(data.SourceFile);
        }

        private static Node NodeFor(GameObject gameObject, FigmaImportProcessData data)
        {
            var nodeObject = gameObject.GetComponent<FigmaNodeObject>();
            if (nodeObject == null || string.IsNullOrEmpty(nodeObject.NodeId)) return null;
            if (data.NodeLookupDictionary.TryGetValue(nodeObject.NodeId, out var node)) return node;
            var lastSegment = nodeObject.NodeId.Split(';').Last();
            return data.NodeLookupDictionary.TryGetValue(lastSegment, out node) ? node : null;
        }

        private static bool IsInsideNestedInstance(GameObject gameObject, GameObject root)
        {
            if (gameObject == root) return false;
            var nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            return nearestRoot != null && nearestRoot != gameObject;
        }

        private static void ProcessPrefab(GameObject root, bool isScreen, FigmaImportProcessData data,
            List<NineSliceRequest> nineSliceRequests, Dictionary<string, (string, string)> localizationRows)
        {
            var refEntries = new List<(string key, string path, GameObject target)>();
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var gameObject = transform.gameObject;
                var node = NodeFor(gameObject, data);
                if (node == null) continue;
                var nested = IsInsideNestedInstance(gameObject, root);

                if (!nested)
                {
                    try
                    {
                        ApplyRole(gameObject, node);
                        ApplyLocalization(gameObject, node, root.name, localizationRows);
                        CollectNineSlice(gameObject, node, nineSliceRequests);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                        FigmaImportReport.Warning($"Widget setup failed ({exception.Message})", node);
                    }
                }

                if (isScreen && gameObject != root && !HasInstanceAncestor(transform, root.transform) && IsReferenceWorthy(gameObject, node))
                    refEntries.Add((FigmaBridgeTags.GetBindingName(node), PathFromRoot(transform, root.transform), gameObject));
            }

            if (!isScreen) return;
            ApplyScreenFitter(root, data);
            var refs = UnityUiUtils.GetOrAddComponent<FigmaScreenRefs>(root);
            refs.Entries.Clear();
            var duplicateKeys = new HashSet<string>(refEntries.GroupBy(entry => entry.key, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var (key, path, target) in refEntries)
                refs.Entries.Add(new FigmaScreenRefs.Entry { Key = duplicateKeys.Contains(key) ? path : key, Target = target });
        }

        private static bool HasInstanceAncestor(Transform transform, Transform root)
        {
            for (var current = transform.parent; current != null && current != root; current = current.parent)
            {
                if (PrefabUtility.IsAnyPrefabInstanceRoot(current.gameObject)) return true;
            }
            return false;
        }

        private static bool IsReferenceWorthy(GameObject gameObject, Node node)
        {
            if (!string.IsNullOrWhiteSpace(node.figmaBridge?.bindingName)) return true;
            if (gameObject.TryGetComponent<Selectable>(out _) || gameObject.TryGetComponent<IFigmaStateful>(out _)) return true;
            if (gameObject.GetComponent<TMP_Text>() != null) return true;
            return !DefaultNamePattern.IsMatch(FigmaBridgeTags.CleanName(node.name));
        }

        private static string PathFromRoot(Transform transform, Transform root)
        {
            var parts = new List<string>();
            for (var current = transform; current != null && current != root; current = current.parent) parts.Add(current.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void ApplyRole(GameObject gameObject, Node node)
        {
            switch (FigmaBridgeTags.GetRole(node))
            {
                case FigmaBridgeTags.RoleButton:
                    if (gameObject.GetComponent<Selectable>() == null)
                    {
                        var button = gameObject.AddComponent<Button>();
                        button.targetGraphic = gameObject.GetComponentInChildren<Graphic>(true);
                    }
                    break;
                case FigmaBridgeTags.RoleToggle:
                    if (gameObject.GetComponent<Selectable>() == null) SetupPlainToggle(gameObject);
                    break;
                case FigmaBridgeTags.RoleToggleGroup:
                    SetupToggleGroup(gameObject);
                    break;
                case FigmaBridgeTags.RoleSlider:
                    SetupSlider(gameObject, node);
                    break;
                case FigmaBridgeTags.RoleInputField:
                    SetupInputField(gameObject, node);
                    break;
            }
        }

        private static Transform FindChild(Transform parent, Func<string, bool> predicate, int depth = 3)
        {
            foreach (Transform child in parent)
            {
                if (predicate(child.name.ToLowerInvariant())) return child;
            }
            if (depth <= 0) return null;
            foreach (Transform child in parent)
            {
                var found = FindChild(child, predicate, depth - 1);
                if (found != null) return found;
            }
            return null;
        }

        private static void SetupPlainToggle(GameObject gameObject)
        {
            var toggle = gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = gameObject.GetComponentInChildren<Graphic>(true);
            var checkmark = FindChild(gameObject.transform, name => name.Contains("check") || name == "on" || name.Contains("tick"));
            if (checkmark != null)
            {
                toggle.graphic = checkmark.GetComponentInChildren<Graphic>(true);
                toggle.isOn = checkmark.gameObject.activeSelf;
            }
        }

        private static void SetupToggleGroup(GameObject gameObject)
        {
            var group = UnityUiUtils.GetOrAddComponent<ToggleGroup>(gameObject);
            group.allowSwitchOff = false;
            foreach (var toggle in gameObject.GetComponentsInChildren<Toggle>(true))
                toggle.group = group;
        }

        private static void SetupSlider(GameObject gameObject, Node node)
        {
            if (gameObject.TryGetComponent<Selectable>(out var existing) && existing is not Slider)
            {
                FigmaImportReport.Warning("Slider role ignored because the layer is already a button or toggle", node);
                return;
            }
            var slider = UnityUiUtils.GetOrAddComponent<Slider>(gameObject);
            var rect = (RectTransform)gameObject.transform;
            var fill = FindChild(gameObject.transform, name => name.Contains("fill") || name.Contains("progress")) as RectTransform;
            var handle = FindChild(gameObject.transform, name => name.Contains("handle") || name.Contains("knob") || name.Contains("thumb")) as RectTransform;
            slider.direction = rect.rect.width >= rect.rect.height ? Slider.Direction.LeftToRight : Slider.Direction.BottomToTop;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            var startValue = 0.5f;
            if (fill != null && rect.rect.width > 0 && rect.rect.height > 0)
                startValue = Mathf.Clamp01(slider.direction == Slider.Direction.LeftToRight ? fill.rect.width / rect.rect.width : fill.rect.height / rect.rect.height);
            var horizontal = slider.direction == Slider.Direction.LeftToRight;
            slider.fillRect = fill != null ? WrapInSlideArea(fill, rect, "Fill Area", horizontal, false) : null;
            slider.handleRect = handle != null ? WrapInSlideArea(handle, rect, "Handle Slide Area", horizontal, true) : null;
            slider.targetGraphic = handle != null ? handle.GetComponentInChildren<Graphic>(true) : gameObject.GetComponentInChildren<Graphic>(true);
            slider.SetValueWithoutNotify(startValue);
            if (fill == null && handle == null)
                FigmaImportReport.Warning("Slider has no child named Fill or Handle, so it won't show its value", node);
        }

        private static RectTransform WrapInSlideArea(RectTransform target, RectTransform sliderRect, string areaName, bool horizontal, bool isHandle)
        {
            if (target.parent != null && target.parent.name == areaName) return target;
            var corners = new Vector3[4];
            target.GetWorldCorners(corners);
            var localMin = sliderRect.InverseTransformPoint(corners[0]);
            var localMax = sliderRect.InverseTransformPoint(corners[2]);
            var sliderBounds = sliderRect.rect;
            var area = new GameObject(areaName, typeof(RectTransform));
            var sliderNodeObject = sliderRect.GetComponent<FigmaNodeObject>();
            area.AddComponent<FigmaNodeObject>().NodeId = $"{(sliderNodeObject != null ? sliderNodeObject.NodeId : sliderRect.name)}#{areaName}";
            var areaRect = (RectTransform)area.transform;
            areaRect.SetParent(target.parent, false);
            areaRect.SetSiblingIndex(target.GetSiblingIndex());
            areaRect.anchorMin = Vector2.zero;
            areaRect.anchorMax = Vector2.one;
            areaRect.pivot = new Vector2(0.5f, 0.5f);
            var targetSize = new Vector2(localMax.x - localMin.x, localMax.y - localMin.y);
            if (horizontal)
            {
                areaRect.offsetMin = new Vector2(0f, localMin.y - sliderBounds.yMin);
                areaRect.offsetMax = new Vector2(0f, localMax.y - sliderBounds.yMax);
            }
            else
            {
                areaRect.offsetMin = new Vector2(localMin.x - sliderBounds.xMin, 0f);
                areaRect.offsetMax = new Vector2(localMax.x - sliderBounds.xMax, 0f);
            }
            target.SetParent(areaRect, false);
            target.localRotation = Quaternion.identity;
            target.localScale = Vector3.one;
            target.pivot = new Vector2(0.5f, 0.5f);
            target.anchorMin = Vector2.zero;
            target.anchorMax = Vector2.one;
            target.anchoredPosition = Vector2.zero;
            target.sizeDelta = isHandle ? (horizontal ? new Vector2(targetSize.x, 0f) : new Vector2(0f, targetSize.y)) : Vector2.zero;
            return target;
        }

        private static void SetupInputField(GameObject gameObject, Node node)
        {
            if (gameObject.TryGetComponent<Selectable>(out var existing) && existing is not TMP_InputField)
            {
                FigmaImportReport.Warning("Input field role ignored because the layer is already a button or toggle", node);
                return;
            }
            var texts = gameObject.GetComponentsInChildren<TextMeshProUGUI>(true);
            var placeholder = texts.FirstOrDefault(text => text.name.ToLowerInvariant().Contains("placeholder") || text.name.ToLowerInvariant().Contains("hint"));
            var mainText = texts.FirstOrDefault(text => text != placeholder);
            if (mainText == null && placeholder != null)
            {
                mainText = Object.Instantiate(placeholder, placeholder.transform.parent);
                mainText.name = "Text";
                mainText.text = string.Empty;
            }
            if (mainText == null)
            {
                FigmaImportReport.Warning("Input field has no text layer inside it", node);
                return;
            }
            UnityUiUtils.GetOrAddComponent<RectMask2D>(gameObject);
            var input = UnityUiUtils.GetOrAddComponent<TMP_InputField>(gameObject);
            input.textViewport = (RectTransform)gameObject.transform;
            input.textComponent = mainText;
            input.placeholder = placeholder;
            input.targetGraphic = gameObject.GetComponentInChildren<Graphic>(true);
            var designText = mainText.text;
            input.text = placeholder != null ? string.Empty : designText;
            var lowerName = node.name?.ToLowerInvariant() ?? string.Empty;
            if (lowerName.Contains("password") || lowerName.Contains("[pwd]")) input.contentType = TMP_InputField.ContentType.Password;
        }

        private static void ApplyLocalization(GameObject gameObject, Node node, string screenName, Dictionary<string, (string, string)> rows)
        {
            if (node.type != NodeType.TEXT) return;
            var key = node.figmaBridge?.localizationKey;
            var existing = gameObject.GetComponent<FigmaLocalizedText>();
            if (string.IsNullOrWhiteSpace(key))
            {
                if (existing != null) Object.DestroyImmediate(existing);
                return;
            }
            var localized = existing != null ? existing : gameObject.AddComponent<FigmaLocalizedText>();
            localized.Key = key.Trim();
            localized.SourceText = node.characters;
            rows[localized.Key] = (node.characters ?? string.Empty, screenName);
        }

        private static void ApplyScreenFitter(GameObject root, FigmaImportProcessData data)
        {
            var settings = data.Settings;
            if (settings != null && !settings.FitScreensToParent)
            {
                if (root.TryGetComponent<FigmaScreenFitter>(out var existing)) UnityEngine.Object.DestroyImmediate(existing, true);
                return;
            }
            var rect = (RectTransform)root.transform;
            var designSize = rect.rect.size;
            var node = NodeFor(root, data);
            if (node?.size != null && node.size.x > 0 && node.size.y > 0) designSize = new Vector2(node.size.x, node.size.y);
            var designResolution = settings != null && settings.DesignResolution.x > 0 && settings.DesignResolution.y > 0
                ? new Vector2(settings.DesignResolution.x, settings.DesignResolution.y)
                : new Vector2(3840f, 2160f);
            if (!root.TryGetComponent<FigmaScreenFitter>(out var fitter)) fitter = root.AddComponent<FigmaScreenFitter>();
            fitter.DesignSize = designSize;
            fitter.DesignResolution = designResolution;
            fitter.FillParent = designSize.x >= designResolution.x * 0.98f && designSize.y >= designResolution.y * 0.98f;
            fitter.Mode = settings != null ? settings.ScreenFitMode : FigmaScreenFitter.FitMode.Expand;
            fitter.Match = settings != null ? settings.ScreenFitMatch : 0.5f;
        }

        private static void CollectNineSlice(GameObject gameObject, Node node, List<NineSliceRequest> requests)
        {
            if (!FigmaBridgeTags.HasNineSlice(node)) return;
            var image = gameObject.GetComponent<Image>();
            if (image == null || image is FigmaImage || image.sprite == null)
            {
                FigmaImportReport.Warning("9-slice needs the layer to be flattened into an image", node);
                return;
            }
            image.type = Image.Type.Sliced;
            image.fillCenter = true;
            var bounds = node.absoluteRenderBounds ?? node.absoluteBoundingBox;
            var nodeWidth = bounds?.width ?? node.size?.x ?? 0f;
            if (nodeWidth > 0) image.pixelsPerUnitMultiplier = image.sprite.rect.width / nodeWidth;
            requests.Add(new NineSliceRequest { SpritePath = AssetDatabase.GetAssetPath(image.sprite), Insets = node.figmaBridge.nineSlice, NodeWidth = nodeWidth });
        }

        private static void ApplyNineSlices(List<NineSliceRequest> requests)
        {
            foreach (var request in requests)
            {
                if (!FigmaAssetReuse.IsGeneratedAsset(request.SpritePath))
                {
                    FigmaImportReport.Warning($"9-slice borders weren't set because {request.SpritePath} isn't a Figma import; set them on that sprite in Unity", null, request.SpritePath);
                    continue;
                }
                var importer = AssetImporter.GetAtPath(request.SpritePath) as TextureImporter;
                if (importer == null || request.NodeWidth <= 0) continue;
                importer.GetSourceTextureWidthAndHeight(out var width, out var height);
                var scale = width / request.NodeWidth;
                var border = new Vector4(request.Insets.left * scale, request.Insets.bottom * scale, request.Insets.right * scale, request.Insets.top * scale);
                if (importer.spriteBorder == border) continue;
                importer.spriteBorder = border;
                importer.SaveAndReimport();
            }
        }

        private static void WriteLocalization(Dictionary<string, (string text, string screen)> rows)
        {
            if (rows.Count == 0) return;
            var folder = $"{FigmaPaths.FigmaAssetsRootFolder}/{LocalizationFolder}";
            Directory.CreateDirectory(folder);
            var path = $"{folder}/strings.csv";
            var merged = new SortedDictionary<string, (string text, string screen)>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                foreach (var fields in ParseCsv(File.ReadAllText(path)).Skip(1))
                {
                    if (fields.Count >= 2 && !string.IsNullOrEmpty(fields[0])) merged[fields[0]] = (fields[1], fields.Count > 2 ? fields[2] : string.Empty);
                }
            }
            foreach (var row in rows) merged[row.Key] = row.Value;
            var builder = new StringBuilder("key,text,screen\n");
            foreach (var row in merged) builder.Append($"{Csv(row.Key)},{Csv(row.Value.text)},{Csv(row.Value.screen)}\n");
            File.WriteAllText(path, builder.ToString());
            AssetDatabase.ImportAsset(path);
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }

        private static List<List<string>> ParseCsv(string content)
        {
            var rows = new List<List<string>>();
            var fields = new List<string>();
            var current = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < content.Length; i++)
            {
                var character = content[i];
                if (quoted)
                {
                    if (character == '"' && i + 1 < content.Length && content[i + 1] == '"') { current.Append('"'); i++; }
                    else if (character == '"') quoted = false;
                    else current.Append(character);
                }
                else if (character == '"') quoted = true;
                else if (character == ',') { fields.Add(current.ToString()); current.Clear(); }
                else if (character == '\r') { }
                else if (character == '\n')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                    rows.Add(fields);
                    fields = new List<string>();
                }
                else current.Append(character);
            }
            if (current.Length > 0 || fields.Count > 0)
            {
                fields.Add(current.ToString());
                rows.Add(fields);
            }
            return rows;
        }

        private static void WriteFlowMap(FigmaImportProcessData data)
        {
            var pages = data.SelectedPagesForImport ?? new List<Node>();
            var screens = pages.Where(page => page.children != null).SelectMany(page => page.children).Where(node => node.type == NodeType.FRAME).ToList();
            if (screens.Count == 0) return;
            var links = new List<FigmaFlowMap.Link>();
            foreach (var screen in screens) CollectLinks(screen, screen, data, links);

            var path = $"{FigmaPaths.FigmaAssetsRootFolder}/FlowMap.asset";
            var flowMap = AssetDatabase.LoadAssetAtPath<FigmaFlowMap>(path);
            if (flowMap == null)
            {
                if (links.Count == 0) return;
                flowMap = ScriptableObject.CreateInstance<FigmaFlowMap>();
                AssetDatabase.CreateAsset(flowMap, path);
            }
            var exportedScreens = new HashSet<string>(screens.Select(screen => FigmaBridgeTags.CleanName(screen.name)), StringComparer.OrdinalIgnoreCase);
            flowMap.Links.RemoveAll(link => exportedScreens.Contains(link.Screen));
            flowMap.Links.AddRange(links);
            EditorUtility.SetDirty(flowMap);
            AssetDatabase.SaveAssetIfDirty(flowMap);
        }

        private static void CollectLinks(Node node, Node screen, FigmaImportProcessData data, List<FigmaFlowMap.Link> links)
        {
            if (node.interactions != null)
            {
                foreach (var interaction in node.interactions.Where(interaction => interaction?.actions != null))
                {
                    foreach (var action in interaction.actions.Where(action => action != null))
                    {
                        var destination = string.Empty;
                        if (!string.IsNullOrEmpty(action.destinationId) && data.NodeLookupDictionary.TryGetValue(action.destinationId, out var destinationNode) && destinationNode != null)
                            destination = FigmaBridgeTags.CleanName(destinationNode.name);
                        else if (!string.IsNullOrEmpty(action.url)) destination = action.url;
                        links.Add(new FigmaFlowMap.Link
                        {
                            Screen = FigmaBridgeTags.CleanName(screen.name),
                            Element = FigmaBridgeTags.GetBindingName(node),
                            Trigger = interaction.trigger?.type ?? string.Empty,
                            Action = string.IsNullOrEmpty(action.navigation) ? action.type : action.navigation,
                            Destination = destination,
                        });
                    }
                }
            }
            if (node.children == null) return;
            foreach (var child in node.children) CollectLinks(child, screen, data, links);
        }

        private static void WriteDesignTokens(FigmaFile file)
        {
            var tokens = file?.designTokens;
            if (tokens == null) return;
            var path = $"{FigmaPaths.FigmaAssetsRootFolder}/DesignTokens.asset";
            var asset = AssetDatabase.LoadAssetAtPath<FigmaDesignTokens>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<FigmaDesignTokens>();
                AssetDatabase.CreateAsset(asset, path);
            }
            asset.Modes.Clear();
            asset.Colors.Clear();
            asset.Numbers.Clear();
            asset.Texts.Clear();
            asset.TextStyles.Clear();

            foreach (var variable in (tokens["variables"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var collection = variable.Value<string>("collection");
                var name = string.IsNullOrEmpty(collection) ? variable.Value<string>("name") : $"{collection}/{variable.Value<string>("name")}";
                var type = variable.Value<string>("type");
                foreach (var mode in (variable["valuesByMode"] as JObject ?? new JObject()).Properties())
                {
                    if (!asset.Modes.Contains(mode.Name)) asset.Modes.Add(mode.Name);
                    switch (type)
                    {
                        case "COLOR" when mode.Value is JObject color:
                            asset.Colors.Add(new FigmaDesignTokens.ColorToken { Name = name, Mode = mode.Name, Value = ToColor(color) });
                            break;
                        case "FLOAT":
                            asset.Numbers.Add(new FigmaDesignTokens.NumberToken { Name = name, Mode = mode.Name, Value = mode.Value.Type is JTokenType.Float or JTokenType.Integer ? mode.Value.Value<float>() : 0f });
                            break;
                        case "BOOLEAN":
                            asset.Numbers.Add(new FigmaDesignTokens.NumberToken { Name = name, Mode = mode.Name, Value = mode.Value.Type == JTokenType.Boolean && mode.Value.Value<bool>() ? 1f : 0f });
                            break;
                        default:
                            asset.Texts.Add(new FigmaDesignTokens.TextToken { Name = name, Mode = mode.Name, Value = mode.Value.ToString() });
                            break;
                    }
                }
            }
            foreach (var paintStyle in (tokens["paintStyles"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (paintStyle["color"] is JObject color)
                    asset.Colors.Add(new FigmaDesignTokens.ColorToken { Name = $"Styles/{paintStyle.Value<string>("name")}", Mode = string.Empty, Value = ToColor(color) });
            }
            foreach (var textStyle in (tokens["textStyles"] as JArray ?? new JArray()).OfType<JObject>())
            {
                asset.TextStyles.Add(new FigmaDesignTokens.TextStyleToken
                {
                    Name = textStyle.Value<string>("name"),
                    FontFamily = textStyle.Value<string>("fontFamily"),
                    FontStyle = textStyle.Value<string>("fontStyle"),
                    FontSize = textStyle.Value<float?>("fontSize") ?? 0f,
                    LetterSpacing = textStyle.Value<float?>("letterSpacing") ?? 0f,
                    LineHeight = textStyle.Value<float?>("lineHeightPx") ?? 0f,
                });
            }
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
        }

        private static UnityEngine.Color ToColor(JObject color) => new(
            color.Value<float?>("r") ?? 0f,
            color.Value<float?>("g") ?? 0f,
            color.Value<float?>("b") ?? 0f,
            color.Value<float?>("a") ?? 1f);
    }
}
