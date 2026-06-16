using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class RuntimeTelemetryRequest
    {
        public RuntimeTelemetryInput input = new();
    }

    [Serializable]
    public sealed class RuntimeTelemetryInput
    {
        public string pathPrefix;
        public bool includeInactive;
        public SceneRadiusFilterInput withinRadius;
        public bool includeRenderers = true;
        public bool includeColliders = true;
        public bool includeRigidbodies = true;
        public int maxObjects = 200;
    }

    [Serializable]
    public sealed class RuntimeBoundsTelemetry
    {
        public SceneVector3 center;
        public SceneVector3 size;
        public SceneVector3 min;
        public SceneVector3 max;
    }

    [Serializable]
    public sealed class RuntimeBodyTelemetry
    {
        public string dimension;
        public string bodyType;
        public float mass;
        public bool isKinematic;
        public bool useGravity;
        public float gravityScale;
        public bool sleeping;
        public SceneVector3 velocity;
        public float speed;
        public SceneVector3 angularVelocity;
        public float angularSpeed;
    }

    [Serializable]
    public sealed class RuntimeObjectTelemetry
    {
        public string name;
        public string path;
        public string tag;
        public int layer;
        public string layerName;
        public bool activeSelf;
        public bool activeInHierarchy;
        public SceneVector3 position;
        public SceneVector3 localPosition;
        public SceneVector3 rotationEuler;
        public SceneVector3 scale;
        public SceneVector3 lossyScale;
        public RuntimeBoundsTelemetry rendererBounds;
        public RuntimeBoundsTelemetry colliderBounds;
        public RuntimeBodyTelemetry body3D;
        public RuntimeBodyTelemetry body2D;
        public string[] components = Array.Empty<string>();
    }

    [Serializable]
    public sealed class RuntimeTelemetryReport
    {
        public string scenePath;
        public string sceneName;
        public bool isPlaying;
        public bool isPaused;
        public int frameCount;
        public float time;
        public float fixedTime;
        public float deltaTime;
        public float fixedDeltaTime;
        public float timeScale;
        public int scannedGameObjectCount;
        public int matchedGameObjectCount;
        public int returnedGameObjectCount;
        public bool truncated;
        public RuntimeObjectTelemetry[] objects = Array.Empty<RuntimeObjectTelemetry>();
        public string[] verificationSignals = Array.Empty<string>();
        public string capturedAtUtc;
    }

    public static class RuntimeTelemetryObserver
    {
        public static RuntimeTelemetryReport Capture(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new RuntimeTelemetryInput();
            var maxObjects = input.maxObjects <= 0 ? 200 : Math.Min(input.maxObjects, 500);
            var scene = EditorSceneManager.GetActiveScene();
            var radiusCenter = ResolveRadiusCenter(input.withinRadius);
            var objects = new List<RuntimeObjectTelemetry>();
            var scannedCount = 0;
            var matchedCount = 0;

            if (scene.IsValid())
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    CaptureHierarchy(root, root.name, input, radiusCenter, maxObjects, objects, ref scannedCount, ref matchedCount);
                }
            }

            return new RuntimeTelemetryReport
            {
                scenePath = scene.path,
                sceneName = scene.name,
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                frameCount = Time.frameCount,
                time = Time.time,
                fixedTime = Time.fixedTime,
                deltaTime = Time.deltaTime,
                fixedDeltaTime = Time.fixedDeltaTime,
                timeScale = Time.timeScale,
                scannedGameObjectCount = scannedCount,
                matchedGameObjectCount = matchedCount,
                returnedGameObjectCount = objects.Count,
                truncated = matchedCount > objects.Count,
                objects = objects.ToArray(),
                verificationSignals = new[] { "structured_observation", "runtime_telemetry_captured" },
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static void CaptureHierarchy(
            GameObject gameObject,
            string path,
            RuntimeTelemetryInput input,
            Vector3? radiusCenter,
            int maxObjects,
            List<RuntimeObjectTelemetry> output,
            ref int scannedCount,
            ref int matchedCount)
        {
            scannedCount++;
            if (Matches(gameObject, path, input, radiusCenter))
            {
                matchedCount++;
                if (output.Count < maxObjects)
                {
                    output.Add(BuildObjectTelemetry(gameObject, path, input));
                }
            }

            for (var index = 0; index < gameObject.transform.childCount; index++)
            {
                var child = gameObject.transform.GetChild(index).gameObject;
                CaptureHierarchy(child, path + "/" + child.name, input, radiusCenter, maxObjects, output, ref scannedCount, ref matchedCount);
            }
        }

        private static bool Matches(GameObject gameObject, string path, RuntimeTelemetryInput input, Vector3? radiusCenter)
        {
            if (!input.includeInactive && !gameObject.activeInHierarchy)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(input.pathPrefix) && !PathMatchesPrefix(path, input.pathPrefix))
            {
                return false;
            }

            return !radiusCenter.HasValue
                || Vector3.Distance(gameObject.transform.position, radiusCenter.Value) <= Math.Max(0f, input.withinRadius.radius);
        }

        private static RuntimeObjectTelemetry BuildObjectTelemetry(GameObject gameObject, string path, RuntimeTelemetryInput input)
        {
            var transform = gameObject.transform;
            return new RuntimeObjectTelemetry
            {
                name = gameObject.name,
                path = path,
                tag = SafeGetTag(gameObject),
                layer = gameObject.layer,
                layerName = LayerMask.LayerToName(gameObject.layer),
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                position = ToSceneVector3(transform.position),
                localPosition = ToSceneVector3(transform.localPosition),
                rotationEuler = ToSceneVector3(transform.eulerAngles),
                scale = ToSceneVector3(transform.localScale),
                lossyScale = ToSceneVector3(transform.lossyScale),
                rendererBounds = input.includeRenderers ? ToBoundsTelemetry(GetCombinedRendererBounds(gameObject)) : null,
                colliderBounds = input.includeColliders ? ToBoundsTelemetry(GetCombinedColliderBounds(gameObject)) : null,
                body3D = input.includeRigidbodies ? ToBodyTelemetry(gameObject.GetComponent<Rigidbody>()) : null,
                body2D = input.includeRigidbodies ? ToBodyTelemetry(gameObject.GetComponent<Rigidbody2D>()) : null,
                components = gameObject.GetComponents<Component>()
                    .Where(component => component != null)
                    .Select(component => component.GetType().FullName ?? component.GetType().Name)
                    .Take(32)
                    .ToArray()
            };
        }

        private static RuntimeBodyTelemetry ToBodyTelemetry(Rigidbody body)
        {
            if (body == null)
            {
                return null;
            }

            var velocity = body.linearVelocity;
            return new RuntimeBodyTelemetry
            {
                dimension = "3d",
                bodyType = body.isKinematic ? "kinematic" : "dynamic",
                mass = body.mass,
                isKinematic = body.isKinematic,
                useGravity = body.useGravity,
                gravityScale = body.useGravity ? 1f : 0f,
                sleeping = body.IsSleeping(),
                velocity = ToSceneVector3(velocity),
                speed = velocity.magnitude,
                angularVelocity = ToSceneVector3(body.angularVelocity),
                angularSpeed = body.angularVelocity.magnitude
            };
        }

        private static RuntimeBodyTelemetry ToBodyTelemetry(Rigidbody2D body)
        {
            if (body == null)
            {
                return null;
            }

            var velocity = new Vector3(body.linearVelocity.x, body.linearVelocity.y, 0f);
            var angularVelocity = new Vector3(0f, 0f, body.angularVelocity);
            return new RuntimeBodyTelemetry
            {
                dimension = "2d",
                bodyType = body.bodyType.ToString().ToLowerInvariant(),
                mass = body.mass,
                isKinematic = body.bodyType == RigidbodyType2D.Kinematic,
                useGravity = Math.Abs(body.gravityScale) > 0.0001f,
                gravityScale = body.gravityScale,
                sleeping = body.IsSleeping(),
                velocity = ToSceneVector3(velocity),
                speed = velocity.magnitude,
                angularVelocity = ToSceneVector3(angularVelocity),
                angularSpeed = Math.Abs(body.angularVelocity)
            };
        }

        private static Bounds? GetCombinedRendererBounds(GameObject gameObject)
        {
            var hasBounds = false;
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var renderer in gameObject.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds ? bounds : null;
        }

        private static Bounds? GetCombinedColliderBounds(GameObject gameObject)
        {
            var hasBounds = false;
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var collider in gameObject.GetComponentsInChildren<Collider>(true).Cast<Component>()
                         .Concat(gameObject.GetComponentsInChildren<Collider2D>(true)))
            {
                if (collider == null)
                {
                    continue;
                }

                var colliderBounds = collider is Collider collider3D
                    ? collider3D.bounds
                    : ((Collider2D)collider).bounds;
                if (!hasBounds)
                {
                    bounds = colliderBounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(colliderBounds);
                }
            }

            return hasBounds ? bounds : null;
        }

        private static RuntimeBoundsTelemetry ToBoundsTelemetry(Bounds? bounds)
        {
            if (!bounds.HasValue)
            {
                return null;
            }

            return new RuntimeBoundsTelemetry
            {
                center = ToSceneVector3(bounds.Value.center),
                size = ToSceneVector3(bounds.Value.size),
                min = ToSceneVector3(bounds.Value.min),
                max = ToSceneVector3(bounds.Value.max)
            };
        }

        private static Vector3? ResolveRadiusCenter(SceneRadiusFilterInput filter)
        {
            if (filter == null
                || string.IsNullOrWhiteSpace(filter.centerPath)
                && filter.center == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(filter.centerPath))
            {
                var target = FindByPath(filter.centerPath.Trim().Replace('\\', '/'));
                if (target == null)
                {
                    throw new InvalidOperationException($"Runtime telemetry radius centerPath was not found: {filter.centerPath}");
                }

                return target.transform.position;
            }

            return new Vector3(filter.center.x, filter.center.y, filter.center.z);
        }

        private static bool PathMatchesPrefix(string path, string rawPrefix)
        {
            var prefix = rawPrefix.Trim().TrimEnd('/').Replace('\\', '/');
            return string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var segments = path.Split('/');
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (!string.Equals(root.name, segments[0], StringComparison.Ordinal))
                {
                    continue;
                }

                var current = root.transform;
                for (var index = 1; index < segments.Length && current != null; index++)
                {
                    current = current.Find(segments[index]);
                }

                return current != null ? current.gameObject : null;
            }

            return null;
        }

        private static string SafeGetTag(GameObject gameObject)
        {
            try
            {
                return gameObject.tag;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static SceneVector3 ToSceneVector3(Vector3 value)
        {
            return new SceneVector3 { x = value.x, y = value.y, z = value.z };
        }

        private static RuntimeTelemetryRequest ParseRequest(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new RuntimeTelemetryRequest();
            }

            try
            {
                return JsonUtility.FromJson<RuntimeTelemetryRequest>(requestBody) ?? new RuntimeTelemetryRequest();
            }
            catch
            {
                return new RuntimeTelemetryRequest();
            }
        }
    }
}
