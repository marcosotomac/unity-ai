using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class MetaXrValidationReport
    {
        public bool likelyMetaXrInstalled;
        public string activeBuildTarget;
        public List<string> findings = new();
    }

    public static class MetaXrValidator
    {
        public static MetaXrValidationReport Validate()
        {
            var report = new MetaXrValidationReport
            {
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString()
            };

            var ovrPrefabs = AssetDatabase.FindAssets("OVRCameraRig t:Prefab");
            var interactionPrefabs = AssetDatabase.FindAssets("Interaction t:Prefab");

            report.likelyMetaXrInstalled = ovrPrefabs.Length > 0 || interactionPrefabs.Length > 0;

            if (!report.likelyMetaXrInstalled)
            {
                report.findings.Add("Meta XR assets were not detected by the initial prefab scan.");
            }

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                report.findings.Add("Active build target is not Android, which is required for Quest device builds.");
            }

            if (report.findings.Count == 0)
            {
                report.findings.Add("Initial Meta XR checks passed. Deeper OpenXR and Android settings validation is not implemented yet.");
            }

            return report;
        }
    }
}
