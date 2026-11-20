using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityFigmaBridge.Editor.FigmaApi;
using UnityFigmaBridge.Editor.Reporting;
using UnityFigmaBridge.Editor.Settings;
using UnityFigmaBridge.Editor.Utils;

namespace UnityFigmaBridge.Editor.Plugin
{
    [InitializeOnLoad]
    public static class FigmaPluginReceiver
    {
        public static readonly int[] AllowedPorts = { 37264, 37265, 37266 };
        private static int s_ListeningPort;

        public static int Port
        {
            get
            {
                var settings = UnityFigmaBridgeSettingsProvider.FindUnityBridgeSettingsAsset();
                var port = settings != null ? settings.ReceiverPort : AllowedPorts[0];
                return Array.IndexOf(AllowedPorts, port) >= 0 ? port : AllowedPorts[0];
            }
        }

        public static void ApplyPortSetting()
        {
            if (s_Listener == null || s_ListeningPort == Port) return;
            StopListening();
            StartListening();
        }
        public const int ProtocolVersion = 2;
        public const string MinPluginVersion = "2.0.0";
        private const string EnabledPrefKey = "FigmaBridge.PluginReceiverEnabled";
        private const string ProtocolHeader = "X-Figma-Bridge";
        private const string TokenHeader = "X-Figma-Bridge-Token";
        private const string PluginVersionHeader = "X-Figma-Bridge-Plugin-Version";
        private const string ComponentsCanvasId = "figma-bridge-components";
        private static readonly Regex ImageHashPattern = new("^[0-9a-f]{20,64}$");
        private static readonly ConcurrentQueue<HttpListenerContext> s_PendingContexts = new();

        private static HttpListener s_Listener;
        private static string s_PendingDocumentJson;
        private static List<string> s_PendingScreenNames = new();
        private static Dictionary<string, string> s_PendingScreenHashes = new();
        private static List<ServerRenderNodeData> s_PendingRenderNodes = new();
        private static bool s_Importing;
        private static List<(string family, string style)> s_FontCache;

        static FigmaPluginReceiver()
        {
            if (Application.isBatchMode) return;
            EditorApplication.update += ProcessPendingContexts;
            AssemblyReloadEvents.beforeAssemblyReload += StopListening;
            EditorApplication.quitting += StopListening;
            if (EditorPrefs.GetBool(EnabledPrefKey, true)) StartListening();
        }

        public static bool IsListening => s_Listener != null && s_Listener.IsListening;

        [MenuItem("Tools/Figma Bridge/Pair Figma Plugin", false, 0)]
        private static void ShowPairingCode()
        {
            var code = PairingCode;
            EditorGUIUtility.systemCopyBuffer = code;
            EditorUtility.DisplayDialog("Pair the Figma plugin",
                $"Pairing code: {code}\n\nIt's copied to the clipboard. Paste it into the Export to Unity plugin in Figma when it asks.", "OK");
        }

        private static string StablePluginFolder => Path.GetFullPath(Path.Combine("UserSettings", "FigmaToUnityPlugin"));

        private static string PackagePluginFolder
        {
            get
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FigmaPluginReceiver).Assembly);
                return package != null ? Path.Combine(package.resolvedPath, "FigmaPlugin~") : null;
            }
        }

        private static bool CopyPluginToStableFolder()
        {
            var source = PackagePluginFolder;
            if (source == null || !Directory.Exists(source)) return false;
            Directory.CreateDirectory(StablePluginFolder);
            foreach (var file in Directory.GetFiles(source))
            {
                var target = Path.Combine(StablePluginFolder, Path.GetFileName(file));
                if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(file))) File.Copy(file, target, true);
            }
            return true;
        }

        [InitializeOnLoadMethod]
        private static void KeepPluginCopyUpToDate()
        {
            if (Application.isBatchMode || !Directory.Exists(StablePluginFolder)) return;
            try
            {
                CopyPluginToStableFolder();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Figma Bridge could not update the Figma plugin copy: {exception.Message}");
            }
        }

        [MenuItem("Tools/Figma Bridge/Set Up Figma Plugin", false, 2)]
        private static void SetUpFigmaPlugin()
        {
            if (!CopyPluginToStableFolder())
            {
                EditorUtility.DisplayDialog("Figma Bridge", "The Figma plugin files could not be found in the package.", "OK");
                return;
            }
            var manifest = Path.Combine(StablePluginFolder, "manifest.json");
            EditorGUIUtility.systemCopyBuffer = manifest;
            EditorUtility.RevealInFinder(manifest);
            EditorUtility.DisplayDialog("Set up the Figma plugin",
                "In the Figma desktop app open a design file, then use the Figma menu: Plugins > Development > Import plugin from manifest... and pick this file:\n\n" + manifest +
                "\n\nThe path is copied to the clipboard. Unity keeps this copy up to date when the package updates, so you only import it once per project.", "OK");
        }

        [MenuItem("Tools/Figma Bridge/Reset Pairing Code", false, 1)]
        private static void ResetPairingCode()
        {
            EditorPrefs.DeleteKey(PairingPrefKey);
            ShowPairingCode();
        }

        [MenuItem("Tools/Figma Bridge/Plugin Receiver/Enable")]
        private static void Enable()
        {
            EditorPrefs.SetBool(EnabledPrefKey, true);
            StartListening();
        }

        [MenuItem("Tools/Figma Bridge/Plugin Receiver/Disable")]
        private static void Disable()
        {
            EditorPrefs.SetBool(EnabledPrefKey, false);
            StopListening();
        }

        [MenuItem("Tools/Figma Bridge/Re-import Last Export", false, 20)]
        private static async void ReimportLastExport()
        {
            if (!File.Exists(ExportDocumentPath))
            {
                EditorUtility.DisplayDialog("Figma Bridge", "There is no previous export on this computer yet.", "OK");
                return;
            }
            var report = await RunImport(File.ReadAllText(ExportDocumentPath), null, "Re-import of last export");
            FigmaImportReportWindow.ShowReport(report);
        }

        [MenuItem("Tools/Figma Bridge/Re-import Last Export", true)]
        private static bool CanReimportLastExport() => !s_Importing && !Application.isPlaying;

        [MenuItem("Tools/Figma Bridge/Import Export File...", false, 21)]
        private static async void ImportBundleFile()
        {
            var path = EditorUtility.OpenFilePanel("Import Figma export file", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            var report = await ImportBundle(path);
            FigmaImportReportWindow.ShowReport(report);
        }

        private static string PairingPrefKey => $"FigmaBridge.PairingCode.{HashOf(Application.dataPath)}";

        public static string PairingCode
        {
            get
            {
                var code = EditorPrefs.GetString(PairingPrefKey, null);
                if (!string.IsNullOrEmpty(code)) return code;
                const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
                var bytes = new byte[6];
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
                code = new string(bytes.Select(value => alphabet[value % alphabet.Length]).ToArray());
                EditorPrefs.SetString(PairingPrefKey, code);
                return code;
            }
        }

        private static string HashOf(string text)
        {
            using var md5 = MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty).Substring(0, 12);
        }

        private static string ExportDocumentPath => Path.GetFullPath(Path.Combine("Library", "FigmaBridge", "plugin-export.json"));

        private static string PackageVersion =>
            UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FigmaPluginReceiver).Assembly)?.version ?? "2.0.0";

        private static void StartListening()
        {
            if (s_Listener != null) return;
            try
            {
                var port = Port;
                s_Listener = new HttpListener();
                s_Listener.Prefixes.Add($"http://localhost:{port}/");
                s_Listener.Start();
                s_ListeningPort = port;
                Task.Run(() => AcceptLoop(s_Listener));
                Debug.Log($"Figma Bridge plugin receiver listening on http://localhost:{port}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Figma Bridge plugin receiver could not start on port {Port}: {e.Message}. If another Unity project is open, give this one a different Receiver Port ({string.Join(", ", AllowedPorts)}) in the Figma Bridge settings asset.");
                s_Listener = null;
                s_ListeningPort = 0;
            }
        }

        private static void StopListening()
        {
            var listener = s_Listener;
            s_Listener = null;
            try { listener?.Close(); } catch (ObjectDisposedException) { }
        }

        private static async Task AcceptLoop(HttpListener listener)
        {
            while (listener.IsListening)
            {
                try
                {
                    s_PendingContexts.Enqueue(await listener.GetContextAsync());
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        private static void ProcessPendingContexts()
        {
            while (s_PendingContexts.TryDequeue(out var context))
                HandleContext(context);
        }

        private static async void HandleContext(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var origin = request.Headers["Origin"];
            if (origin == "null") response.AddHeader("Access-Control-Allow-Origin", "null");

            try
            {
                if (request.HttpMethod == "OPTIONS")
                {
                    response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    response.AddHeader("Access-Control-Allow-Headers", $"Content-Type, {ProtocolHeader}, {TokenHeader}, {PluginVersionHeader}");
                    response.AddHeader("Access-Control-Allow-Private-Network", "true");
                    response.AddHeader("Access-Control-Max-Age", "600");
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                if (origin != null && origin != "null")
                {
                    await Respond(response, 403, new { error = "Only the Figma plugin may call this receiver" });
                    return;
                }

                if (request.Headers[ProtocolHeader] == null)
                {
                    await Respond(response, 400, new { error = $"Missing {ProtocolHeader} header" });
                    return;
                }

                var path = request.Url.AbsolutePath.TrimEnd('/');
                var paired = string.Equals(request.Headers[TokenHeader]?.Trim(), PairingCode, StringComparison.OrdinalIgnoreCase);
                if (path == "/ping")
                {
                    await Respond(response, 200, BuildPing(paired));
                    return;
                }

                if (!paired)
                {
                    await Respond(response, 401, new { error = "This plugin isn't paired with Unity yet. In Unity, use Tools > Figma Bridge > Pair Figma Plugin.", pairingRequired = true });
                    return;
                }

                var pluginVersion = request.Headers[PluginVersionHeader];
                if (string.IsNullOrEmpty(pluginVersion) || CompareVersions(pluginVersion, MinPluginVersion) < 0)
                {
                    await Respond(response, 426, new { error = $"The Figma plugin is too old (needs {MinPluginVersion}). Pull the latest project and reopen the plugin." });
                    return;
                }

                switch (path)
                {
                    case "/document":
                        await HandleDocument(request, response);
                        return;
                    case "/image":
                        await HandleImage(request, response);
                        return;
                    case "/render":
                        await HandleRender(request, response);
                        return;
                    case "/commit":
                        await HandleCommit(response);
                        return;
                    default:
                        await Respond(response, 404, new { error = $"Unknown endpoint {path}" });
                        return;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Figma Bridge plugin receiver error: {e}");
                try { await Respond(response, 500, new { error = e.Message }); } catch (Exception) { }
            }
        }

        private static object BuildPing(bool paired)
        {
            var settings = UnityFigmaBridgeSettingsProvider.FindUnityBridgeSettingsAsset();
            if (!paired)
            {
                return new
                {
                    ok = true,
                    protocol = ProtocolVersion,
                    packageVersion = PackageVersion,
                    minPluginVersion = MinPluginVersion,
                    project = Application.productName,
                    importing = s_Importing,
                    paired = false,
                };
            }
            var resolution = settings != null ? settings.DesignResolution : new Vector2Int(3840, 2160);
            return new
            {
                ok = true,
                protocol = ProtocolVersion,
                packageVersion = PackageVersion,
                minPluginVersion = MinPluginVersion,
                project = Application.productName,
                importing = s_Importing,
                paired = true,
                designResolution = new { width = resolution.x, height = resolution.y },
                fonts = GetProjectFonts().Select(font => new { family = font.family, style = font.style }).ToList(),
                googleFonts = settings == null || settings.EnableGoogleFontsDownloads,
            };
        }

        private static List<(string family, string style)> GetProjectFonts()
        {
            if (s_FontCache != null) return s_FontCache;
            s_FontCache = new List<(string, string)>();
            foreach (var guid in AssetDatabase.FindAssets("t:TMP_FontAsset"))
            {
                var fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (fontAsset == null || string.IsNullOrEmpty(fontAsset.faceInfo.familyName)) continue;
                var entry = (fontAsset.faceInfo.familyName, fontAsset.faceInfo.styleName ?? "Regular");
                if (!s_FontCache.Contains(entry)) s_FontCache.Add(entry);
            }
            return s_FontCache;
        }

        public static int CompareVersions(string left, string right)
        {
            int[] Parse(string version) => (version ?? "0").Split('.').Select(part => int.TryParse(new string(part.TakeWhile(char.IsDigit).ToArray()), out var number) ? number : 0).ToArray();
            var a = Parse(left);
            var b = Parse(right);
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var difference = (i < a.Length ? a[i] : 0) - (i < b.Length ? b[i] : 0);
                if (difference != 0) return Math.Sign(difference);
            }
            return 0;
        }

        private static async Task HandleDocument(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (s_Importing)
            {
                await Respond(response, 409, new { error = "Unity is still importing the previous export" });
                return;
            }
            var json = Encoding.UTF8.GetString(await ReadBody(request));
            var plan = PrepareDocument(json);
            var settings = UnityFigmaBridgeSettingsProvider.FindUnityBridgeSettingsAsset();
            await Respond(response, 200, new
            {
                images = plan.missingImages,
                renders = s_PendingRenderNodes.Select(node => node.SourceNode.id).ToList(),
                scale = settings != null ? settings.ServerRenderImageScale : 1,
                plan = new { screens = plan.screens, deletions = plan.deletions, warnings = new object[0] },
            });
        }

        private class DocumentPlan
        {
            public List<string> missingImages = new();
            public List<object> screens = new();
            public List<string> deletions = new();
        }

        private static DocumentPlan PrepareDocument(string json)
        {
            var plan = new DocumentPlan();
            var figmaFile = FigmaApiUtils.ParseFigmaDocument(json);
            var pageIds = FigmaDataUtils.GetPageNodes(figmaFile).Select(page => page.id).ToList();
            var missingComponents = FigmaDataUtils.FindMissingComponentDefinitions(figmaFile);
            var settings = UnityFigmaBridgeSettingsProvider.FindUnityBridgeSettingsAsset();
            var exportMarkedNodes = settings == null || settings.GenerateNodesMarkedForExport;
            s_PendingRenderNodes = FigmaDataUtils.FindAllServerRenderNodesInFile(figmaFile, missingComponents, pageIds)
                .Where(node => exportMarkedNodes || node.RenderType != ServerRenderType.Export)
                .ToList();
            s_PendingDocumentJson = json;
            var registry = FigmaBridgeRegistry.Load();
            plan.missingImages = FigmaDataUtils.GetAllImageFillIdsFromFile(figmaFile, pageIds)
                .Distinct()
                .Where(hash => !File.Exists(FigmaPaths.GetPathForImageFill(hash)) && !FigmaAssetReuse.IsReusedImageAvailable(registry, hash))
                .ToList();
            s_PendingScreenNames = new List<string>();
            s_PendingScreenHashes = new Dictionary<string, string>();
            var rawDocument = JObject.Parse(json);
            var rawPages = rawDocument["document"]?["children"] as JArray ?? new JArray();
            foreach (var rawPage in rawPages.OfType<JObject>())
            {
                var pageId = rawPage.Value<string>("id");
                if (pageId == ComponentsCanvasId) continue;
                foreach (var rawScreen in (rawPage["children"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    if (rawScreen.Value<string>("type") != "FRAME") continue;
                    var screenName = rawScreen.Value<string>("name");
                    s_PendingScreenNames.Add(screenName);
                    var key = FigmaBridgeRegistry.ScreenKey(pageId, screenName);
                    var hash = FigmaBridgeRegistry.HashText(rawScreen.ToString(Formatting.None));
                    s_PendingScreenHashes[key] = hash;
                    var status = !registry.screenHashes.TryGetValue(key, out var previousHash) ? "new" : previousHash == hash ? "unchanged" : "changed";
                    plan.screens.Add(new { name = screenName, status });
                }
            }

            if (!figmaFile.partialExport)
            {
                var exportedScreenFiles = new HashSet<string>(s_PendingScreenNames.Select(FigmaPaths.SafeFileName), StringComparer.OrdinalIgnoreCase);
                foreach (var pair in registry.prefabs)
                {
                    if (!pageIds.Contains(pair.Value)) continue;
                    if (!pair.Key.StartsWith(FigmaPaths.FigmaScreenPrefabFolder + "/")) continue;
                    var prefabName = Path.GetFileNameWithoutExtension(pair.Key);
                    if (!exportedScreenFiles.Contains(prefabName) && File.Exists(pair.Key)) plan.deletions.Add(prefabName);
                }
            }
            return plan;
        }

        private static async Task HandleImage(HttpListenerRequest request, HttpListenerResponse response)
        {
            var hash = request.QueryString["hash"] ?? string.Empty;
            if (!ImageHashPattern.IsMatch(hash))
            {
                await Respond(response, 400, new { error = "Invalid image hash" });
                return;
            }
            var imagePath = FigmaPaths.GetPathForImageFill(hash);
            var body = await ReadBody(request);
            if (!File.Exists(imagePath)) FigmaAssetReuse.RecordNewImageFill(hash);
            WriteImportedImage(imagePath, body, true);
            await Respond(response, 200, new { ok = true });
        }

        private static async Task HandleRender(HttpListenerRequest request, HttpListenerResponse response)
        {
            var nodeId = request.QueryString["id"] ?? string.Empty;
            var renderPath = GetRenderPath(nodeId);
            if (renderPath == null)
            {
                await Respond(response, 400, new { error = "Render was not requested for this node" });
                return;
            }
            WriteImportedImage(renderPath, await ReadBody(request), false);
            await Respond(response, 200, new { ok = true });
        }

        private static string GetRenderPath(string nodeId)
        {
            if (s_PendingRenderNodes.All(node => node.SourceNode.id != nodeId)) return null;
            var filePath = FigmaPaths.GetPathForServerRenderedImage(nodeId, s_PendingRenderNodes).Replace('\\', '/');
            return filePath.StartsWith("Assets/") && !filePath.Contains("..") ? filePath : null;
        }

        private static async Task HandleCommit(HttpListenerResponse response)
        {
            if (s_PendingDocumentJson == null)
            {
                await Respond(response, 400, new { error = "No document was sent before commit" });
                return;
            }
            var report = await RunImport(s_PendingDocumentJson, s_PendingScreenHashes, "Figma plugin");
            if (report == null)
            {
                await Respond(response, 409, new { error = "Unity is already importing" });
                return;
            }
            s_PendingDocumentJson = null;
            await Respond(report.HasErrors ? 500 : 200, response, report);
        }

        private static Task Respond(int statusCode, HttpListenerResponse response, FigmaImportReport report)
        {
            object Entry(FigmaReportEntry entry) => new { message = entry.Message, nodeId = entry.NodeId, nodeName = entry.NodeName };
            return Respond(response, statusCode, new
            {
                ok = !report.HasErrors,
                error = report.HasErrors ? report.OfSeverity(FigmaReportSeverity.Error).First().Message : null,
                screens = s_PendingScreenNames,
                report = new
                {
                    errors = report.OfSeverity(FigmaReportSeverity.Error).Select(Entry).ToList(),
                    warnings = report.OfSeverity(FigmaReportSeverity.Warning).Select(Entry).ToList(),
                    info = report.OfSeverity(FigmaReportSeverity.Info).Select(Entry).ToList(),
                },
            });
        }

        private static async Task<FigmaImportReport> RunImport(string documentJson, Dictionary<string, string> screenHashes, string source)
        {
            if (s_Importing) return null;
            s_Importing = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ExportDocumentPath));
                File.WriteAllText(ExportDocumentPath, documentJson);
                AssetDatabase.Refresh();
                var report = await UnityFigmaBridgeImporter.ImportLocalDocument(ExportDocumentPath, source);
                if (!report.HasErrors && screenHashes != null)
                {
                    var registry = FigmaBridgeRegistry.Load();
                    foreach (var pair in screenHashes) registry.screenHashes[pair.Key] = pair.Value;
                    registry.Save();
                }
                s_FontCache = null;
                return report;
            }
            finally
            {
                s_Importing = false;
            }
        }

        public static async Task<FigmaImportReport> ImportBundle(string bundlePath)
        {
            JObject bundle;
            try
            {
                bundle = JObject.Parse(File.ReadAllText(bundlePath));
            }
            catch (Exception exception)
            {
                var failed = FigmaImportReport.Begin("Export file");
                FigmaImportReport.Error($"The export file can't be read: {exception.Message}");
                FigmaImportReport.End();
                return failed;
            }
            var documentJson = bundle["document"]?.ToString(Formatting.None);
            if (string.IsNullOrEmpty(documentJson))
            {
                var failed = FigmaImportReport.Begin("Export file");
                FigmaImportReport.Error("The export file has no document in it");
                FigmaImportReport.End();
                return failed;
            }
            PrepareDocument(documentJson);
            var bundleRegistry = FigmaBridgeRegistry.Load();
            foreach (var image in (bundle["images"] as JObject ?? new JObject()).Properties())
            {
                if (!ImageHashPattern.IsMatch(image.Name) || FigmaAssetReuse.IsReusedImageAvailable(bundleRegistry, image.Name)) continue;
                var imagePath = FigmaPaths.GetPathForImageFill(image.Name);
                if (!File.Exists(imagePath)) FigmaAssetReuse.RecordNewImageFill(image.Name);
                WriteImportedImage(imagePath, Convert.FromBase64String(image.Value.ToString()), true);
            }
            foreach (var render in (bundle["renders"] as JObject ?? new JObject()).Properties())
            {
                var renderPath = GetRenderPath(render.Name);
                if (renderPath == null) continue;
                WriteImportedImage(renderPath, Convert.FromBase64String(render.Value.ToString()), false);
            }
            var report = await RunImport(documentJson, s_PendingScreenHashes, $"Export file {Path.GetFileName(bundlePath)}");
            s_PendingDocumentJson = null;
            return report;
        }

        private static void WriteImportedImage(string filePath, byte[] bytes, bool repeatWrap)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(filePath, FigmaApiUtils.PadImageToMultipleOfFour(bytes));
            AssetDatabase.ImportAsset(filePath);
            FigmaApiUtils.ConfigureImageImporter(filePath, repeatWrap);
        }

        private static async Task<byte[]> ReadBody(HttpListenerRequest request)
        {
            using var memory = new MemoryStream();
            await request.InputStream.CopyToAsync(memory);
            return memory.ToArray();
        }

        private static async Task Respond(HttpListenerResponse response, int statusCode, object payload)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.Close();
        }
    }
}
