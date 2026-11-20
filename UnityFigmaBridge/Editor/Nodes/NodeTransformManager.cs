using UnityEngine;
using UnityEngine.UI;
using UnityFigmaBridge.Editor.FigmaApi;

namespace UnityFigmaBridge.Editor.Nodes
{
    public static class NodeTransformManager
    {
        public static bool UseRenderBoundsForSubstitutions;

        public static void ApplyFigmaTransform(RectTransform targetRectTransform, Node figmaNode, Node figmaParentNode, bool centerPivot)
        {
            targetRectTransform.anchorMin = targetRectTransform.anchorMax = new Vector2(0, 1);
            targetRectTransform.pivot = new Vector2(0, 1);

            var mirrorX = 1f;
            var rotation = 0f;
            if (figmaNode.relativeTransform != null)
            {
                var m00 = figmaNode.relativeTransform[0, 0];
                var m01 = figmaNode.relativeTransform[0, 1];
                var m10 = figmaNode.relativeTransform[1, 0];
                var m11 = figmaNode.relativeTransform[1, 1];
                targetRectTransform.anchoredPosition = new Vector2(figmaNode.relativeTransform[0, 2], -figmaNode.relativeTransform[1, 2]);
                mirrorX = m00 * m11 - m01 * m10 < 0 ? -1f : 1f;
                rotation = Mathf.Rad2Deg * Mathf.Atan2(-m10 * mirrorX, m00 * mirrorX);
            }
            targetRectTransform.localRotation = Quaternion.Euler(0, 0, rotation);
            targetRectTransform.localScale = new Vector3(mirrorX, 1f, 1f);

            var size = figmaNode.size != null
                ? new Vector2(figmaNode.size.x, figmaNode.size.y)
                : figmaNode.absoluteBoundingBox != null
                    ? new Vector2(figmaNode.absoluteBoundingBox.width, figmaNode.absoluteBoundingBox.height)
                    : Vector2.zero;
            targetRectTransform.sizeDelta = size;
            ConfigureLayoutElement(targetRectTransform.gameObject, figmaNode, figmaParentNode, size);

            var useFirstChildForConstraints = figmaNode.type == NodeType.GROUP && figmaNode.children != null && figmaNode.children.Length > 0;
            var constraintsSourceNode = useFirstChildForConstraints ? figmaNode.children[0] : figmaNode;
            if (constraintsSourceNode.constraints != null && figmaParentNode != null)
                ApplyFigmaConstraints(targetRectTransform, constraintsSourceNode, figmaParentNode);

            if (figmaNode.type == NodeType.TEXT) centerPivot = false;
            if (centerPivot) SetPivot(targetRectTransform, new Vector2(0.5f, 0.5f));
        }

        private static bool ParentHasAutoLayout(Node figmaParentNode) =>
            figmaParentNode != null && figmaParentNode.layoutMode != Node.LayoutMode.NONE;

        private static void ConfigureLayoutElement(GameObject gameObject, Node figmaNode, Node figmaParentNode, Vector2 size)
        {
            var layoutElement = gameObject.GetComponent<LayoutElement>();
            if (layoutElement == null) layoutElement = gameObject.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = false;
            layoutElement.preferredWidth = size.x;
            layoutElement.preferredHeight = size.y;
            layoutElement.minWidth = figmaNode.minWidth ?? -1f;
            layoutElement.minHeight = figmaNode.minHeight ?? -1f;
            layoutElement.flexibleWidth = -1f;
            layoutElement.flexibleHeight = -1f;

            var hugsHorizontally = figmaNode.layoutSizingHorizontal == "HUG";
            var hugsVertically = figmaNode.layoutSizingVertical == "HUG";
            if (figmaNode.type == NodeType.TEXT && figmaNode.style != null)
            {
                hugsHorizontally |= figmaNode.style.textAutoResize == TypeStyle.TextAutoResize.WIDTH_AND_HEIGHT;
                hugsVertically |= figmaNode.style.textAutoResize is TypeStyle.TextAutoResize.WIDTH_AND_HEIGHT or TypeStyle.TextAutoResize.HEIGHT;
            }
            if (figmaNode.layoutMode != Node.LayoutMode.NONE || figmaNode.type == NodeType.TEXT)
            {
                if (hugsHorizontally) layoutElement.preferredWidth = -1f;
                if (hugsVertically) layoutElement.preferredHeight = -1f;
            }

            if (!ParentHasAutoLayout(figmaParentNode)) return;
            if (figmaNode.layoutPositioning == "ABSOLUTE")
            {
                layoutElement.ignoreLayout = true;
                return;
            }
            var parentIsHorizontal = figmaParentNode.layoutMode == Node.LayoutMode.HORIZONTAL;
            if (figmaNode.layoutSizingHorizontal == "FILL") layoutElement.flexibleWidth = 1f;
            if (figmaNode.layoutSizingVertical == "FILL") layoutElement.flexibleHeight = 1f;
            if (figmaNode.layoutGrow > 0)
            {
                if (parentIsHorizontal) layoutElement.flexibleWidth = figmaNode.layoutGrow;
                else layoutElement.flexibleHeight = figmaNode.layoutGrow;
            }
            if (figmaNode.layoutAlign == Node.LayoutAlign.STRETCH)
            {
                if (parentIsHorizontal) layoutElement.flexibleHeight = 1f;
                else layoutElement.flexibleWidth = 1f;
            }
        }

        private static void ApplyFigmaConstraints(RectTransform targetRectTransform, Node figmaNode, Node figmaParentNode)
        {
            (targetRectTransform.anchorMin, targetRectTransform.anchorMax) = AnchorPositionsForFigmaConstraints(figmaNode.constraints);
            var parentNodeSize = figmaParentNode.size ?? new Vector { x = 0, y = 0 };

            var anchoredPosition = targetRectTransform.anchoredPosition;
            anchoredPosition.x += figmaNode.constraints.horizontal switch
            {
                LayoutConstraint.HorizontalLayoutConstraint.CENTER => -parentNodeSize.x * 0.5f,
                LayoutConstraint.HorizontalLayoutConstraint.RIGHT => -parentNodeSize.x,
                _ => 0
            };
            anchoredPosition.y += figmaNode.constraints.vertical switch
            {
                LayoutConstraint.VerticalLayoutConstraint.CENTER => parentNodeSize.y * 0.5f,
                LayoutConstraint.VerticalLayoutConstraint.BOTTOM => parentNodeSize.y,
                _ => 0
            };
            targetRectTransform.anchoredPosition = anchoredPosition;

            if (figmaNode.constraints.horizontal == LayoutConstraint.HorizontalLayoutConstraint.LEFT_RIGHT)
            {
                var sizeDelta = targetRectTransform.sizeDelta;
                targetRectTransform.sizeDelta = new Vector2(sizeDelta.x - parentNodeSize.x, sizeDelta.y);
            }
            if (figmaNode.constraints.vertical == LayoutConstraint.VerticalLayoutConstraint.TOP_BOTTOM)
            {
                var sizeDelta = targetRectTransform.sizeDelta;
                targetRectTransform.sizeDelta = new Vector2(sizeDelta.x, sizeDelta.y - parentNodeSize.y);
            }

            ApplyScaleConstraint(targetRectTransform, figmaNode, parentNodeSize);
        }

        private static void ApplyScaleConstraint(RectTransform targetRectTransform, Node figmaNode, Vector parentNodeSize)
        {
            var scaleHorizontal = figmaNode.constraints.horizontal == LayoutConstraint.HorizontalLayoutConstraint.SCALE && parentNodeSize.x > 0;
            var scaleVertical = figmaNode.constraints.vertical == LayoutConstraint.VerticalLayoutConstraint.SCALE && parentNodeSize.y > 0;
            if (!scaleHorizontal && !scaleVertical) return;

            var left = figmaNode.relativeTransform != null ? figmaNode.relativeTransform[0, 2] : 0f;
            var top = figmaNode.relativeTransform != null ? figmaNode.relativeTransform[1, 2] : 0f;
            var width = figmaNode.size?.x ?? 0f;
            var height = figmaNode.size?.y ?? 0f;

            var anchorMin = targetRectTransform.anchorMin;
            var anchorMax = targetRectTransform.anchorMax;
            var offsetMin = targetRectTransform.offsetMin;
            var offsetMax = targetRectTransform.offsetMax;
            if (scaleHorizontal)
            {
                anchorMin.x = left / parentNodeSize.x;
                anchorMax.x = (left + width) / parentNodeSize.x;
                offsetMin.x = 0;
                offsetMax.x = 0;
            }
            if (scaleVertical)
            {
                anchorMax.y = 1f - top / parentNodeSize.y;
                anchorMin.y = 1f - (top + height) / parentNodeSize.y;
                offsetMin.y = 0;
                offsetMax.y = 0;
            }
            targetRectTransform.anchorMin = anchorMin;
            targetRectTransform.anchorMax = anchorMax;
            targetRectTransform.offsetMin = offsetMin;
            targetRectTransform.offsetMax = offsetMax;
        }

        public static void ApplyAbsoluteBoundsFigmaTransform(RectTransform targetRectTransform, Node figmaNode, Node figmaParentNode, bool centerPivot)
        {
            targetRectTransform.anchorMin = targetRectTransform.anchorMax = new Vector2(0, 1);
            targetRectTransform.pivot = new Vector2(0, 1);
            targetRectTransform.localRotation = Quaternion.identity;
            targetRectTransform.localScale = Vector3.one;

            var bounds = UseRenderBoundsForSubstitutions && figmaNode.absoluteRenderBounds != null && figmaNode.absoluteRenderBounds.width > 0
                ? figmaNode.absoluteRenderBounds
                : figmaNode.absoluteBoundingBox;
            if (bounds == null) return;

            targetRectTransform.sizeDelta = new Vector2(bounds.width, bounds.height);
            ConfigureLayoutElement(targetRectTransform.gameObject, figmaNode, figmaParentNode, new Vector2(bounds.width, bounds.height));

            var parentPosition = figmaParentNode?.absoluteBoundingBox != null
                ? new Vector2(figmaParentNode.absoluteBoundingBox.x, figmaParentNode.absoluteBoundingBox.y)
                : Vector2.zero;
            targetRectTransform.anchoredPosition = new Vector2(bounds.x - parentPosition.x, -(bounds.y - parentPosition.y));

            if (figmaNode.constraints != null && figmaParentNode != null) ApplyFigmaConstraints(targetRectTransform, figmaNode, figmaParentNode);
            if (centerPivot) SetPivot(targetRectTransform, new Vector2(0.5f, 0.5f));
        }

        private static void SetPivot(RectTransform target, Vector2 pivot)
        {
            if (!target) return;
            var offset = pivot - target.pivot;
            offset.Scale(target.rect.size);
            var worldPos = target.position + target.TransformVector(offset);
            target.pivot = pivot;
            target.position = worldPos;
        }

        private static (Vector2, Vector2) AnchorPositionsForFigmaConstraints(LayoutConstraint constraint)
        {
            var anchorsX = constraint.horizontal switch
            {
                LayoutConstraint.HorizontalLayoutConstraint.LEFT => new Vector2(0, 0),
                LayoutConstraint.HorizontalLayoutConstraint.RIGHT => new Vector2(1, 1),
                LayoutConstraint.HorizontalLayoutConstraint.CENTER => new Vector2(0.5f, 0.5f),
                LayoutConstraint.HorizontalLayoutConstraint.LEFT_RIGHT => new Vector2(0, 1),
                LayoutConstraint.HorizontalLayoutConstraint.SCALE => new Vector2(0, 0),
                _ => new Vector2(0, 0)
            };
            var anchorsY = constraint.vertical switch
            {
                LayoutConstraint.VerticalLayoutConstraint.TOP => new Vector2(1, 1),
                LayoutConstraint.VerticalLayoutConstraint.BOTTOM => new Vector2(0, 0),
                LayoutConstraint.VerticalLayoutConstraint.CENTER => new Vector2(0.5f, 0.5f),
                LayoutConstraint.VerticalLayoutConstraint.TOP_BOTTOM => new Vector2(0, 1),
                LayoutConstraint.VerticalLayoutConstraint.SCALE => new Vector2(1, 1),
                _ => new Vector2(1, 1)
            };
            return (new Vector2(anchorsX.x, anchorsY.x), new Vector2(anchorsX.y, anchorsY.y));
        }

        public static Rect GetRelativeBoundsForAllChildNodes(Node figmaNode)
        {
            if (figmaNode.children == null || figmaNode.absoluteBoundingBox == null) return new Rect();
            var mergedRect = new Rect();
            var first = true;
            foreach (var childNode in figmaNode.children)
            {
                if (childNode.absoluteBoundingBox == null) continue;
                var relativePosition = new Vector2(childNode.absoluteBoundingBox.x - figmaNode.absoluteBoundingBox.x,
                    childNode.absoluteBoundingBox.y - figmaNode.absoluteBoundingBox.y);
                var size = new Vector2(childNode.absoluteBoundingBox.width, childNode.absoluteBoundingBox.height);
                if (first)
                {
                    mergedRect = new Rect(relativePosition, size);
                    first = false;
                    continue;
                }
                mergedRect.xMin = Mathf.Min(mergedRect.xMin, relativePosition.x);
                mergedRect.xMax = Mathf.Max(mergedRect.xMax, relativePosition.x + size.x);
                mergedRect.yMin = Mathf.Min(mergedRect.yMin, relativePosition.y);
                mergedRect.yMax = Mathf.Max(mergedRect.yMax, relativePosition.y + size.y);
            }
            return mergedRect;
        }
    }
}
