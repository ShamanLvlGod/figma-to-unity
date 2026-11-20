using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityFigmaBridge.Editor.Reporting
{
    public class FigmaImportReportWindow : EditorWindow
    {
        private FigmaImportReport m_Report;
        private Vector2 m_Scroll;
        private bool m_ShowInfo = true;
        private bool m_ShowWarnings = true;
        private bool m_ShowErrors = true;

        [InitializeOnLoadMethod]
        private static void RegisterAutoOpen()
        {
            FigmaImportReport.Completed -= OnImportCompleted;
            FigmaImportReport.Completed += OnImportCompleted;
        }

        private static void OnImportCompleted(FigmaImportReport report)
        {
            if (report.HasErrors) EditorApplication.delayCall += () => ShowReport(report);
            else if (HasOpenInstances<FigmaImportReportWindow>()) EditorApplication.delayCall += () => ShowReport(report);
        }

        [MenuItem("Tools/Figma Bridge/Import Report", false, 40)]
        private static void OpenFromMenu() => ShowReport(FigmaImportReport.Last);

        public static void ShowReport(FigmaImportReport report)
        {
            var window = GetWindow<FigmaImportReportWindow>("Figma Import Report");
            window.m_Report = report;
            window.Repaint();
        }

        private void OnGUI()
        {
            if (m_Report == null)
            {
                EditorGUILayout.HelpBox("No import has run in this Editor session yet.", MessageType.Info);
                return;
            }

            var errors = m_Report.OfSeverity(FigmaReportSeverity.Error).Count();
            var warnings = m_Report.OfSeverity(FigmaReportSeverity.Warning).Count();
            var infos = m_Report.OfSeverity(FigmaReportSeverity.Info).Count();
            EditorGUILayout.LabelField($"{m_Report.Source}, {m_Report.StartedAt}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(m_Report.Screens.Count > 0 ? $"Screens: {string.Join(", ", m_Report.Screens)}" : "No screens built");
            EditorGUILayout.BeginHorizontal();
            m_ShowErrors = GUILayout.Toggle(m_ShowErrors, $"Errors ({errors})", EditorStyles.toolbarButton);
            m_ShowWarnings = GUILayout.Toggle(m_ShowWarnings, $"Warnings ({warnings})", EditorStyles.toolbarButton);
            m_ShowInfo = GUILayout.Toggle(m_ShowInfo, $"Info ({infos})", EditorStyles.toolbarButton);
            EditorGUILayout.EndHorizontal();

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            foreach (var entry in m_Report.Entries)
            {
                if (entry.Severity == FigmaReportSeverity.Error && !m_ShowErrors) continue;
                if (entry.Severity == FigmaReportSeverity.Warning && !m_ShowWarnings) continue;
                if (entry.Severity == FigmaReportSeverity.Info && !m_ShowInfo) continue;
                var messageType = entry.Severity switch
                {
                    FigmaReportSeverity.Error => MessageType.Error,
                    FigmaReportSeverity.Warning => MessageType.Warning,
                    _ => MessageType.Info,
                };
                var text = string.IsNullOrEmpty(entry.NodeName) ? entry.Message : $"{entry.NodeName}: {entry.Message}";
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox(text, messageType);
                if (!string.IsNullOrEmpty(entry.AssetPath) && GUILayout.Button("Show", GUILayout.Width(50), GUILayout.Height(38)))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(entry.AssetPath);
                    if (asset != null)
                    {
                        EditorGUIUtility.PingObject(asset);
                        Selection.activeObject = asset;
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
