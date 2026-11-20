using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityFigmaBridge.Editor.FigmaApi;
using UnityFigmaBridge.Editor.Fonts;
using UnityFigmaBridge.Editor.Reporting;
using UnityFigmaBridge.Editor.Utils;
using UnityFigmaBridge.Runtime.UI;
using Color = UnityEngine.Color;

namespace UnityFigmaBridge.Editor.Nodes
{
    public static class FigmaNodeManager
    {
        private const float FigmaToTextMeshProSpacingAdjustment = -0.7f;

        public static void ApplyUnityComponentPropertiesForNode(GameObject nodeGameObject, Node node, FigmaImportProcessData figmaImportProcessData)
        {
            switch (node.type)
            {
                case NodeType.FRAME:
                case NodeType.RECTANGLE:
                case NodeType.ELLIPSE:
                case NodeType.STAR:
                case NodeType.COMPONENT:
                case NodeType.INSTANCE:
                case NodeType.SECTION:
                    if (NodeIsSubstitution(node, figmaImportProcessData)) break;
                    ApplyShapeProperties(nodeGameObject, node);
                    break;
                case NodeType.TEXT:
                    ApplyTextProperties(nodeGameObject, node, figmaImportProcessData);
                    break;
            }

            var canvasGroup = nodeGameObject.GetComponent<CanvasGroup>();
            if (node.opacity < 0.999f)
            {
                if (canvasGroup == null) canvasGroup = nodeGameObject.AddComponent<CanvasGroup>();
                canvasGroup.alpha = node.opacity;
            }
            else if (canvasGroup != null)
            {
                canvasGroup.alpha = 1f;
            }

            nodeGameObject.SetActive(node.visible);
        }

        private static Paint FirstVisible(Paint[] paints) => paints?.FirstOrDefault(paint => paint != null && paint.visible);

        private static void ApplyShapeProperties(GameObject nodeGameObject, Node node)
        {
            var visibleFill = FirstVisible(node.fills);
            var visibleStroke = node.strokeWeight > 0 ? FirstVisible(node.strokes) : null;
            var figmaImage = nodeGameObject.GetComponent<FigmaImage>();
            if (visibleFill == null && visibleStroke == null)
            {
                if (figmaImage != null) figmaImage.enabled = false;
                return;
            }
            if (figmaImage == null) figmaImage = nodeGameObject.AddComponent<FigmaImage>();
            figmaImage.enabled = true;

            switch (node.type)
            {
                case NodeType.ELLIPSE:
                    figmaImage.Shape = FigmaImage.ShapeType.Ellipse;
                    if (node.arcData != null)
                    {
                        figmaImage.EllipseArcAngleRange = new Vector2(node.arcData.startingAngle, node.arcData.endingAngle);
                        figmaImage.EllipseInnerRadius = node.arcData.innerRadius;
                    }
                    break;
                case NodeType.STAR:
                    figmaImage.Shape = FigmaImage.ShapeType.Star;
                    break;
                default:
                    figmaImage.Shape = FigmaImage.ShapeType.Rectangle;
                    break;
            }

            var radii = node.rectangleCornerRadii;
            figmaImage.CornerRadius = radii != null && radii.Length == 4
                ? new Vector4(radii[0], radii[1], radii[2], radii[3])
                : new Vector4(node.cornerRadius, node.cornerRadius, node.cornerRadius, node.cornerRadius);

            SetupFill(figmaImage, visibleFill, node);
            SetupStroke(figmaImage, visibleStroke, visibleFill, node);
        }

        private static void SetupStroke(FigmaImage figmaImage, Paint stroke, Paint fill, Node node)
        {
            if (stroke == null)
            {
                figmaImage.StrokeWidth = 0;
                return;
            }
            figmaImage.StrokeWidth = node.strokeWeight;
            figmaImage.StrokeColor = FigmaDataUtils.GetUnityFillColor(stroke);
            figmaImage.StrokePosition = node.strokeAlign switch
            {
                Node.StrokeAlign.OUTSIDE => FigmaImage.StrokePositionType.Outside,
                Node.StrokeAlign.CENTER => FigmaImage.StrokePositionType.Center,
                _ => FigmaImage.StrokePositionType.Inside,
            };
            if (fill == null) figmaImage.FillColor = new Color(1f, 1f, 1f, 0f);
        }

        private static void SetupFill(FigmaImage figmaImage, Paint fill, Node node)
        {
            figmaImage.sprite = null;
            figmaImage.Fill = FigmaImage.FillStyle.Solid;
            if (fill == null)
            {
                figmaImage.FillColor = new Color(0, 0, 0, 0);
                return;
            }

            switch (fill.type)
            {
                case Paint.PaintType.IMAGE:
                    SetupImageFill(figmaImage, fill, node);
                    figmaImage.FillColor = FigmaDataUtils.GetUnityFillColor(fill);
                    break;
                case Paint.PaintType.GRADIENT_LINEAR:
                case Paint.PaintType.GRADIENT_RADIAL:
                    if (fill.gradientStops == null || fill.gradientStops.Length == 0)
                    {
                        figmaImage.FillColor = new Color(0, 0, 0, 0);
                        FigmaImportReport.Warning("Gradient without colour stops is drawn transparent", node);
                        break;
                    }
                    figmaImage.FillGradient = FigmaDataUtils.ToUnityGradient(fill);
                    figmaImage.Fill = fill.type == Paint.PaintType.GRADIENT_RADIAL
                        ? FigmaImage.FillStyle.RadialGradient
                        : FigmaImage.FillStyle.LinearGradient;
                    var handles = fill.gradientHandlePositions;
                    if (handles != null && handles.Length == 3)
                    {
                        figmaImage.GradientHandlePositions = new[]
                        {
                            FigmaDataUtils.ToUnityVector(handles[0]),
                            FigmaDataUtils.ToUnityVector(handles[1]),
                            FigmaDataUtils.ToUnityVector(handles[2]),
                        };
                    }
                    figmaImage.FillColor = new Color(1f, 1f, 1f, fill.opacity);
                    break;
                case Paint.PaintType.SOLID:
                    figmaImage.FillColor = FigmaDataUtils.GetUnityFillColor(fill);
                    break;
                default:
                    var fallback = fill.gradientStops != null && fill.gradientStops.Length > 0
                        ? FigmaDataUtils.ToUnityColor(fill.gradientStops[0].color)
                        : new Color(0.5f, 0.5f, 0.5f, 1f);
                    fallback.a *= fill.opacity;
                    figmaImage.FillColor = fallback;
                    FigmaImportReport.Warning($"Fill type {fill.type} can't be drawn natively; mark the layer to flatten for an exact match", node);
                    break;
            }
        }

        private static void SetupImageFill(FigmaImage figmaImage, Paint fill, Node node)
        {
            var spritePath = FigmaPaths.GetPathForImageFill(fill.imageRef);
            figmaImage.sprite = FigmaAssetReuse.LoadImageFillSprite(fill.imageRef);
            if (figmaImage.sprite == null) FigmaImportReport.Warning("Image is missing, so the layer shows as a plain rectangle", node, spritePath);
            switch (fill.scaleMode)
            {
                case Paint.ScaleMode.FIT:
                    figmaImage.ScaleMode = FigmaImage.ImageScaleMode.Fit;
                    break;
                case Paint.ScaleMode.TILE:
                    figmaImage.ScaleMode = FigmaImage.ImageScaleMode.Tile;
                    figmaImage.ImageScaleFactor = fill.scalingFactor;
                    break;
                case Paint.ScaleMode.STRETCH when fill.imageTransform != null:
                    figmaImage.ScaleMode = FigmaImage.ImageScaleMode.Stretch;
                    figmaImage.ImageTransform = FigmaDataUtils.ToUnityVector3Array(fill.imageTransform);
                    break;
                default:
                    figmaImage.ScaleMode = FigmaImage.ImageScaleMode.Fill;
                    break;
            }
        }

        private static void ApplyTextProperties(GameObject nodeGameObject, Node node, FigmaImportProcessData figmaImportProcessData)
        {
            var text = nodeGameObject.GetComponent<TextMeshProUGUI>();
            if (text == null) text = nodeGameObject.AddComponent<TextMeshProUGUI>();
            var style = node.style ?? new TypeStyle { fontSize = 24 };
            var fontSize = style.fontSize > 0 ? style.fontSize : 24f;

            var mapping = figmaImportProcessData.FontMap?.GetFontMapping(style.fontFamily, style.fontWeight);
            if (mapping?.FontAsset != null) text.font = mapping.FontAsset;
            else FigmaImportReport.Warning($"No font for {style.fontFamily} {style.fontWeight}; using the default font", node);

            text.richText = true;
            text.fontSize = fontSize;
            text.characterSpacing = style.letterSpacing / fontSize * 100f + FigmaToTextMeshProSpacingAdjustment;
            text.paragraphSpacing = style.paragraphSpacing / fontSize * 100f;
            text.lineSpacing = ComputeLineSpacing(text.font, style, fontSize);

            var fill = FirstVisible(node.fills);
            text.color = fill != null ? FigmaDataUtils.GetUnityFillColor(fill) : new Color(1f, 1f, 1f, 0f);

            text.horizontalAlignment = style.textAlignHorizontal switch
            {
                TypeStyle.TextAlignHorizontal.CENTER => HorizontalAlignmentOptions.Center,
                TypeStyle.TextAlignHorizontal.JUSTIFIED => HorizontalAlignmentOptions.Justified,
                TypeStyle.TextAlignHorizontal.RIGHT => HorizontalAlignmentOptions.Right,
                _ => HorizontalAlignmentOptions.Left,
            };
            text.verticalAlignment = style.textAlignVertical switch
            {
                TypeStyle.TextAlignVertical.CENTER => VerticalAlignmentOptions.Middle,
                TypeStyle.TextAlignVertical.BOTTOM => VerticalAlignmentOptions.Bottom,
                _ => VerticalAlignmentOptions.Top,
            };

            text.fontStyle = BaseFontStyle(style);
            text.text = BuildRichText(node, style, fontSize);

            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            if (string.Equals(style.textTruncation, "ENDING", System.StringComparison.OrdinalIgnoreCase) || style.textAutoResize == TypeStyle.TextAutoResize.TRUNCATE)
                text.overflowMode = TextOverflowModes.Ellipsis;
            text.maxVisibleLines = style.maxLines > 0 ? style.maxLines : 99999;

            var autoSize = node.figmaBridge != null && node.figmaBridge.textAutoSize;
            text.enableAutoSizing = autoSize;
            if (autoSize)
            {
                text.fontSizeMax = fontSize;
                text.fontSizeMin = Mathf.Max(1f, fontSize * 0.5f);
            }

            ApplyTextAutoResize(nodeGameObject, text, style);
            ApplyTextMaterial(text, node, mapping, fontSize);
        }

        private static float ComputeLineSpacing(TMP_FontAsset font, TypeStyle style, float fontSize)
        {
            if (font == null || style.lineHeightPx <= 0) return 0f;
            if (string.Equals(style.lineHeightUnit, "INTRINSIC_%", System.StringComparison.OrdinalIgnoreCase)) return 0f;
            var faceInfo = font.faceInfo;
            if (faceInfo.pointSize <= 0) return 0f;
            var naturalLineHeight = faceInfo.lineHeight / faceInfo.pointSize * fontSize;
            return (style.lineHeightPx - naturalLineHeight) / fontSize * 100f;
        }

        private static FontStyles BaseFontStyle(TypeStyle style)
        {
            var fontStyle = FontStyles.Normal;
            fontStyle |= style.textCase switch
            {
                TypeStyle.TextCase.LOWER => FontStyles.LowerCase,
                TypeStyle.TextCase.UPPER => FontStyles.UpperCase,
                TypeStyle.TextCase.SMALL_CAPS => FontStyles.SmallCaps,
                _ => FontStyles.Normal,
            };
            fontStyle |= style.textDecoration switch
            {
                TypeStyle.TextDecoration.UNDERLINE => FontStyles.Underline,
                TypeStyle.TextDecoration.STRIKETHROUGH => FontStyles.Strikethrough,
                _ => FontStyles.Normal,
            };
            if (style.italic) fontStyle |= FontStyles.Italic;
            return fontStyle;
        }

        private static string BuildRichText(Node node, TypeStyle baseStyle, float baseFontSize)
        {
            var characters = node.characters ?? string.Empty;
            var overrides = node.characterStyleOverrides;
            var table = node.styleOverrideTable;
            if (overrides == null || overrides.Length == 0 || table == null || table.Count == 0) return Escape(characters);

            var builder = new StringBuilder();
            var runStart = 0;
            var currentId = OverrideAt(overrides, 0);
            for (var index = 1; index <= characters.Length; index++)
            {
                var id = index < characters.Length ? OverrideAt(overrides, index) : -1;
                if (index < characters.Length && id == currentId) continue;
                var run = characters.Substring(runStart, index - runStart);
                builder.Append(WrapRun(run, currentId, table, baseStyle, baseFontSize));
                runStart = index;
                currentId = id;
            }
            return builder.ToString();
        }

        private static int OverrideAt(int[] overrides, int index) => index < overrides.Length ? overrides[index] : 0;

        private static string WrapRun(string run, int overrideId, System.Collections.Generic.Dictionary<string, TypeStyle> table, TypeStyle baseStyle, float baseFontSize)
        {
            var escaped = Escape(run);
            if (overrideId == 0 || !table.TryGetValue(overrideId.ToString(), out var overrideStyle) || overrideStyle == null) return escaped;
            var open = new StringBuilder();
            var close = new StringBuilder();
            void Wrap(string openTag, string closeTag)
            {
                open.Append(openTag);
                close.Insert(0, closeTag);
            }
            if (overrideStyle.fontWeight >= 600 && baseStyle.fontWeight < 600) Wrap("<b>", "</b>");
            if (overrideStyle.italic && !baseStyle.italic) Wrap("<i>", "</i>");
            if (overrideStyle.textDecoration == TypeStyle.TextDecoration.UNDERLINE) Wrap("<u>", "</u>");
            if (overrideStyle.textDecoration == TypeStyle.TextDecoration.STRIKETHROUGH) Wrap("<s>", "</s>");
            if (overrideStyle.fontSize > 0 && Mathf.Abs(overrideStyle.fontSize - baseFontSize) > 0.01f)
                Wrap($"<size={overrideStyle.fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}>", "</size>");
            var overrideFill = FirstVisible(overrideStyle.fills);
            if (overrideFill != null && overrideFill.type == Paint.PaintType.SOLID)
                Wrap($"<color=#{ColorUtility.ToHtmlStringRGBA(FigmaDataUtils.GetUnityFillColor(overrideFill))}>", "</color>");
            return open + escaped + close;
        }

        private static string Escape(string text) => text.Contains("<") ? $"<noparse>{text}</noparse>" : text;

        private static void ApplyTextAutoResize(GameObject nodeGameObject, TextMeshProUGUI text, TypeStyle style)
        {
            var fitter = nodeGameObject.GetComponent<ContentSizeFitter>();
            switch (style.textAutoResize)
            {
                case TypeStyle.TextAutoResize.HEIGHT:
                    if (fitter == null) fitter = nodeGameObject.AddComponent<ContentSizeFitter>();
                    fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                    fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                    break;
                case TypeStyle.TextAutoResize.WIDTH_AND_HEIGHT:
                    if (fitter == null) fitter = nodeGameObject.AddComponent<ContentSizeFitter>();
                    fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                    fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                    text.textWrappingMode = TextWrappingModes.NoWrap;
                    break;
                default:
                    if (fitter != null) Object.DestroyImmediate(fitter);
                    break;
            }
        }

        private static void ApplyTextMaterial(TextMeshProUGUI text, Node node, FigmaFontMapEntry mapping, float fontSize)
        {
            var shadowEffect = node.effects?.FirstOrDefault(effect => effect != null && effect.visible && effect.type == Effect.EffectType.DROP_SHADOW);
            var stroke = node.strokeWeight > 0 ? FirstVisible(node.strokes) : null;
            if (mapping?.FontAsset == null || (shadowEffect == null && stroke == null))
            {
                if (text.font != null) text.fontSharedMaterial = text.font.material;
                return;
            }
            var shadowColor = shadowEffect != null && shadowEffect.color != null ? FigmaDataUtils.ToUnityColor(shadowEffect.color) : Color.black;
            var shadowDistance = shadowEffect != null && shadowEffect.offset != null ? new Vector2(shadowEffect.offset.x, -shadowEffect.offset.y) : Vector2.zero;
            var outlineColor = stroke != null ? FigmaDataUtils.GetUnityFillColor(stroke) : Color.white;
            var outlineWidth = stroke != null ? Mathf.Clamp(4.0f * node.strokeWeight / fontSize, 0f, 0.5f) : 0f;
            text.fontMaterial = FontManager.GetEffectMaterialPreset(mapping, shadowEffect != null, shadowColor, shadowDistance, stroke != null, outlineColor, outlineWidth);
        }

        public static void CreateUnityComponentsForNode(GameObject nodeGameObject, Node node, FigmaImportProcessData figmaImportProcessData)
        {
            if (node.type == NodeType.TEXT && nodeGameObject.GetComponent<TextMeshProUGUI>() == null)
                nodeGameObject.AddComponent<TextMeshProUGUI>();
        }

        public static bool NodeIsSubstitution(Node node, FigmaImportProcessData figmaImportProcessData)
        {
            switch (node.type)
            {
                case NodeType.INSTANCE when
                    figmaImportProcessData.ServerRenderNodes.Find(serverRenderNode => serverRenderNode.SourceNode.id == node.componentId && serverRenderNode.RenderType == ServerRenderType.Substitution) != null:
                case NodeType.COMPONENT when
                    figmaImportProcessData.ServerRenderNodes.Find(serverRenderNode => serverRenderNode.SourceNode.id == node.id && serverRenderNode.RenderType == ServerRenderType.Substitution) != null:
                    return true;
                default:
                    return false;
            }
        }
    }
}
