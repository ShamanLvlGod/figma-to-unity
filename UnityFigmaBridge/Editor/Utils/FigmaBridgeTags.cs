using System.Text.RegularExpressions;
using UnityFigmaBridge.Editor.FigmaApi;

namespace UnityFigmaBridge.Editor.Utils
{
    public static class FigmaBridgeTags
    {
        public const string RoleAuto = "auto";
        public const string RoleButton = "button";
        public const string RoleToggle = "toggle";
        public const string RoleToggleGroup = "toggleGroup";
        public const string RoleSlider = "slider";
        public const string RoleInputField = "inputField";
        public const string RoleScrollView = "scrollView";
        public const string RoleImage = "image";
        public const string RoleIgnore = "ignore";

        private static readonly (string tag, string role)[] NameTags =
        {
            ("[button]", RoleButton),
            ("[togglegroup]", RoleToggleGroup),
            ("[toggle]", RoleToggle),
            ("[slider]", RoleSlider),
            ("[input]", RoleInputField),
            ("[scroll]", RoleScrollView),
            ("[image]", RoleImage),
            ("[flatten]", RoleImage),
            ("[ignore]", RoleIgnore),
        };

        private static readonly Regex TagPattern = new(@"\[[a-z]+\]", RegexOptions.IgnoreCase);
        private static readonly Regex RenderWordPattern = new(@"(^|[^a-z])render([^a-z]|$)", RegexOptions.IgnoreCase);

        public static string GetRole(Node node)
        {
            if (node == null) return RoleAuto;
            var settingsRole = node.figmaBridge?.role;
            if (!string.IsNullOrEmpty(settingsRole) && settingsRole != RoleAuto) return settingsRole;
            var lowerName = node.name?.ToLowerInvariant() ?? string.Empty;
            foreach (var (tag, role) in NameTags)
            {
                if (lowerName.Contains(tag)) return role;
            }
            return RoleAuto;
        }

        public static bool IsIgnored(Node node) => GetRole(node) == RoleIgnore;

        public static bool HasNineSlice(Node node)
        {
            var insets = node?.figmaBridge?.nineSlice;
            return insets != null && (insets.left > 0 || insets.top > 0 || insets.right > 0 || insets.bottom > 0);
        }

        public static bool ShouldFlatten(Node node)
        {
            if (node == null) return false;
            if (node.figmaBridge != null && node.figmaBridge.flatten) return true;
            if (GetRole(node) == RoleImage) return true;
            if (HasNineSlice(node)) return true;
            return RenderWordPattern.IsMatch(node.name ?? string.Empty);
        }

        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Unnamed";
            var cleaned = TagPattern.Replace(name, string.Empty).Trim();
            return cleaned.Length == 0 ? "Unnamed" : cleaned;
        }

        public static string GetBindingName(Node node)
        {
            var bindingName = node?.figmaBridge?.bindingName;
            return string.IsNullOrWhiteSpace(bindingName) ? CleanName(node?.name) : bindingName.Trim();
        }
    }
}
