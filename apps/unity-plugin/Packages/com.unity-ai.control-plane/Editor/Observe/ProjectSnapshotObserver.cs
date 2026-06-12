using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class ProjectContextSnapshot
    {
        public ProjectSnapshotIdentity identity = new();
        public ProjectSnapshotConsole console = new();
        public ProjectSnapshotScenes scenes = new();
        public ProjectSnapshotPrefabs prefabs = new();
        public ProjectSnapshotScripts scripts = new();
        public ProjectSnapshotAssemblies assemblies = new();
        public ProjectSnapshotPackages packages = new();
        public ProjectSettingsReport settings = new();
        public ProjectSnapshotMetaXr metaXr = new();
        public ProjectSnapshotArtifacts artifacts = new();
        public ProjectSnapshotCapability[] capabilities = Array.Empty<ProjectSnapshotCapability>();
        public string[] riskFlags = Array.Empty<string>();
        public string[] recommendedNextActions = Array.Empty<string>();
        public string[] verificationSignals = Array.Empty<string>();
        public string[] partialFailures = Array.Empty<string>();
    }

    [Serializable]
    public sealed class ProjectSnapshotIdentity
    {
        public string projectName;
        public string unityVersion;
        public string dataPath;
        public string projectRoot;
        public string activeBuildTarget;
        public string capturedAtUtc;
    }

    [Serializable]
    public sealed class ProjectSnapshotConsole
    {
        public int errorCount;
        public int warningCount;
        public int logCount;
        public int diagnosticCount;
        public bool hasErrors;
        public ConsoleDiagnosticEntry[] topDiagnostics = Array.Empty<ConsoleDiagnosticEntry>();
    }

    [Serializable]
    public sealed class ProjectSnapshotScenes
    {
        public int totalFound;
        public int buildSettingsCount;
        public int returned;
        public bool truncated;
        public string activeScenePath;
        public string activeSceneName;
        public bool activeSceneIsDirty;
        public int activeRootGameObjectCount;
        public SceneListItem[] mainScenes = Array.Empty<SceneListItem>();
    }

    [Serializable]
    public sealed class ProjectSnapshotPrefabs
    {
        public int totalFound;
        public int returned;
        public bool truncated;
        public PrefabListItem[] importantPrefabs = Array.Empty<PrefabListItem>();
    }

    [Serializable]
    public sealed class ProjectSnapshotScripts
    {
        public int totalFound;
        public int returned;
        public bool truncated;
        public ScriptListItem[] importantScripts = Array.Empty<ScriptListItem>();
    }

    [Serializable]
    public sealed class ProjectSnapshotAssemblies
    {
        public int totalFound;
        public int returned;
        public bool truncated;
        public AssemblyListItem[] assemblies = Array.Empty<AssemblyListItem>();
    }

    [Serializable]
    public sealed class ProjectSnapshotPackages
    {
        public int totalFound;
        public int returned;
        public bool truncated;
        public PackageListItem[] packages = Array.Empty<PackageListItem>();
    }

    [Serializable]
    public sealed class ProjectSnapshotMetaXr
    {
        public bool likelyMetaXrInstalled;
        public string activeBuildTarget;
        public string[] findings = Array.Empty<string>();
    }

    [Serializable]
    public sealed class ProjectSnapshotArtifacts
    {
        public string auditLogPath;
        public bool auditLogExists;
        public int auditEventCount;
        public string auditLastModifiedUtc;
        public string checkpointsPath;
        public bool checkpointsDirectoryExists;
        public int checkpointCount;
    }

    [Serializable]
    public sealed class ProjectSnapshotCapability
    {
        public string name;
        public string effect;
    }

    public static class ProjectSnapshotObserver
    {
        private const int MaxDiagnostics = 5;
        private const int MaxScenes = 10;
        private const int MaxPrefabs = 10;
        private const int MaxScripts = 12;
        private const int MaxAssemblies = 20;
        private const int MaxPackages = 20;
        private const int MaxFindings = 10;

        public static ProjectContextSnapshot Capture()
        {
            var snapshot = new ProjectContextSnapshot();
            var failures = new List<string>();

            snapshot.identity = CaptureIdentity();
            TryCapture("console", failures, () => snapshot.console = CaptureConsole());
            TryCapture("scenes", failures, () => snapshot.scenes = CaptureScenes());
            TryCapture("prefabs", failures, () => snapshot.prefabs = CapturePrefabs());
            TryCapture("scripts", failures, () => snapshot.scripts = CaptureScripts());
            TryCapture("assemblies", failures, () => snapshot.assemblies = CaptureAssemblies());
            TryCapture("packages", failures, () => snapshot.packages = CapturePackages());
            TryCapture("settings", failures, () => snapshot.settings = ProjectSettingsInspector.Inspect());
            TryCapture("meta_xr", failures, () => snapshot.metaXr = CaptureMetaXr());
            TryCapture("artifacts", failures, () => snapshot.artifacts = CaptureArtifacts());

            snapshot.partialFailures = failures.ToArray();
            snapshot.capabilities = BuildCapabilities();
            snapshot.riskFlags = BuildRiskFlags(snapshot).ToArray();
            snapshot.recommendedNextActions = BuildRecommendedNextActions(snapshot).ToArray();
            snapshot.verificationSignals = BuildVerificationSignals(snapshot).ToArray();
            return snapshot;
        }

        private static ProjectSnapshotIdentity CaptureIdentity()
        {
            return new ProjectSnapshotIdentity
            {
                projectName = GetProjectName(),
                unityVersion = Application.unityVersion,
                dataPath = "Assets",
                projectRoot = "[project-root]",
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static ProjectSnapshotConsole CaptureConsole()
        {
            var report = ConsoleLogBridge.Diagnose();
            return new ProjectSnapshotConsole
            {
                errorCount = report.errorCount,
                warningCount = report.warningCount,
                logCount = report.logCount,
                diagnosticCount = report.diagnosticCount,
                hasErrors = report.hasErrors,
                topDiagnostics = SelectTopDiagnostics(report.diagnostics).ToArray()
            };
        }

        private static List<ConsoleDiagnosticEntry> SelectTopDiagnostics(List<ConsoleDiagnosticEntry> diagnostics)
        {
            var selected = new List<ConsoleDiagnosticEntry>();
            AddDiagnosticsByCategory(diagnostics, selected, "compiler_error");
            AddDiagnosticsByCategory(diagnostics, selected, "runtime_exception");
            AddDiagnosticsByCategory(diagnostics, selected, "import_error");
            AddDiagnosticsByCategory(diagnostics, selected, "warning");
            AddDiagnosticsByCategory(diagnostics, selected, "unknown");
            return selected;
        }

        private static void AddDiagnosticsByCategory(List<ConsoleDiagnosticEntry> diagnostics, List<ConsoleDiagnosticEntry> selected, string category)
        {
            if (diagnostics == null || selected.Count >= MaxDiagnostics)
            {
                return;
            }

            for (var index = 0; index < diagnostics.Count && selected.Count < MaxDiagnostics; index += 1)
            {
                var diagnostic = diagnostics[index];
                if (diagnostic == null || !string.Equals(diagnostic.category, category, StringComparison.Ordinal) || ContainsDiagnostic(selected, diagnostic))
                {
                    continue;
                }

                selected.Add(diagnostic);
            }
        }

        private static bool ContainsDiagnostic(List<ConsoleDiagnosticEntry> diagnostics, ConsoleDiagnosticEntry candidate)
        {
            for (var index = 0; index < diagnostics.Count; index += 1)
            {
                var diagnostic = diagnostics[index];
                if (string.Equals(diagnostic.category, candidate.category, StringComparison.Ordinal) &&
                    string.Equals(diagnostic.message, candidate.message, StringComparison.Ordinal) &&
                    string.Equals(diagnostic.file, candidate.file, StringComparison.Ordinal) &&
                    diagnostic.line == candidate.line)
                {
                    return true;
                }
            }

            return false;
        }

        private static ProjectSnapshotScenes CaptureScenes()
        {
            var sceneReport = SceneListObserver.ListScenes();
            var activeScene = EditorSceneManager.GetActiveScene();
            var activeRoots = activeScene.IsValid() ? activeScene.GetRootGameObjects() : Array.Empty<GameObject>();
            var returnedScenes = Math.Min(sceneReport.scenes != null ? sceneReport.scenes.Length : 0, MaxScenes);
            var mainScenes = new List<SceneListItem>();

            if (sceneReport.scenes != null)
            {
                for (var index = 0; index < sceneReport.scenes.Length && mainScenes.Count < MaxScenes; index += 1)
                {
                    var scene = sceneReport.scenes[index];
                    if (scene.inBuildSettings || scene.path.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        mainScenes.Add(scene);
                    }
                }

                for (var index = 0; index < sceneReport.scenes.Length && mainScenes.Count < MaxScenes; index += 1)
                {
                    if (!ContainsScene(mainScenes, sceneReport.scenes[index].path))
                    {
                        mainScenes.Add(sceneReport.scenes[index]);
                    }
                }
            }

            return new ProjectSnapshotScenes
            {
                totalFound = sceneReport.totalFound,
                buildSettingsCount = sceneReport.buildSettingsCount,
                returned = returnedScenes,
                truncated = sceneReport.truncated || (sceneReport.scenes != null && sceneReport.scenes.Length > MaxScenes),
                activeScenePath = activeScene.IsValid() ? activeScene.path : string.Empty,
                activeSceneName = activeScene.IsValid() ? activeScene.name : string.Empty,
                activeSceneIsDirty = activeScene.IsValid() && activeScene.isDirty,
                activeRootGameObjectCount = activeRoots.Length,
                mainScenes = mainScenes.ToArray()
            };
        }

        private static ProjectSnapshotPrefabs CapturePrefabs()
        {
            var report = PrefabObserver.ListPrefabs(BuildInputJson($"{{\"folder\":\"Assets\",\"maxResults\":{MaxPrefabs}}}"));
            return new ProjectSnapshotPrefabs
            {
                totalFound = report.totalFound,
                returned = report.returned,
                truncated = report.truncated,
                importantPrefabs = report.prefabs ?? Array.Empty<PrefabListItem>()
            };
        }

        private static ProjectSnapshotScripts CaptureScripts()
        {
            var report = ScriptAndAssemblyObserver.ListScripts(BuildInputJson($"{{\"includePackages\":false,\"maxResults\":{MaxScripts}}}"));
            return new ProjectSnapshotScripts
            {
                totalFound = report.totalFound,
                returned = report.returned,
                truncated = report.truncated,
                importantScripts = report.scripts ?? Array.Empty<ScriptListItem>()
            };
        }

        private static ProjectSnapshotAssemblies CaptureAssemblies()
        {
            var report = ScriptAndAssemblyObserver.ListAssemblies(BuildInputJson($"{{\"maxResults\":{MaxAssemblies}}}"));
            return new ProjectSnapshotAssemblies
            {
                totalFound = report.totalFound,
                returned = report.returned,
                truncated = report.truncated,
                assemblies = report.assemblies ?? Array.Empty<AssemblyListItem>()
            };
        }

        private static ProjectSnapshotPackages CapturePackages()
        {
            var report = PackageListObserver.ListPackages();
            var packages = new List<PackageListItem>();

            if (report.packages != null)
            {
                for (var index = 0; index < report.packages.Length && packages.Count < MaxPackages; index += 1)
                {
                    packages.Add(report.packages[index]);
                }
            }

            return new ProjectSnapshotPackages
            {
                totalFound = report.totalFound,
                returned = packages.Count,
                truncated = report.totalFound > packages.Count,
                packages = packages.ToArray()
            };
        }

        private static ProjectSnapshotMetaXr CaptureMetaXr()
        {
            var report = MetaXrValidator.Validate();
            return new ProjectSnapshotMetaXr
            {
                likelyMetaXrInstalled = report.likelyMetaXrInstalled,
                activeBuildTarget = report.activeBuildTarget,
                findings = Take(report.findings, MaxFindings).ToArray()
            };
        }

        private static ProjectSnapshotArtifacts CaptureArtifacts()
        {
            const string checkpointPath = "UnityAIArtifacts/Checkpoints";
            var projectRoot = GetProjectRoot();
            var auditAbsolutePath = Path.Combine(projectRoot, "UnityAIArtifacts", "Audit", "events.jsonl");
            var checkpointsAbsolutePath = Path.Combine(projectRoot, checkpointPath);
            var auditExists = File.Exists(auditAbsolutePath);
            var checkpointsExist = Directory.Exists(checkpointsAbsolutePath);

            return new ProjectSnapshotArtifacts
            {
                auditLogPath = "UnityAIArtifacts/Audit/events.jsonl",
                auditLogExists = auditExists,
                auditEventCount = auditExists ? CountNonEmptyLines(auditAbsolutePath) : 0,
                auditLastModifiedUtc = auditExists ? File.GetLastWriteTimeUtc(auditAbsolutePath).ToString("O") : string.Empty,
                checkpointsPath = checkpointPath,
                checkpointsDirectoryExists = checkpointsExist,
                checkpointCount = checkpointsExist ? Directory.GetFiles(checkpointsAbsolutePath, "*.bak").Length : 0
            };
        }

        private static ProjectSnapshotCapability[] BuildCapabilities()
        {
            return new[]
            {
                Capability("unity.project.inspect", "read"),
                Capability("unity.project.snapshot", "read"),
                Capability("unity.console.read", "read"),
                Capability("unity.console.diagnose", "read"),
                Capability("unity.console.plan_fix", "read"),
                Capability("unity.console.apply_fix", "mutating_token_required"),
                Capability("unity.assets.list", "read"),
                Capability("unity.scenes.list", "read"),
                Capability("unity.scene.inspect", "read"),
                Capability("unity.prefabs.list", "read"),
                Capability("unity.prefab.inspect", "read"),
                Capability("unity.asset.dependencies", "read"),
                Capability("unity.scripts.list", "read"),
                Capability("unity.assemblies.list", "read"),
                Capability("unity.packages.list", "read"),
                Capability("unity.project.settings.inspect", "read"),
                Capability("unity.vision.capture", "writes_artifact"),
                Capability("unity.meta_xr.validate_setup", "read"),
                Capability("unity.editor.create_empty_game_object", "mutating_token_required"),
                Capability("unity.editor.undo_last_operation", "mutating_token_required")
            };
        }

        private static List<string> BuildRiskFlags(ProjectContextSnapshot snapshot)
        {
            var flags = new List<string> { "missing_bridge_token_not_relevant" };

            if (snapshot.console.hasErrors)
            {
                flags.Add("compiler_errors_present");
            }

            if (snapshot.console.warningCount > 0)
            {
                flags.Add("warnings_present");
            }

            if (snapshot.scenes.totalFound == 0)
            {
                flags.Add("no_scenes_found");
            }

            if (!snapshot.metaXr.likelyMetaXrInstalled || (snapshot.metaXr.findings != null && snapshot.metaXr.findings.Length > 0))
            {
                flags.Add("meta_xr_incomplete");
            }

            if (snapshot.partialFailures != null && snapshot.partialFailures.Length > 0)
            {
                flags.Add("partial_snapshot");
            }

            return flags;
        }

        private static List<string> BuildRecommendedNextActions(ProjectContextSnapshot snapshot)
        {
            var actions = new List<string>();

            if (snapshot.console.hasErrors)
            {
                actions.Add("run unity.console.diagnose");
                actions.Add("run unity.console.plan_fix");
            }

            if (snapshot.scenes.totalFound == 0)
            {
                actions.Add("create or import an initial scene before scene mutation");
            }

            if (snapshot.metaXr.findings != null && snapshot.metaXr.findings.Length > 0)
            {
                actions.Add("run unity.meta_xr.validate_setup");
            }

            if (actions.Count == 0)
            {
                actions.Add("inspect active scene before acting");
            }

            return actions;
        }

        private static List<string> BuildVerificationSignals(ProjectContextSnapshot snapshot)
        {
            var signals = new List<string> { "structured_observation", "console_snapshot", "console_diagnostics" };

            if (snapshot.console.errorCount == 0)
            {
                signals.Add("console_clean");
            }

            if (snapshot.metaXr.likelyMetaXrInstalled)
            {
                signals.Add("xr_settings_valid");
            }

            return signals;
        }

        private static ProjectSnapshotCapability Capability(string name, string effect)
        {
            return new ProjectSnapshotCapability
            {
                name = name,
                effect = effect
            };
        }

        private static void TryCapture(string section, List<string> failures, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failures.Add($"{section}: {exception.GetType().Name}");
            }
        }

        private static List<T> Take<T>(IList<T> input, int max)
        {
            var output = new List<T>();

            if (input == null)
            {
                return output;
            }

            for (var index = 0; index < input.Count && output.Count < max; index += 1)
            {
                output.Add(input[index]);
            }

            return output;
        }

        private static bool ContainsScene(List<SceneListItem> scenes, string path)
        {
            foreach (var scene in scenes)
            {
                if (scene.path == path)
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildInputJson(string inputJson)
        {
            return $"{{\"input\":{inputJson}}}";
        }

        private static int CountNonEmptyLines(string path)
        {
            var count = 0;
            foreach (var line in File.ReadLines(path))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    count += 1;
                }
            }

            return count;
        }

        private static string GetProjectName()
        {
            var projectRoot = GetProjectRoot();
            return string.IsNullOrWhiteSpace(projectRoot) ? string.Empty : Path.GetFileName(projectRoot);
        }

        private static string GetProjectRoot()
        {
            var parent = Directory.GetParent(Application.dataPath);
            return parent?.FullName ?? Application.dataPath;
        }
    }
}
