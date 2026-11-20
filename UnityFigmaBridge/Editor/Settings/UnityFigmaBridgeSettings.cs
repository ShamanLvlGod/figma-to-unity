using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityFigmaBridge.Editor.FigmaApi;

namespace UnityFigmaBridge.Editor.Settings
{
    [Serializable]
    public class FigmaFontOverride
    {
        public string FigmaFamily;
        public int FigmaWeight;
        public TMP_FontAsset Font;
    }

    public class UnityFigmaBridgeSettings : ScriptableObject
    {
       
        [Tooltip("The FIGMA Document URL to import")]
        public string DocumentUrl;
        
        [Tooltip("Generate logic and linking of screens based on FIGMA's 'Prototype' settings")]
        public bool BuildPrototypeFlow=true;
        
        [Space(10)]
        [Tooltip("Scene used for prototype assets, including canvas")]
        public string RunTimeAssetsScenePath;
        
        [Tooltip("Enable Auto layout components (Horizontal/Vertical layout) (EXPERIMENTAL)")]
        public bool EnableAutoLayout = false;
        
        [Tooltip("C# Namespace filter for binding MonoBehaviours for screens. Use this to ensure it will only bind to MonoBehaviours in that namespace (eg specify 'MyGame.UI' to only bind MyGame.UI.PlayScreen node to 'PlayScreen')")]
        public string ScreenBindingNamespace="";
        
        [Tooltip("Scale for rendering server images")]
        public int ServerRenderImageScale=1;

        [Tooltip("Screen size the designs are made at. The Figma plugin warns about frames of a different size.")]
        public Vector2Int DesignResolution = new Vector2Int(3840, 2160);

        [Tooltip("Tick this to enable downloading missing fonts from Google Fonts")]
        public bool EnableGoogleFontsDownloads = true;

        [Tooltip("Project folder that imported screens, components and images are written to. Move the folder in Unity before changing this, or the next import starts fresh in the new place")]
        public string OutputFolder = "Assets/Figma";

        [Tooltip("Local port the Figma plugin connects to. The plugin looks on 37264, 37265 and 37266, so give each Unity project you keep open at the same time its own port")]
        public int ReceiverPort = 37264;

        [Tooltip("Imported screens scale themselves to fill whatever canvas or panel they are placed under")]
        public bool FitScreensToParent = true;

        [Tooltip("Expand shows the whole design and gives extra space to the Figma constraints. Shrink fills and crops. Match blends between fitting width and height")]
        public UnityFigmaBridge.Runtime.UI.FigmaScreenFitter.FitMode ScreenFitMode = UnityFigmaBridge.Runtime.UI.FigmaScreenFitter.FitMode.Expand;

        [Range(0f, 1f)]
        [Tooltip("Only used by Match: 0 fits the width, 1 fits the height")]
        public float ScreenFitMatch = 0.5f;

        [Tooltip("Use fonts and images that already exist in the project instead of importing copies of them")]
        public bool ReuseProjectAssets = true;

        [Tooltip("Fonts to use for a Figma font family. Weight 0 covers every weight of the family")]
        public List<FigmaFontOverride> FontOverrides = new();

        [Tooltip("Generate a C# file containing all found screens")]
        public bool CreateScreenNameCSharpFile = false;
        
        [Tooltip("If false, the generator will not attempt to build any nodes marked for export")]
        public bool GenerateNodesMarkedForExport = true;
        
        [Tooltip("If true, download only selected pages and screens")]
        public bool OnlyImportSelectedPages = false;

        public string LocalDocumentPath = "";

        [HideInInspector]
        public List<FigmaPageData> PageDataList = new ();

        private void OnValidate()
        {
            UnityFigmaBridge.Editor.Utils.FigmaPaths.RefreshRootFolder();
            UnityEditor.EditorApplication.delayCall += UnityFigmaBridge.Editor.Plugin.FigmaPluginReceiver.ApplyPortSetting;
        }

        public string FileId {
            get
            {
                var (isValid, fileId) = FigmaApiUtils.GetFigmaDocumentIdFromUrl(DocumentUrl);
                return isValid ? fileId : "";
            }
        }
        
        public void RefreshForUpdatedPages(FigmaFile file)
        {
            // Get all pages from Figma Doc
            var pageNodeList = FigmaDataUtils.GetPageNodes(file);
            var downloadPageNodeIdList = pageNodeList.Select(p => p.id).ToList();

            // Get a list of all pages in the settings file
            var settingsPageDataIdList = PageDataList.Select(p => p.NodeId).ToList();

            // Build a list of all new pages to add
            var addPageIdList = downloadPageNodeIdList.Except(settingsPageDataIdList);
            foreach (var addPageId in addPageIdList)
            {
                var addNode = pageNodeList.FirstOrDefault(p => p.id == addPageId);
                PageDataList.Add(new FigmaPageData(addNode.name, addNode.id));
            }
            
            // Build a list of removed pages to remove from list
            var deletePageIdList = settingsPageDataIdList.Except(downloadPageNodeIdList);
            foreach (var deletePageId in deletePageIdList)
            {
                var index = PageDataList.FindIndex(p => p.NodeId == deletePageId);
                PageDataList.RemoveAt(index);
            }
            PageDataList.OrderBy(p => p.NodeId);
        }
    }

    [Serializable]
    public class FigmaPageData
    {
        public string Name;
        public string NodeId;
        public bool Selected;

        public FigmaPageData(){}

        public FigmaPageData(string name, string nodeId)
        {
            Name = name;
            NodeId = nodeId;
            Selected = true; // default is true
        }
    }
    
}