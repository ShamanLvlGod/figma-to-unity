using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityFigmaBridge.Editor.FigmaApi;
using UnityFigmaBridge.Editor.Reporting;
using UnityFigmaBridge.Editor.Utils;

namespace UnityFigmaBridge.Editor.Nodes
{
    public static class FigmaLayoutManager
    {
        private const string ScrollContentSuffix = "_ScrollContent";

        private static bool IsContainer(Node node) =>
            node.type is NodeType.FRAME or NodeType.COMPONENT or NodeType.INSTANCE or NodeType.COMPONENT_SET;

        public static void ApplyLayoutPropertiesForNode(GameObject nodeGameObject, Node node,
            FigmaImportProcessData figmaImportProcessData, out GameObject scrollContentGameObject)
        {
            var targetLayoutObject = nodeGameObject;
            scrollContentGameObject = null;

            var wantsScroll = IsContainer(node) &&
                              (node.overflowDirection != Node.OverflowDirection.NONE ||
                               FigmaBridgeTags.GetRole(node) == FigmaBridgeTags.RoleScrollView);
            if (IsContainer(node) && (node.clipsContent || wantsScroll))
                UnityUiUtils.GetOrAddComponent<RectMask2D>(nodeGameObject);
            else if (nodeGameObject.TryGetComponent<RectMask2D>(out var staleMask))
                Object.DestroyImmediate(staleMask);

            if (wantsScroll)
            {
                scrollContentGameObject = GetOrCreateScrollContent(nodeGameObject, node);
                var scrollRect = UnityUiUtils.GetOrAddComponent<ScrollRect>(nodeGameObject);
                scrollRect.content = scrollContentGameObject.transform as RectTransform;
                scrollRect.viewport = nodeGameObject.transform as RectTransform;
                var horizontal = node.overflowDirection is Node.OverflowDirection.HORIZONTAL_SCROLLING or Node.OverflowDirection.HORIZONTAL_AND_VERTICAL_SCROLLING;
                var vertical = node.overflowDirection is Node.OverflowDirection.VERTICAL_SCROLLING or Node.OverflowDirection.HORIZONTAL_AND_VERTICAL_SCROLLING;
                if (!horizontal && !vertical) vertical = true;
                scrollRect.horizontal = horizontal;
                scrollRect.vertical = vertical;
                scrollRect.movementType = ScrollRect.MovementType.Clamped;
                if (node.layoutMode != Node.LayoutMode.NONE)
                {
                    var contentSizeFitter = UnityUiUtils.GetOrAddComponent<ContentSizeFitter>(scrollContentGameObject);
                    contentSizeFitter.horizontalFit = horizontal ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
                    contentSizeFitter.verticalFit = vertical ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
                }
                targetLayoutObject = scrollContentGameObject;
            }

            if (node.layoutMode == Node.LayoutMode.NONE || !figmaImportProcessData.Settings.EnableAutoLayout)
            {
                RemoveLayoutGroup(targetLayoutObject);
                return;
            }

            if (node.layoutWrap == "WRAP")
                FigmaImportReport.Warning("Auto layout wrap isn't supported yet; items are laid out in one line", node);

            HorizontalOrVerticalLayoutGroup layoutGroup = node.layoutMode == Node.LayoutMode.VERTICAL
                ? GetOrSwapLayoutGroup<VerticalLayoutGroup>(targetLayoutObject)
                : GetOrSwapLayoutGroup<HorizontalLayoutGroup>(targetLayoutObject);

            layoutGroup.childControlWidth = true;
            layoutGroup.childControlHeight = true;
            layoutGroup.childForceExpandWidth = false;
            layoutGroup.childForceExpandHeight = false;
            layoutGroup.childScaleWidth = false;
            layoutGroup.childScaleHeight = false;
            layoutGroup.reverseArrangement = false;
            layoutGroup.padding = new RectOffset(Mathf.RoundToInt(node.paddingLeft), Mathf.RoundToInt(node.paddingRight),
                Mathf.RoundToInt(node.paddingTop), Mathf.RoundToInt(node.paddingBottom));
            layoutGroup.spacing = ComputeSpacing(node);
            layoutGroup.childAlignment = ComputeAlignment(node);
        }

        private static GameObject GetOrCreateScrollContent(GameObject nodeGameObject, Node node)
        {
            var contentName = $"{FigmaBridgeTags.CleanName(node.name)}{ScrollContentSuffix}";
            var existing = nodeGameObject.transform.Cast<Transform>().FirstOrDefault(child => child.name.EndsWith(ScrollContentSuffix));
            GameObject content;
            if (existing != null)
            {
                content = existing.gameObject;
                content.name = contentName;
            }
            else
            {
                content = new GameObject(contentName, typeof(RectTransform));
                content.AddComponent<UnityFigmaBridge.Runtime.UI.FigmaNodeObject>().NodeId = $"{node.id}#scroll-content";
                content.transform.SetParent(nodeGameObject.transform, false);
            }
            var contentTransform = (RectTransform)content.transform;
            contentTransform.pivot = new Vector2(0, 1);
            contentTransform.anchorMin = contentTransform.anchorMax = new Vector2(0, 1);
            contentTransform.anchoredPosition = Vector2.zero;
            if (node.size != null) contentTransform.sizeDelta = new Vector2(node.size.x, node.size.y);
            return content;
        }

        private static T GetOrSwapLayoutGroup<T>(GameObject target) where T : HorizontalOrVerticalLayoutGroup
        {
            var existing = target.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (existing is T matching) return matching;
            if (existing != null) Object.DestroyImmediate(existing);
            return target.AddComponent<T>();
        }

        private static void RemoveLayoutGroup(GameObject target)
        {
            var existing = target.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (existing != null) Object.DestroyImmediate(existing);
        }

        private static float ComputeSpacing(Node node)
        {
            if (node.primaryAxisAlignItems is not (Node.PrimaryAxisAlignItems.SPACE_BETWEEN or Node.PrimaryAxisAlignItems.SPACE_EVENLY or Node.PrimaryAxisAlignItems.SPACE_AROUND))
                return node.itemSpacing;
            var flowChildren = node.children?.Where(child => child.visible && child.layoutPositioning != "ABSOLUTE" && child.size != null).ToList();
            if (flowChildren == null || flowChildren.Count < 2 || node.size == null) return node.itemSpacing;
            var horizontal = node.layoutMode == Node.LayoutMode.HORIZONTAL;
            var available = horizontal
                ? node.size.x - node.paddingLeft - node.paddingRight
                : node.size.y - node.paddingTop - node.paddingBottom;
            var used = flowChildren.Sum(child => horizontal ? child.size.x : child.size.y);
            return Mathf.Max(0f, (available - used) / (flowChildren.Count - 1));
        }

        private static TextAnchor ComputeAlignment(Node node)
        {
            int primary = node.primaryAxisAlignItems switch
            {
                Node.PrimaryAxisAlignItems.CENTER => 1,
                Node.PrimaryAxisAlignItems.MAX => 2,
                _ => 0,
            };
            int counter = node.counterAxisAlignItems switch
            {
                Node.CounterAxisAlignItems.CENTER => 1,
                Node.CounterAxisAlignItems.MAX => 2,
                _ => 0,
            };
            var column = node.layoutMode == Node.LayoutMode.HORIZONTAL ? primary : counter;
            var row = node.layoutMode == Node.LayoutMode.HORIZONTAL ? counter : primary;
            return (TextAnchor)(row * 3 + column);
        }
    }
}
