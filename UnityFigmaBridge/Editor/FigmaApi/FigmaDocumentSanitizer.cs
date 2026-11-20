using System.Collections.Generic;
using System.Linq;
using UnityFigmaBridge.Editor.Reporting;

namespace UnityFigmaBridge.Editor.FigmaApi
{
    public static class FigmaDocumentSanitizer
    {
        public static void Sanitize(FigmaFile file)
        {
            if (file == null) return;
            file.components ??= new Dictionary<string, Component>();
            file.styles ??= new Dictionary<string, Style>();
            if (file.document == null) return;
            file.document.children ??= new Node[0];
            var fallbackStyle = FindFallbackTextStyle(file.document);
            foreach (var page in file.document.children.Where(page => page != null))
            {
                page.children ??= new Node[0];
                page.flowStartingPoints ??= new FlowStartingPoint[0];
                foreach (var child in page.children.Where(child => child != null))
                    SanitizeNode(child, null, fallbackStyle);
            }
        }

        private static TypeStyle FindFallbackTextStyle(Node root)
        {
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.type == NodeType.TEXT && node.style != null && !string.IsNullOrEmpty(node.style.fontFamily)) return node.style;
                if (node.children == null) continue;
                foreach (var child in node.children.Where(child => child != null)) stack.Push(child);
            }
            return new TypeStyle { fontFamily = "Inter", fontWeight = 400, fontSize = 24 };
        }

        private static void SanitizeNode(Node node, Node parent, TypeStyle fallbackStyle)
        {
            node.name ??= "Unnamed";
            node.fills = node.fills?.Where(paint => paint != null).ToArray() ?? new Paint[0];
            node.strokes = node.strokes?.Where(paint => paint != null).ToArray() ?? new Paint[0];
            node.effects = node.effects?.Where(effect => effect != null).ToArray() ?? new Effect[0];
            node.constraints ??= new LayoutConstraint
            {
                horizontal = LayoutConstraint.HorizontalLayoutConstraint.LEFT,
                vertical = LayoutConstraint.VerticalLayoutConstraint.TOP,
            };

            if (node.size == null && node.absoluteBoundingBox != null)
                node.size = new Vector { x = node.absoluteBoundingBox.width, y = node.absoluteBoundingBox.height };
            if (node.relativeTransform == null)
            {
                var x = node.absoluteBoundingBox != null && parent?.absoluteBoundingBox != null ? node.absoluteBoundingBox.x - parent.absoluteBoundingBox.x : 0f;
                var y = node.absoluteBoundingBox != null && parent?.absoluteBoundingBox != null ? node.absoluteBoundingBox.y - parent.absoluteBoundingBox.y : 0f;
                node.relativeTransform = new float[,] { { 1, 0, x }, { 0, 1, y } };
            }
            if (node.size == null)
            {
                node.size = new Vector { x = 0, y = 0 };
                FigmaImportReport.Warning("Layer has no size in the export, so it is built empty", node);
            }
            if (node.absoluteBoundingBox == null)
            {
                var parentX = parent?.absoluteBoundingBox?.x ?? 0f;
                var parentY = parent?.absoluteBoundingBox?.y ?? 0f;
                node.absoluteBoundingBox = new Rectangle
                {
                    x = parentX + node.relativeTransform[0, 2],
                    y = parentY + node.relativeTransform[1, 2],
                    width = node.size.x,
                    height = node.size.y,
                };
            }

            if (node.type == NodeType.TEXT)
            {
                node.characters ??= string.Empty;
                if (node.style == null)
                {
                    node.style = fallbackStyle;
                    FigmaImportReport.Warning("Text layer has no text style in the export; using the document's first text style", node);
                }
            }

            if (node.type == NodeType.DOCUMENT)
                FigmaImportReport.Info("Layer of a type this importer doesn't know yet; built as an empty container", node);

            if (node.children == null) return;
            node.children = node.children.Where(child => child != null).ToArray();
            foreach (var child in node.children) SanitizeNode(child, node, fallbackStyle);
        }
    }
}
