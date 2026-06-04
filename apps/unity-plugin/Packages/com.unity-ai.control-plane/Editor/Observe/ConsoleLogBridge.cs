using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
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

    [Serializable]
    public sealed class ConsoleDiagnosticReport
    {
        public string timestampUtc;
        public int errorCount;
        public int warningCount;
        public int logCount;
        public int totalEntries;
        public int diagnosticCount;
        public bool hasErrors;
        public List<ConsoleDiagnosticEntry> diagnostics = new();
    }

    [Serializable]
    public sealed class ConsoleDiagnosticEntry
    {
        public string category;
        public string severity;
        public string message;
        public string file;
        public int line;
        public string stackHint;
        public string functionHint;
        public string likelyRootCause;
        public string suggestedNextSafeAction;
    }

    internal sealed class ConsoleEntrySnapshot
    {
        public string type;
        public string message;
        public string stackTrace;
        public string file;
        public int line;
        public int mode;
    }

    [InitializeOnLoad]
    public static class ConsoleLogBridge
    {
        private const int MaxEntries = 100;
        private static readonly int ErrorModeMask = ResolveLogMessageFlags("Error", "Fatal", "Assert", "AssetImportError", "ScriptingError", "ScriptingException", "ScriptCompileError", "ScriptingAssertion", "GraphError", "VisualScriptingError");
        private static readonly int WarningModeMask = ResolveLogMessageFlags("Warning", "AssetImportWarning", "ScriptingWarning", "ScriptCompileWarning");
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

        public static ConsoleDiagnosticReport Diagnose()
        {
            var summary = GetSummary();
            var entries = TryReadEditorConsoleEntries();

            foreach (var recentEntry in RecentEntries)
            {
                var recentSnapshot = new ConsoleEntrySnapshot
                {
                    type = recentEntry.type,
                    message = recentEntry.condition,
                    stackTrace = recentEntry.stackTrace
                };

                if (!ContainsEquivalentEntry(entries, recentSnapshot))
                {
                    entries.Add(recentSnapshot);
                }
            }

            var report = new ConsoleDiagnosticReport
            {
                timestampUtc = DateTime.UtcNow.ToString("o"),
                errorCount = summary.errorCount,
                warningCount = summary.warningCount,
                logCount = summary.logCount,
                totalEntries = entries.Count
            };

            foreach (var entry in entries)
            {
                var diagnostic = BuildDiagnostic(entry);
                report.diagnostics.Add(diagnostic);

                if (diagnostic.severity == "error")
                {
                    report.hasErrors = true;
                }
            }

            report.diagnosticCount = report.diagnostics.Count;
            return report;
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

        private static List<ConsoleEntrySnapshot> TryReadEditorConsoleEntries()
        {
            var results = new List<ConsoleEntrySnapshot>();
            var logEntriesType = Type.GetType("UnityEditor.LogEntries,UnityEditor.dll");
            var logEntryType = Type.GetType("UnityEditor.LogEntry,UnityEditor.dll");
            var startMethod = logEntriesType?.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var endMethod = logEntriesType?.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var getCountMethod = logEntriesType?.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var getEntryMethod = logEntriesType?.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (logEntryType == null || getCountMethod == null || getEntryMethod == null)
            {
                return results;
            }

            try
            {
                startMethod?.Invoke(null, Array.Empty<object>());
                var count = Math.Min((int)getCountMethod.Invoke(null, Array.Empty<object>()), MaxEntries);

                for (var index = 0; index < count; index += 1)
                {
                    var logEntry = Activator.CreateInstance(logEntryType);
                    getEntryMethod.Invoke(null, new[] { (object)index, logEntry });
                    var mode = ReadIntMember(logEntryType, logEntry, "mode");
                    results.Add(new ConsoleEntrySnapshot
                    {
                        type = TypeFromMode(mode),
                        message = ReadFirstStringMember(logEntryType, logEntry, "condition", "message", "text"),
                        stackTrace = ReadFirstStringMember(logEntryType, logEntry, "stackTrace", "callstack"),
                        file = NormalizeUnityPath(ReadStringMember(logEntryType, logEntry, "file")),
                        line = ReadIntMember(logEntryType, logEntry, "line"),
                        mode = mode
                    });
                }
            }
            catch
            {
                results.Clear();
            }
            finally
            {
                endMethod?.Invoke(null, Array.Empty<object>());
            }

            return results;
        }

        private static bool ContainsEquivalentEntry(List<ConsoleEntrySnapshot> entries, ConsoleEntrySnapshot candidate)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry.type, candidate.type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.message, candidate.message, StringComparison.Ordinal) &&
                    string.Equals(entry.stackTrace, candidate.stackTrace, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static ConsoleDiagnosticEntry BuildDiagnostic(ConsoleEntrySnapshot entry)
        {
            var message = SanitizeDiagnosticText(entry.message);
            var stackTrace = SanitizeDiagnosticText(entry.stackTrace);
            var file = !string.IsNullOrWhiteSpace(entry.file) ? entry.file : ExtractUnityPath(message + "\n" + stackTrace);
            var line = entry.line > 0 ? entry.line : ExtractLine(message + "\n" + stackTrace);
            var severity = DetermineSeverity(entry, message);
            var category = ClassifyEntry(severity, message, stackTrace);

            return new ConsoleDiagnosticEntry
            {
                category = category,
                severity = severity,
                message = message,
                file = file,
                line = line,
                stackHint = ExtractStackHint(stackTrace),
                functionHint = ExtractFunctionHint(stackTrace),
                likelyRootCause = SummarizeRootCause(category, message, file, line),
                suggestedNextSafeAction = SuggestNextSafeAction(category, file, line)
            };
        }

        private static string DetermineSeverity(ConsoleEntrySnapshot entry, string message)
        {
            if ((entry.mode & ErrorModeMask) != 0)
            {
                return "error";
            }

            if ((entry.mode & WarningModeMask) != 0)
            {
                return "warning";
            }

            if (string.Equals(entry.type, LogType.Warning.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return "warning";
            }

            if (string.Equals(entry.type, LogType.Error.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.type, LogType.Exception.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.type, LogType.Assert.ToString(), StringComparison.OrdinalIgnoreCase) ||
                message.Contains("error CS", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            {
                return "error";
            }

            if (message.Contains("warning", StringComparison.OrdinalIgnoreCase))
            {
                return "warning";
            }

            return "info";
        }

        private static string TypeFromMode(int mode)
        {
            if ((mode & ErrorModeMask) != 0)
            {
                return LogType.Error.ToString();
            }

            if ((mode & WarningModeMask) != 0)
            {
                return LogType.Warning.ToString();
            }

            return LogType.Log.ToString();
        }

        private static int ResolveLogMessageFlags(params string[] names)
        {
            var flagsType = Type.GetType("UnityEditor.LogMessageFlags,UnityEditor.dll");
            if (flagsType == null || !flagsType.IsEnum)
            {
                return 0;
            }

            var mask = 0;
            foreach (var name in names)
            {
                if (Enum.IsDefined(flagsType, name))
                {
                    mask |= Convert.ToInt32(Enum.Parse(flagsType, name));
                }
            }

            return mask;
        }

        private static string ClassifyEntry(string severity, string message, string stackTrace)
        {
            var text = (message + "\n" + stackTrace).Trim();

            if (text.Contains("error CS", StringComparison.OrdinalIgnoreCase) || text.Contains("Compilation failed", StringComparison.OrdinalIgnoreCase))
            {
                return "compiler_error";
            }

            if (text.Contains("Importer", StringComparison.OrdinalIgnoreCase) || text.Contains("failed to import", StringComparison.OrdinalIgnoreCase) || text.Contains("Asset import", StringComparison.OrdinalIgnoreCase))
            {
                return "import_error";
            }

            if (text.Contains("Exception", StringComparison.OrdinalIgnoreCase) || text.Contains("NullReferenceException", StringComparison.OrdinalIgnoreCase))
            {
                return "runtime_exception";
            }

            if (severity == "warning")
            {
                return "warning";
            }

            return "unknown";
        }

        private static string SummarizeRootCause(string category, string message, string file, int line)
        {
            var location = string.IsNullOrWhiteSpace(file) ? string.Empty : $" at {file}{(line > 0 ? ":" + line : string.Empty)}";

            switch (category)
            {
                case "compiler_error":
                    return $"Unity reported a C# compiler error{location}; inspect the referenced script before attempting fixes.";
                case "runtime_exception":
                    return $"Unity reported a runtime exception{location}; inspect the first project stack frame and object state.";
                case "import_error":
                    return $"Unity reported an asset import problem{location}; inspect the asset and importer settings.";
                case "warning":
                    return $"Unity reported a warning{location}; review it after blocking errors are resolved.";
                default:
                    return string.IsNullOrWhiteSpace(message) ? "No actionable console message was available." : "Unity reported a console entry that needs manual classification.";
            }
        }

        private static string SuggestNextSafeAction(string category, string file, int line)
        {
            var location = string.IsNullOrWhiteSpace(file) ? "the reported location" : $"{file}{(line > 0 ? ":" + line : string.Empty)}";

            switch (category)
            {
                case "compiler_error":
                    return $"Read {location}, propose a minimal fix plan, then recompile and rerun console diagnostics before mutating files.";
                case "runtime_exception":
                    return $"Inspect {location} and related scene/object state, propose a guarded fix plan, then reproduce before editing.";
                case "import_error":
                    return $"Inspect {location} and importer metadata; avoid asset changes until the importer failure is understood.";
                case "warning":
                    return $"Record the warning and prioritize errors first; fix only if it blocks verification.";
                default:
                    return "Collect more context with project, asset, script, or scene inspection before proposing changes.";
            }
        }

        private static string ExtractUnityPath(string text)
        {
            var match = Regex.Match(text ?? string.Empty, @"(?:^|[\s\(])((?:Assets|Packages)/[^\s\):]+)");
            return match.Success ? NormalizeUnityPath(match.Groups[1].Value) : string.Empty;
        }

        private static int ExtractLine(string text)
        {
            var match = Regex.Match(text ?? string.Empty, @"(?:Assets|Packages)/[^\s\):]+\((\d+),\d+\)");

            if (match.Success && int.TryParse(match.Groups[1].Value, out var line))
            {
                return line;
            }

            match = Regex.Match(text ?? string.Empty, @":line (\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out line) ? line : 0;
        }

        private static string ExtractStackHint(string stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
            {
                return string.Empty;
            }

            var lines = stackTrace.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = NormalizeUnityPath(line.Trim());
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    return trimmed;
                }
            }

            return string.Empty;
        }

        private static string ExtractFunctionHint(string stackTrace)
        {
            var hint = ExtractStackHint(stackTrace);
            var index = hint.IndexOf(" (", StringComparison.Ordinal);
            return index > 0 ? hint.Substring(0, index) : hint;
        }

        private static string NormalizeUnityPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Replace('\\', '/');
            var assetsIndex = normalized.IndexOf("Assets/", StringComparison.Ordinal);
            var packagesIndex = normalized.IndexOf("Packages/", StringComparison.Ordinal);
            var start = -1;

            if (assetsIndex >= 0 && packagesIndex >= 0)
            {
                start = Math.Min(assetsIndex, packagesIndex);
            }
            else if (assetsIndex >= 0)
            {
                start = assetsIndex;
            }
            else if (packagesIndex >= 0)
            {
                start = packagesIndex;
            }

            if (start >= 0)
            {
                return normalized.Substring(start);
            }

            return Path.IsPathRooted(normalized) ? string.Empty : normalized;
        }

        private static string SanitizeDiagnosticText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Replace('\\', '/');
            var projectRoot = GetProjectRoot();

            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                normalized = normalized.Replace(projectRoot + "/", string.Empty);
            }

            normalized = Regex.Replace(normalized, @"(?:[A-Za-z]:)?/[^\s\)\(]*(?=(?:Assets|Packages)/)", string.Empty);
            normalized = Regex.Replace(normalized, @"(^|[\s\(])(?:[A-Za-z]:/[^\s\)\(]+|/[^\s\)\(]+)", match => match.Groups[1].Value + "[absolute-path]");
            return normalized;
        }

        private static string GetProjectRoot()
        {
            try
            {
                var dataDirectory = Directory.GetParent(Application.dataPath);
                return dataDirectory?.FullName.Replace('\\', '/') ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadStringMember(Type type, object instance, string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return field.GetValue(instance) as string ?? string.Empty;
            }

            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(instance) as string ?? string.Empty;
        }

        private static string ReadFirstStringMember(Type type, object instance, params string[] names)
        {
            foreach (var name in names)
            {
                var value = ReadStringMember(type, instance, name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static int ReadIntMember(Type type, object instance, string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var value = field != null ? field.GetValue(instance) : type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
            return value is int intValue ? intValue : 0;
        }
    }
}
