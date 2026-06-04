using System;
using System.IO;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    public static class AuditLogStore
    {
        private const string AuditDirectory = "UnityAIArtifacts/Audit";
        private const string AuditFileName = "events.jsonl";

        public static string AuditLogPath => Path.Combine(GetProjectRoot(), AuditDirectory, AuditFileName);
        public static string AuditLogRelativePath => Path.Combine(AuditDirectory, AuditFileName);

        public static void Append(UnityAiAuditEvent[] events)
        {
            if (events == null || events.Length == 0)
            {
                return;
            }

            var path = AuditLogPath;
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(path, append: true);

            foreach (var auditEvent in events)
            {
                writer.WriteLine(JsonUtility.ToJson(auditEvent, false));
            }
        }

        private static string GetProjectRoot()
        {
            var parent = Directory.GetParent(Application.dataPath);
            return parent?.FullName ?? Application.dataPath;
        }
    }
}
