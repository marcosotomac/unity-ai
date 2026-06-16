using System;
using System.Collections.Generic;
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

        public static UnityAiAuditEvent[] ReadAll(out int malformedLineCount)
        {
            malformedLineCount = 0;
            if (!File.Exists(AuditLogPath))
            {
                return Array.Empty<UnityAiAuditEvent>();
            }

            var events = new List<UnityAiAuditEvent>();
            foreach (var line in File.ReadLines(AuditLogPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var auditEvent = JsonUtility.FromJson<UnityAiAuditEvent>(line);
                    if (auditEvent == null || string.IsNullOrWhiteSpace(auditEvent.capability))
                    {
                        malformedLineCount++;
                        continue;
                    }

                    events.Add(auditEvent);
                }
                catch
                {
                    malformedLineCount++;
                }
            }

            return events.ToArray();
        }

        private static string GetProjectRoot()
        {
            var parent = Directory.GetParent(Application.dataPath);
            return parent?.FullName ?? Application.dataPath;
        }
    }
}
