using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityFigmaBridge.Editor.Settings;
using UnityFigmaBridge.Editor.Utils;

namespace UnityFigmaBridge.Editor.FigmaApi
{
    
    /// <summary>
    /// Reason for server rendering
    /// </summary>
    public enum ServerRenderType
    {
        Substitution, // We want to replace a complex node with an image
        Export // We want to export this image
    }
        
    /// <summary>
    /// Encapsulates server render node data
    /// </summary>
    public class ServerRenderNodeData
    {
        public ServerRenderType RenderType = ServerRenderType.Substitution;
        public Node SourceNode;
    }
    
    public static class FigmaApiUtils
    {
        private static string WRITE_FILE_PATH = "Figma/FigmaOutput.json";
        
        /// <summary>
        /// Encapsulate download data
        /// </summary>
        public class FigmaDownloadQueueItem
        {
            public enum FigmaFileType
            {
                ImageFill,
                ServerRenderedImage
            }

            public FigmaFileType FileType;
            public string Url;
            public string FilePath;
        }
        
        


        /// <summary>
        /// Get Figma File Id from document Url
        /// </summary>
        /// <param name="url"Document Url</param>
        /// <returns>File Id</returns>
        public static (bool, string) GetFigmaDocumentIdFromUrl(string url)
        {
            // Legacy Format is https://www.figma.com/file/{DOC_ID}/{NAME}?node-id={NODE}
            // New format is https://www.figma.com/design/{DOC_ID}/{NAME}?node-id={NODE}
            
            var legacyInitialSection = "https://www.figma.com/file/";
            var modernInitialSection = "https://www.figma.com/design/";

            var legacyInitialSectionIndex = url.IndexOf(legacyInitialSection, StringComparison.Ordinal);
            var modernInitialSectionIndex = url.IndexOf(modernInitialSection, StringComparison.Ordinal);
            
            // If neither found, it's invalid
            if ( legacyInitialSectionIndex!= 0 && modernInitialSectionIndex!=0) return (false, "");
            // Select best fit
            var targetSectionToUse = legacyInitialSectionIndex == 0 ? legacyInitialSection : modernInitialSection;
            
            var remainder = url.Substring(targetSectionToUse.Length);
            var nextSeperatorIndex = remainder.IndexOf('/');
            if (nextSeperatorIndex == -1) return (false, "");
            return (true, remainder.Substring(0, nextSeperatorIndex));
        }

        /// <summary>
        /// Download a Figma doc from server and deserialize
        /// </summary>
        /// <param name="fileId">Figma File Id</param>
        /// <param name="accessToken">Figma Access Token</param>
        /// <param name="writeFile">Optionally write this file to disk</param>
        /// <returns>The deserialized Figma file</returns>
        /// <exception cref="Exception"></exception>
        public static FigmaFile ParseFigmaDocument(string json)
        {
            var settings = new JsonSerializerSettings
            {
                DefaultValueHandling = DefaultValueHandling.Include,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                Converters = { new TolerantEnumConverter() },
            };
            var figmaFile = JsonConvert.DeserializeObject<FigmaFile>(json, settings);
            FigmaDocumentSanitizer.Sanitize(figmaFile);
            return figmaFile;
        }

        public static async Task<FigmaFile> GetFigmaDocument(string fileId, string accessToken, bool writeFile)
        {
            var url =
                $"https://api.figma.com/v1/files/{fileId}?geometry=paths"; // We need geometry=paths to get rotation and full transform

            FigmaFile figmaFile = null;
            // Download the Figma Document
            var webRequest = UnityWebRequest.Get(url);
            webRequest.SetRequestHeader("X-Figma-Token", accessToken);
            await webRequest.SendWebRequest();

            if (webRequest.result == UnityWebRequest.Result.ProtocolError ||
                webRequest.result == UnityWebRequest.Result.ConnectionError)
            {
                throw new Exception($"Error downloading FIGMA document: {webRequest.error} url - {url}");
            }

            try
            {
                figmaFile = ParseFigmaDocument(webRequest.downloadHandler.text);

                Debug.Log($"Figma file downloaded, name {figmaFile.name}");
            }
            catch (Exception e)
            {
                throw new Exception($"Problem decoding Figma document JSON {e.ToString()}");
            }

            if (writeFile) File.WriteAllText(Path.Combine("Assets", WRITE_FILE_PATH), webRequest.downloadHandler.text);
            return figmaFile;
        }

        /// <summary>
        /// Requests a server-side rendering of nodes from a document, returning list of urls to download
        /// </summary>
        /// <param name="fileId">Figma File Id</param>
        /// <param name="accessToken">Figma Access Token</param>
        /// <param name="serverNodeCsvList">Csv List of nodes to render</param>
        /// <param name="serverRenderImageScale">Scale to render images at</param>
        /// <returns>List of urls to access the rendered images</returns>
        /// <exception cref="Exception"></exception>
        public static async Task<FigmaServerRenderData> GetFigmaServerRenderData(string fileId, string accessToken,
            string serverNodeCsvList, int serverRenderImageScale)
        {
            FigmaServerRenderData figmaServerRenderData = null;
            // Execute server-side rendering. Sending this webRequest will return a list of all images to download
            var serverRenderUrl =
                $"https://api.figma.com/v1/images/{fileId}?ids={serverNodeCsvList}&scale={serverRenderImageScale}&use_absolute_bounds=true";
            var webRequest = UnityWebRequest.Get(serverRenderUrl);
            webRequest.SetRequestHeader("X-Figma-Token", accessToken);

            await webRequest.SendWebRequest();
            if (webRequest.result == UnityWebRequest.Result.ProtocolError ||
                webRequest.result == UnityWebRequest.Result.ConnectionError)
            {
                throw new Exception(
                    $"Error downloading FIGMA Server Rendered Images: {webRequest.error} url - {serverRenderUrl}");
            }

            try
            {
                figmaServerRenderData =
                    JsonConvert.DeserializeObject<FigmaServerRenderData>(webRequest.downloadHandler.text);
            }
            catch (Exception e)
            {
                throw new Exception($"Problem decoding server render JSON {e.ToString()}");
            }

            return figmaServerRenderData;
        }

        /// <summary>
        /// Downloads image fill data for a Figma document
        /// </summary>
        /// <param name="fileId">Figma File Id</param>
        /// <param name="accessToken">Figma Access Token</param>
        /// <returns>List of image fills for the document</returns>
        /// <exception cref="Exception"></exception>
        public static async Task<FigmaImageFillData> GetDocumentImageFillData(string fileId, string accessToken)
        {
            FigmaImageFillData imageFillData;
            // Download a list all the image fills container in the Figma document
            var imageFillUrl = $"https://api.figma.com/v1/files/{fileId}/images";

            var webRequest = UnityWebRequest.Get(imageFillUrl);
            webRequest.SetRequestHeader("X-Figma-Token", accessToken);

            await webRequest.SendWebRequest();

            if (webRequest.result is UnityWebRequest.Result.ProtocolError or UnityWebRequest.Result.ConnectionError)
            {
                throw new Exception($"Error downloading FIGMA Image Fill Data: {webRequest.error} url - {imageFillUrl}");
            }
            try
            {
                imageFillData = JsonConvert.DeserializeObject<FigmaImageFillData>(webRequest.downloadHandler.text);
            }
            catch (Exception e)
            {
                throw new Exception($"Problem decoding image fill JSON {e.ToString()}");
            }

            return imageFillData;
        }


        /// <summary>
        /// Retrieves specific nodes from specific files
        /// </summary>
        /// <param name="fileId">Figma File Id</param>
        /// <param name="accessToken">Figma Access Token</param>
        /// <param name="nodeIds">List of Node Ids to process</param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public static async Task<FigmaFileNodes> GetFigmaFileNodes(string fileId, string accessToken,List<string> nodeIds)
        {
            FigmaFileNodes fileNodes;
            var externalComponentsJoined = string.Join(",",nodeIds);
            var componentsUrl = $"https://api.figma.com/v1/files/{fileId}/nodes/?ids={externalComponentsJoined}";
            
            // Download the FIGMA Document
            var webRequest = UnityWebRequest.Get(componentsUrl);
            webRequest.SetRequestHeader("X-Figma-Token",accessToken);
            await webRequest.SendWebRequest();

            if (webRequest.result is UnityWebRequest.Result.ProtocolError or UnityWebRequest.Result.ConnectionError)
            {
                throw new Exception($"Error downloading components: {webRequest.error} url - {componentsUrl}");
            }
            try
            {
                fileNodes = JsonConvert.DeserializeObject<FigmaFileNodes>(webRequest.downloadHandler.text);
                File.WriteAllText("ComponentNodes.json", webRequest.downloadHandler.text);
            }
            catch (Exception e)
            {
                throw new Exception($"Problem decoding Figma components JSON {e.ToString()}");
            }

            return fileNodes;
        }


        /// <summary>
        /// Generates a standardised list of files to download 
        /// </summary>
        /// <param name="imageFillData"></param>
        /// <param name="foundImageFills"></param>
        /// <param name="serverRenderData"></param>
        /// <param name="serverRenderNodes"></param>
        /// <returns></returns>
        public static List<FigmaDownloadQueueItem> GenerateDownloadQueue(FigmaImageFillData imageFillData,List<string> foundImageFills,List<FigmaServerRenderData> serverRenderData,List<ServerRenderNodeData> serverRenderNodes)
        {
            // Check if each image fill file has already been downloaded. If not, add to download list
            //Dictionary<string, string> filteredImageFillList = new Dictionary<string, string>();
            List<FigmaDownloadQueueItem> downloadList = new List<FigmaDownloadQueueItem>();
            foreach (var keyPair in imageFillData.meta.images)
            {
                // Only download if it is used in the document and not already downloaded
                if (foundImageFills.Contains(keyPair.Key) && !File.Exists(FigmaPaths.GetPathForImageFill(keyPair.Key)))
                {
                    downloadList.Add(new FigmaDownloadQueueItem
                    {
                        Url=keyPair.Value,
                        FilePath = FigmaPaths.GetPathForImageFill(keyPair.Key),
                        FileType = FigmaDownloadQueueItem.FigmaFileType.ImageFill
                    });
                }
            }

            // If required, process server render images
           foreach (var serverRenderDataEntry in serverRenderData)
            {
                foreach (var keyPair in serverRenderDataEntry.images)
                {
                    if (string.IsNullOrEmpty(keyPair.Value))
                    {
                        // if the url is invalid...
                        Debug.Log($"Can't download image for Server Node {keyPair.Key}");
                    }
                    else
                    {
                        // Always overwrite as may have changed
                        downloadList.Add(new FigmaDownloadQueueItem
                        {
                            Url = keyPair.Value,
                            FilePath = FigmaPaths.GetPathForServerRenderedImage(keyPair.Key, serverRenderNodes),
                            FileType = FigmaDownloadQueueItem.FigmaFileType.ServerRenderedImage
                        });
                    }
                }
            }

            return downloadList;
        }
        

        /// <summary>
        /// Download required files and process
        /// </summary>
        /// <param name="downloadItems"></param>
        public static byte[] PadImageToMultipleOfFour(byte[] bytes)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!texture.LoadImage(bytes, false)) return bytes;
                var width = texture.width;
                var height = texture.height;
                if (Mathf.Max(width, height) < 256 || (width % 4 == 0 && height % 4 == 0)) return bytes;
                var paddedWidth = (width + 3) / 4 * 4;
                var paddedHeight = (height + 3) / 4 * 4;
                var source = texture.GetPixels32();
                var padded = new Color32[paddedWidth * paddedHeight];
                var verticalOffset = paddedHeight - height;
                for (var y = 0; y < paddedHeight; y++)
                {
                    var sourceY = Mathf.Clamp(y - verticalOffset, 0, height - 1);
                    for (var x = 0; x < paddedWidth; x++)
                    {
                        var sourceX = Mathf.Min(x, width - 1);
                        padded[y * paddedWidth + x] = source[sourceY * width + sourceX];
                    }
                }
                var paddedTexture = new Texture2D(paddedWidth, paddedHeight, TextureFormat.RGBA32, false);
                try
                {
                    paddedTexture.SetPixels32(padded);
                    paddedTexture.Apply(false);
                    return paddedTexture.EncodeToPNG();
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(paddedTexture);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        public static void ConfigureImageImporter(string filePath, bool repeatWrap)
        {
            var textureImporter = AssetImporter.GetAtPath(filePath) as TextureImporter;
            if (textureImporter == null) return;
            ApplyImageImporterSettings(textureImporter, repeatWrap);
            textureImporter.SaveAndReimport();
        }

        private static bool ApplyImageImporterSettings(TextureImporter textureImporter, bool repeatWrap)
        {
            textureImporter.GetSourceTextureWidthAndHeight(out var width, out var height);
            var largest = Mathf.Max(width, height);
            var maxSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(largest, 32)), 32, 8192);
            var compressible = largest >= 256 && width % 4 == 0 && height % 4 == 0;
            var compression = compressible ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Uncompressed;
            var wrapMode = repeatWrap ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            var alreadyConfigured = textureImporter.textureType == TextureImporterType.Sprite
                                    && textureImporter.spriteImportMode == SpriteImportMode.Single
                                    && textureImporter.alphaIsTransparency
                                    && !textureImporter.mipmapEnabled
                                    && textureImporter.sRGBTexture
                                    && textureImporter.maxTextureSize == maxSize
                                    && textureImporter.textureCompression == compression
                                    && textureImporter.wrapMode == wrapMode;
            if (alreadyConfigured) return false;
            textureImporter.textureType = TextureImporterType.Sprite;
            textureImporter.spriteImportMode = SpriteImportMode.Single;
            textureImporter.alphaIsTransparency = true;
            textureImporter.mipmapEnabled = false;
            textureImporter.sRGBTexture = true;
            textureImporter.npotScale = TextureImporterNPOTScale.None;
            textureImporter.maxTextureSize = maxSize;
            textureImporter.textureCompression = compression;
            textureImporter.wrapMode = wrapMode;
            return true;
        }

        public static async Task DownloadFiles(List<FigmaDownloadQueueItem> downloadItems, UnityFigmaBridgeSettings settings)
        {
            var downloadCount = downloadItems.Count;
            var downloadIndex = 0;
            
            // Cycle through each required image and download
            foreach (var downloadItem in downloadItems)
            {
                EditorUtility.DisplayProgressBar("Importing Figma Document", $"Downloading Server Image {downloadIndex}/{downloadCount}", (float)downloadIndex/(float) downloadCount);
                try
                {
                    // Download and write the image data
                    var imageDownloadWebRequest = UnityWebRequest.Get(downloadItem.Url);
                    await imageDownloadWebRequest.SendWebRequest();
                    
                    byte[] imageBytes = imageDownloadWebRequest.downloadHandler.data;
                    
                    // Create the directory if needed
                    var directoryPath= Path.GetDirectoryName(downloadItem.FilePath);
                    if (!Directory.Exists(directoryPath)) Directory.CreateDirectory(directoryPath);
                    
                    File.WriteAllBytes(downloadItem.FilePath,imageBytes);
                    
                    // Refresh the asset database to ensure the asset has been created
                    AssetDatabase.ImportAsset(downloadItem.FilePath);
                    AssetDatabase.Refresh();
                    
                    ConfigureImageImporter(downloadItem.FilePath, downloadItem.FileType == FigmaDownloadQueueItem.FigmaFileType.ImageFill);

                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Error downloading image file '{downloadItem.Url}' of type {downloadItem.FileType} for path {downloadItem.FilePath}: {e.ToString()}");
                }
                downloadIndex++;
            }
        }

    
        /// <summary>
        /// Checks that existing assets are in the correct format
        /// </summary>
        public static void CheckExistingAssetProperties()
        {
            CheckImageFillTextureProperties();
        }

        /// <summary>
        /// Checks downloaded image fills
        /// </summary>
        private static void CheckImageFillTextureProperties()
        {
            foreach (var (folder, repeatWrap) in new[] { (FigmaPaths.FigmaImageFillFolder, true), (FigmaPaths.FigmaServerRenderedImagesFolder, false) })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var filePath in Directory.GetFiles(folder).Where(path => !path.EndsWith(".meta")))
                {
                    var textureImporter = AssetImporter.GetAtPath(filePath.Replace('\\', '/')) as TextureImporter;
                    if (textureImporter == null) continue;
                    if (ApplyImageImporterSettings(textureImporter, repeatWrap)) textureImporter.SaveAndReimport();
                }
            }
        }
    }
}