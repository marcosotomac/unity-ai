using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class ConsoleLogSummary
    {
        public int errorCount;
        public int warningCount;
        public int logCount;
        public List<ConsoleLogEntry> recentEntries = new();
    }

    [Serializable]
    public sealed class ConsoleLogEntry
    {
        public string type;
        public string condition;
        public string stackTrace;
    }

    [InitializeOnLoad]
    public static class ConsoleLogBridge
    {
        private const int MaxEntries = 100;
        private static readonly List<ConsoleLogEntry> RecentEntries = new();

        static ConsoleLogBridge()
        {
            Application.logMessageReceived -= OnLogMessageReceived;
            Application.logMessageReceived += OnLogMessageReceived;
        }

        public static ConsoleLogSummary GetSummary()
        {
            var summary = new ConsoleLogSummary();
            TryReadEditorConsoleCounts(summary);
            summary.recentEntries = new List<ConsoleLogEntry>(RecentEntries);
            return summary;
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            RecentEntries.Add(new ConsoleLogEntry
            {
                type = type.ToString(),
                condition = condition,
                stackTrace = stackTrace
            });

            if (RecentEntries.Count > MaxEntries)
            {
                RecentEntries.RemoveAt(0);
            }
        }

        private static void TryReadEditorConsoleCounts(ConsoleLogSummary summary)
        {
            var logEntriesType = Type.GetType("UnityEditor.LogEntries,UnityEditor.dll");
            var getCountsMethod = logEntriesType?.GetMethod("GetCountsByType", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (getCountsMethod == null)
            {
                return;
            }

            var parameters = new object[] { 0, 0, 0 };
            getCountsMethod.Invoke(null, parameters);

            summary.errorCount = (int)parameters[0];
            summary.warningCount = (int)parameters[1];
            summary.logCount = (int)parameters[2];
        }
    }
}
