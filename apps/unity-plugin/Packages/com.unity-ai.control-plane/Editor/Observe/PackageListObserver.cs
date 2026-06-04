using System;
using System.Collections.Generic;
using UnityEditor.PackageManager;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class PackageListItem
    {
        public string name;
        public string displayName;
        public string version;
        public string source;
    }

    [Serializable]
    public sealed class PackageListReport
    {
        public int totalFound;
        public PackageListItem[] packages;
        public string capturedAtUtc;
    }

    public static class PackageListObserver
    {
        public static PackageListReport ListPackages()
        {
            var registeredPackages = PackageInfo.GetAllRegisteredPackages();
            var packages = new List<PackageListItem>();

            foreach (var packageInfo in registeredPackages)
            {
                packages.Add(new PackageListItem
                {
                    name = packageInfo.name,
                    displayName = packageInfo.displayName,
                    version = packageInfo.version,
                    source = packageInfo.source.ToString()
                });
            }

            return new PackageListReport
            {
                totalFound = packages.Count,
                packages = packages.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }
    }
}
