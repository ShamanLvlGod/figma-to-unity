using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityFigmaBridge.Editor.FigmaApi;

namespace UnityFigmaBridge.Editor.Reporting
{
    public enum FigmaReportSeverity
    {
        Info,
        Warning,
        Error,
    }

    [Serializable]
    public class FigmaReportEntry
    {
        public FigmaReportSeverity Severity;
        public string Message;
        public string NodeId;
        public string NodeName;
        public string AssetPath;
    }

    [Serializable]
    public class FigmaImportReport
    {
        public string StartedAt;
        public string Source;
        public List<string> Screens = new();
        public List<FigmaReportEntry> Entries = new();

        public static FigmaImportReport Current { get; private set; }
        public static FigmaImportReport Last { get; private set; }
        public static event Action<FigmaImportReport> Completed;

        public bool HasErrors => Entries.Any(entry => entry.Severity == FigmaReportSeverity.Error);

        public IEnumerable<FigmaReportEntry> OfSeverity(FigmaReportSeverity severity) =>
            Entries.Where(entry => entry.Severity == severity);

        public static FigmaImportReport Begin(string source)
        {
            Current = new FigmaImportReport { StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Source = source };
            return Current;
        }

        public static FigmaImportReport End()
        {
            var finished = Current;
            Current = null;
            if (finished == null) return null;
            Last = finished;
            Completed?.Invoke(finished);
            return finished;
        }

        public static void Error(string message, Node node = null, string assetPath = null) =>
            Add(FigmaReportSeverity.Error, message, node, assetPath);

        public static void Warning(string message, Node node = null, string assetPath = null) =>
            Add(FigmaReportSeverity.Warning, message, node, assetPath);

        public static void Info(string message, Node node = null, string assetPath = null) =>
            Add(FigmaReportSeverity.Info, message, node, assetPath);

        private static void Add(FigmaReportSeverity severity, string message, Node node, string assetPath)
        {
            var entry = new FigmaReportEntry
            {
                Severity = severity,
                Message = message,
                NodeId = node?.id,
                NodeName = node?.name,
                AssetPath = assetPath,
            };
            var logText = node != null ? $"Figma Bridge: {message} (layer '{node.name}', {node.id})" : $"Figma Bridge: {message}";
            switch (severity)
            {
                case FigmaReportSeverity.Error:
                    Debug.LogError(logText);
                    break;
                case FigmaReportSeverity.Warning:
                    Debug.LogWarning(logText);
                    break;
                default:
                    Debug.Log(logText);
                    break;
            }
            Current?.Entries.Add(entry);
        }
    }
}
