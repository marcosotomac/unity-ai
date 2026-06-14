using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class PhysicsInspectRequest
    {
        public PhysicsInspectInput input = new();
    }

    [Serializable]
    public sealed class PhysicsInspectInput
    {
        public string pathPrefix;
        public bool includeInactive;
        public string dimension = "all";
        public SceneRadiusFilterInput withinRadius;
        public bool includeOverlapDiagnostics = true;
        public int maxObjects = 200;
        public int maxOverlaps = 200;
    }

    [Serializable]
    public sealed class PhysicsBodyInfo
    {
        public string dimension;
        public string bodyType;
        public float mass;
        public bool isKinematic;
        public bool useGravity;
        public float gravityScale;
        public bool sleeping;
        public SceneVector3 velocity;
        public SceneVector3 angularVelocity;
        public SceneVector3 centerOfMass;
        public string collisionDetectionMode;
        public string interpolation;
        public string constraints;
        public bool forceEstimateAvailable;
        public float forceSampleDeltaSeconds;
        public SceneVector3 estimatedAcceleration;
        public SceneVector3 estimatedNetForce;
    }

    [Serializable]
    public sealed class PhysicsColliderInfo
    {
        public string dimension;
        public string type;
        public bool enabled;
        public bool isTrigger;
        public string material;
        public SceneVector3 boundsCenter;
        public SceneVector3 boundsSize;
        public SceneVector3 localCenter;
        public SceneVector3 shapeSize;
        public float radius;
        public float height;
        public string direction;
        public bool convex;
        public string mesh;
        public string attachedRigidbodyPath;
    }

    [Serializable]
    public sealed class PhysicsObjectInfo
    {
        public string name;
        public string path;
        public bool activeInHierarchy;
        public int layer;
        public string layerName;
        public SceneVector3 worldPosition;
        public PhysicsBodyInfo body3D;
        public PhysicsBodyInfo body2D;
        public PhysicsColliderInfo[] colliders3D;
        public PhysicsColliderInfo[] colliders2D;
        public string[] issues;
    }

    [Serializable]
    public sealed class PhysicsOverlapInfo
    {
        public string dimension;
        public string pathA;
        public string colliderTypeA;
        public string pathB;
        public string colliderTypeB;
        public bool triggerOnly;
        public float penetrationDepth;
        public SceneVector3 separationDirection;
        public SceneVector3 relativeVelocity;
        public float relativeNormalSpeed;
    }

    [Serializable]
    public sealed class PhysicsInspectReport
    {
        public string scenePath;
        public string sceneName;
        public bool isPlaying;
        public SceneVector3 gravity3D;
        public SceneVector3 gravity2D;
        public int scannedGameObjectCount;
        public int matchedPhysicsObjectCount;
        public int returnedPhysicsObjectCount;
        public bool truncatedByObjectCount;
        public int detectedOverlapCount;
        public int returnedOverlapCount;
        public bool truncatedByOverlapCount;
        public bool forceEstimatesAreSampled;
        public string forceEstimateCaveat;
        public PhysicsObjectInfo[] objects;
        public PhysicsOverlapInfo[] overlaps;
        public string capturedAtUtc;
    }

    public static class PhysicsInspector
    {
        private sealed class BodySample
        {
            public double timestamp;
            public Vector3 velocity;
        }

        private sealed class Collider3DEntry
        {
            public string path;
            public Collider collider;
        }

        private sealed class Collider2DEntry
        {
            public string path;
            public Collider2D collider;
        }

        private static readonly Dictionary<int, BodySample> BodySamples3D = new();
        private static readonly Dictionary<int, BodySample> BodySamples2D = new();

        public static PhysicsInspectReport Inspect(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new PhysicsInspectInput();
            var maxObjects = input.maxObjects <= 0 ? 200 : Math.Min(input.maxObjects, 500);
            var maxOverlaps = Math.Max(0, Math.Min(input.maxOverlaps, 1000));
            var dimension = NormalizeDimension(input.dimension);
            var scene = EditorSceneManager.GetActiveScene();
            var radiusCenter = ResolveRadiusCenter(input.withinRadius);
            var objects = new List<PhysicsObjectInfo>();
            var colliders3D = new List<Collider3DEntry>();
            var colliders2D = new List<Collider2DEntry>();
            var scannedCount = 0;
            var matchedCount = 0;

            if (scene.IsValid())
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    InspectHierarchy(
                        root,
                        root.name,
                        input,
                        dimension,
                        radiusCenter,
                        maxObjects,
                        objects,
                        colliders3D,
                        colliders2D,
                        ref scannedCount,
                        ref matchedCount);
                }
            }

            var overlaps = new List<PhysicsOverlapInfo>();
            var detectedOverlapCount = 0;
            if (input.includeOverlapDiagnostics && maxOverlaps >= 0)
            {
                if (dimension != "2d")
                {
                    Add3DOverlaps(colliders3D, maxOverlaps, overlaps, ref detectedOverlapCount);
                }

                if (dimension != "3d")
                {
                    Add2DOverlaps(colliders2D, maxOverlaps, overlaps, ref detectedOverlapCount);
                }
            }

            PruneSamples(BodySamples3D);
            PruneSamples(BodySamples2D);

            return new PhysicsInspectReport
            {
                scenePath = scene.path,
                sceneName = scene.name,
                isPlaying = EditorApplication.isPlaying,
                gravity3D = ToSceneVector3(Physics.gravity),
                gravity2D = ToSceneVector3(Physics2D.gravity),
                scannedGameObjectCount = scannedCount,
                matchedPhysicsObjectCount = matchedCount,
                returnedPhysicsObjectCount = objects.Count,
                truncatedByObjectCount = matchedCount > objects.Count,
                detectedOverlapCount = detectedOverlapCount,
                returnedOverlapCount = overlaps.Count,
                truncatedByOverlapCount = detectedOverlapCount > overlaps.Count,
                forceEstimatesAreSampled = true,
                forceEstimateCaveat = "Estimated net force is mass * velocity delta / sample delta between calls; it includes gravity, collisions, constraints, and scripted velocity changes, not only AddForce calls.",
                objects = objects.ToArray(),
                overlaps = overlaps.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static void InspectHierarchy(
            GameObject gameObject,
            string path,
            PhysicsInspectInput input,
            string dimension,
            Vector3? radiusCenter,
            int maxObjects,
            List<PhysicsObjectInfo> output,
            List<Collider3DEntry> colliderEntries3D,
            List<Collider2DEntry> colliderEntries2D,
            ref int scannedCount,
            ref int matchedCount)
        {
            scannedCount++;
            var activeMatches = input.includeInactive || gameObject.activeInHierarchy;
            var pathMatches = string.IsNullOrWhiteSpace(input.pathPrefix)
                || PathMatchesPrefix(path, input.pathPrefix);
            var radiusMatches = !radiusCenter.HasValue
                || Vector3.Distance(gameObject.transform.position, radiusCenter.Value) <= Math.Max(0f, input.withinRadius.radius);

            if (activeMatches && pathMatches && radiusMatches)
            {
                var body3D = dimension == "2d" ? null : gameObject.GetComponent<Rigidbody>();
                var body2D = dimension == "3d" ? null : gameObject.GetComponent<Rigidbody2D>();
                var colliders3D = dimension == "2d" ? Array.Empty<Collider>() : gameObject.GetComponents<Collider>();
                var colliders2D = dimension == "3d" ? Array.Empty<Collider2D>() : gameObject.GetComponents<Collider2D>();

                if (body3D != null || body2D != null || colliders3D.Length > 0 || colliders2D.Length > 0)
                {
                    matchedCount++;
                    if (output.Count < maxObjects)
                    {
                        var issues = new List<string>();
                        var colliderInfos3D = BuildColliderInfos3D(path, colliders3D, colliderEntries3D, issues);
                        var colliderInfos2D = BuildColliderInfos2D(path, colliders2D, colliderEntries2D, issues);
                        AddBodyIssues(gameObject, body3D, body2D, colliders3D, colliders2D, issues);

                        output.Add(new PhysicsObjectInfo
                        {
                            name = gameObject.name,
                            path = path,
                            activeInHierarchy = gameObject.activeInHierarchy,
                            layer = gameObject.layer,
                            layerName = LayerMask.LayerToName(gameObject.layer),
                            worldPosition = ToSceneVector3(gameObject.transform.position),
                            body3D = BuildBodyInfo(body3D),
                            body2D = BuildBodyInfo(body2D),
                            colliders3D = colliderInfos3D,
                            colliders2D = colliderInfos2D,
                            issues = issues.ToArray()
                        });
                    }
                }
            }

            for (var index = 0; index < gameObject.transform.childCount; index++)
            {
                var child = gameObject.transform.GetChild(index).gameObject;
                InspectHierarchy(
                    child,
                    path + "/" + child.name,
                    input,
                    dimension,
                    radiusCenter,
                    maxObjects,
                    output,
                    colliderEntries3D,
                    colliderEntries2D,
                    ref scannedCount,
                    ref matchedCount);
            }
        }

        private static PhysicsBodyInfo BuildBodyInfo(Rigidbody body)
        {
            if (body == null)
            {
                return null;
            }

            var velocity = body.linearVelocity;
            var estimate = SampleForce(BodySamples3D, body.GetInstanceID(), velocity, body.mass, !body.isKinematic);
            return new PhysicsBodyInfo
            {
                dimension = "3d",
                bodyType = body.isKinematic ? "kinematic" : "dynamic",
                mass = body.mass,
                isKinematic = body.isKinematic,
                useGravity = body.useGravity,
                gravityScale = body.useGravity ? 1f : 0f,
                sleeping = body.IsSleeping(),
                velocity = ToSceneVector3(velocity),
                angularVelocity = ToSceneVector3(body.angularVelocity),
                centerOfMass = ToSceneVector3(body.worldCenterOfMass),
                collisionDetectionMode = body.collisionDetectionMode.ToString(),
                interpolation = body.interpolation.ToString(),
                constraints = body.constraints.ToString(),
                forceEstimateAvailable = estimate.available,
                forceSampleDeltaSeconds = estimate.deltaSeconds,
                estimatedAcceleration = ToSceneVector3(estimate.acceleration),
                estimatedNetForce = ToSceneVector3(estimate.netForce)
            };
        }

        private static PhysicsBodyInfo BuildBodyInfo(Rigidbody2D body)
        {
            if (body == null)
            {
                return null;
            }

            var velocity = new Vector3(body.linearVelocity.x, body.linearVelocity.y, 0f);
            var estimate = SampleForce(BodySamples2D, body.GetInstanceID(), velocity, body.mass, body.bodyType == RigidbodyType2D.Dynamic);
            return new PhysicsBodyInfo
            {
                dimension = "2d",
                bodyType = body.bodyType.ToString().ToLowerInvariant(),
                mass = body.mass,
                isKinematic = body.bodyType == RigidbodyType2D.Kinematic,
                useGravity = Math.Abs(body.gravityScale) > 0.0001f,
                gravityScale = body.gravityScale,
                sleeping = body.IsSleeping(),
                velocity = ToSceneVector3(velocity),
                angularVelocity = ToSceneVector3(new Vector3(0f, 0f, body.angularVelocity)),
                centerOfMass = ToSceneVector3(body.worldCenterOfMass),
                collisionDetectionMode = body.collisionDetectionMode.ToString(),
                interpolation = body.interpolation.ToString(),
                constraints = body.constraints.ToString(),
                forceEstimateAvailable = estimate.available,
                forceSampleDeltaSeconds = estimate.deltaSeconds,
                estimatedAcceleration = ToSceneVector3(estimate.acceleration),
                estimatedNetForce = ToSceneVector3(estimate.netForce)
            };
        }

        private static (bool available, float deltaSeconds, Vector3 acceleration, Vector3 netForce) SampleForce(
            Dictionary<int, BodySample> samples,
            int instanceId,
            Vector3 velocity,
            float mass,
            bool dynamicBody)
        {
            var now = EditorApplication.timeSinceStartup;
            var available = false;
            var deltaSeconds = 0f;
            var acceleration = Vector3.zero;
            var netForce = Vector3.zero;

            if (dynamicBody && samples.TryGetValue(instanceId, out var previous))
            {
                var delta = now - previous.timestamp;
                if (delta >= 0.001 && delta <= 10)
                {
                    deltaSeconds = (float)delta;
                    acceleration = (velocity - previous.velocity) / deltaSeconds;
                    netForce = acceleration * mass;
                    available = true;
                }
            }

            samples[instanceId] = new BodySample { timestamp = now, velocity = velocity };
            return (available, deltaSeconds, acceleration, netForce);
        }

        private static PhysicsColliderInfo[] BuildColliderInfos3D(
            string path,
            Collider[] colliders,
            List<Collider3DEntry> entries,
            List<string> issues)
        {
            var result = new List<PhysicsColliderInfo>();
            foreach (var collider in colliders)
            {
                if (collider == null)
                {
                    continue;
                }

                var info = new PhysicsColliderInfo
                {
                    dimension = "3d",
                    type = collider.GetType().Name,
                    enabled = collider.enabled,
                    isTrigger = collider.isTrigger,
                    material = collider.sharedMaterial != null ? collider.sharedMaterial.name : string.Empty,
                    boundsCenter = ToSceneVector3(collider.bounds.center),
                    boundsSize = ToSceneVector3(collider.bounds.size),
                    localCenter = ToSceneVector3(Vector3.zero),
                    shapeSize = ToSceneVector3(Vector3.zero),
                    direction = string.Empty,
                    mesh = string.Empty,
                    attachedRigidbodyPath = collider.attachedRigidbody != null ? GetGameObjectPath(collider.attachedRigidbody.gameObject) : string.Empty
                };

                if (collider is BoxCollider box)
                {
                    info.localCenter = ToSceneVector3(box.center);
                    info.shapeSize = ToSceneVector3(box.size);
                }
                else if (collider is SphereCollider sphere)
                {
                    info.localCenter = ToSceneVector3(sphere.center);
                    info.radius = sphere.radius;
                }
                else if (collider is CapsuleCollider capsule)
                {
                    info.localCenter = ToSceneVector3(capsule.center);
                    info.radius = capsule.radius;
                    info.height = capsule.height;
                    info.direction = capsule.direction == 0 ? "x" : capsule.direction == 1 ? "y" : "z";
                }
                else if (collider is MeshCollider mesh)
                {
                    info.convex = mesh.convex;
                    info.mesh = mesh.sharedMesh != null ? mesh.sharedMesh.name : string.Empty;
                    if (mesh.attachedRigidbody != null && !mesh.attachedRigidbody.isKinematic && !mesh.convex)
                    {
                        issues.Add("dynamic_rigidbody_has_non_convex_mesh_collider");
                    }
                }

                if (!collider.enabled)
                {
                    issues.Add("disabled_3d_collider");
                }

                entries.Add(new Collider3DEntry { path = path, collider = collider });
                result.Add(info);
            }

            return result.ToArray();
        }

        private static PhysicsColliderInfo[] BuildColliderInfos2D(
            string path,
            Collider2D[] colliders,
            List<Collider2DEntry> entries,
            List<string> issues)
        {
            var result = new List<PhysicsColliderInfo>();
            foreach (var collider in colliders)
            {
                if (collider == null)
                {
                    continue;
                }

                var info = new PhysicsColliderInfo
                {
                    dimension = "2d",
                    type = collider.GetType().Name,
                    enabled = collider.enabled,
                    isTrigger = collider.isTrigger,
                    material = collider.sharedMaterial != null ? collider.sharedMaterial.name : string.Empty,
                    boundsCenter = ToSceneVector3(collider.bounds.center),
                    boundsSize = ToSceneVector3(collider.bounds.size),
                    localCenter = ToSceneVector3(new Vector3(collider.offset.x, collider.offset.y, 0f)),
                    shapeSize = ToSceneVector3(Vector3.zero),
                    direction = string.Empty,
                    mesh = string.Empty,
                    attachedRigidbodyPath = collider.attachedRigidbody != null ? GetGameObjectPath(collider.attachedRigidbody.gameObject) : string.Empty
                };

                if (collider is BoxCollider2D box)
                {
                    info.shapeSize = ToSceneVector3(new Vector3(box.size.x, box.size.y, 0f));
                }
                else if (collider is CircleCollider2D circle)
                {
                    info.radius = circle.radius;
                }
                else if (collider is CapsuleCollider2D capsule)
                {
                    info.shapeSize = ToSceneVector3(new Vector3(capsule.size.x, capsule.size.y, 0f));
                    info.direction = capsule.direction.ToString();
                }

                if (!collider.enabled)
                {
                    issues.Add("disabled_2d_collider");
                }

                entries.Add(new Collider2DEntry { path = path, collider = collider });
                result.Add(info);
            }

            return result.ToArray();
        }

        private static void AddBodyIssues(
            GameObject gameObject,
            Rigidbody body3D,
            Rigidbody2D body2D,
            Collider[] colliders3D,
            Collider2D[] colliders2D,
            List<string> issues)
        {
            if (body3D != null && !body3D.isKinematic && colliders3D.Length == 0 && gameObject.GetComponentInChildren<Collider>() == null)
            {
                issues.Add("dynamic_3d_body_without_collider");
            }

            if (body2D != null && body2D.bodyType == RigidbodyType2D.Dynamic && colliders2D.Length == 0 && gameObject.GetComponentInChildren<Collider2D>() == null)
            {
                issues.Add("dynamic_2d_body_without_collider");
            }

            var renderer = gameObject.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            foreach (var collider in colliders3D)
            {
                if (collider.enabled && !collider.bounds.Intersects(renderer.bounds))
                {
                    issues.Add("3d_collider_does_not_overlap_renderer_bounds");
                    break;
                }
            }

            foreach (var collider in colliders2D)
            {
                if (collider.enabled && !collider.bounds.Intersects(renderer.bounds))
                {
                    issues.Add("2d_collider_does_not_overlap_renderer_bounds");
                    break;
                }
            }
        }

        private static void Add3DOverlaps(
            List<Collider3DEntry> entries,
            int maxOverlaps,
            List<PhysicsOverlapInfo> output,
            ref int detectedCount)
        {
            for (var leftIndex = 0; leftIndex < entries.Count; leftIndex++)
            {
                var left = entries[leftIndex];
                if (!IsUsable(left.collider))
                {
                    continue;
                }

                for (var rightIndex = leftIndex + 1; rightIndex < entries.Count; rightIndex++)
                {
                    var right = entries[rightIndex];
                    if (!IsUsable(right.collider)
                        || left.collider.attachedRigidbody != null
                        && left.collider.attachedRigidbody == right.collider.attachedRigidbody
                        || !left.collider.bounds.Intersects(right.collider.bounds))
                    {
                        continue;
                    }

                    if (!Physics.ComputePenetration(
                            left.collider,
                            left.collider.transform.position,
                            left.collider.transform.rotation,
                            right.collider,
                            right.collider.transform.position,
                            right.collider.transform.rotation,
                            out var direction,
                            out var distance))
                    {
                        continue;
                    }

                    detectedCount++;
                    if (output.Count >= maxOverlaps)
                    {
                        continue;
                    }

                    var relativeVelocity = GetVelocity(right.collider.attachedRigidbody) - GetVelocity(left.collider.attachedRigidbody);
                    output.Add(new PhysicsOverlapInfo
                    {
                        dimension = "3d",
                        pathA = left.path,
                        colliderTypeA = left.collider.GetType().Name,
                        pathB = right.path,
                        colliderTypeB = right.collider.GetType().Name,
                        triggerOnly = left.collider.isTrigger || right.collider.isTrigger,
                        penetrationDepth = distance,
                        separationDirection = ToSceneVector3(direction),
                        relativeVelocity = ToSceneVector3(relativeVelocity),
                        relativeNormalSpeed = Math.Abs(Vector3.Dot(relativeVelocity, direction))
                    });
                }
            }
        }

        private static void Add2DOverlaps(
            List<Collider2DEntry> entries,
            int maxOverlaps,
            List<PhysicsOverlapInfo> output,
            ref int detectedCount)
        {
            for (var leftIndex = 0; leftIndex < entries.Count; leftIndex++)
            {
                var left = entries[leftIndex];
                if (!IsUsable(left.collider))
                {
                    continue;
                }

                for (var rightIndex = leftIndex + 1; rightIndex < entries.Count; rightIndex++)
                {
                    var right = entries[rightIndex];
                    if (!IsUsable(right.collider)
                        || left.collider.attachedRigidbody != null
                        && left.collider.attachedRigidbody == right.collider.attachedRigidbody
                        || !left.collider.bounds.Intersects(right.collider.bounds))
                    {
                        continue;
                    }

                    var distance = Physics2D.Distance(left.collider, right.collider);
                    if (!distance.isOverlapped)
                    {
                        continue;
                    }

                    detectedCount++;
                    if (output.Count >= maxOverlaps)
                    {
                        continue;
                    }

                    var direction = new Vector3(distance.normal.x, distance.normal.y, 0f);
                    var relativeVelocity = GetVelocity(right.collider.attachedRigidbody) - GetVelocity(left.collider.attachedRigidbody);
                    output.Add(new PhysicsOverlapInfo
                    {
                        dimension = "2d",
                        pathA = left.path,
                        colliderTypeA = left.collider.GetType().Name,
                        pathB = right.path,
                        colliderTypeB = right.collider.GetType().Name,
                        triggerOnly = left.collider.isTrigger || right.collider.isTrigger,
                        penetrationDepth = Math.Abs(distance.distance),
                        separationDirection = ToSceneVector3(direction),
                        relativeVelocity = ToSceneVector3(relativeVelocity),
                        relativeNormalSpeed = Math.Abs(Vector3.Dot(relativeVelocity, direction))
                    });
                }
            }
        }

        private static bool IsUsable(Collider collider)
        {
            return collider != null && collider.enabled && collider.gameObject.activeInHierarchy;
        }

        private static bool IsUsable(Collider2D collider)
        {
            return collider != null && collider.enabled && collider.gameObject.activeInHierarchy;
        }

        private static Vector3 GetVelocity(Rigidbody body)
        {
            return body != null ? body.linearVelocity : Vector3.zero;
        }

        private static Vector3 GetVelocity(Rigidbody2D body)
        {
            return body != null ? new Vector3(body.linearVelocity.x, body.linearVelocity.y, 0f) : Vector3.zero;
        }

        private static Vector3? ResolveRadiusCenter(SceneRadiusFilterInput filter)
        {
            if (filter == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(filter.centerPath))
            {
                var target = FindByPath(filter.centerPath.Trim().Replace('\\', '/'));
                if (target == null)
                {
                    throw new InvalidOperationException($"Physics radius centerPath was not found: {filter.centerPath}");
                }

                return target.transform.position;
            }

            if (filter.center == null)
            {
                throw new InvalidOperationException("Physics radius filter requires centerPath or center.");
            }

            return new Vector3(filter.center.x, filter.center.y, filter.center.z);
        }

        private static bool PathMatchesPrefix(string path, string rawPrefix)
        {
            var prefix = rawPrefix.Trim().TrimEnd('/').Replace('\\', '/');
            return string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDimension(string value)
        {
            var normalized = (value ?? "all").Trim().ToLowerInvariant();
            return normalized == "3d" || normalized == "2d" ? normalized : "all";
        }

        private static void PruneSamples(Dictionary<int, BodySample> samples)
        {
            var threshold = EditorApplication.timeSinceStartup - 30;
            var stale = new List<int>();
            foreach (var pair in samples)
            {
                if (pair.Value.timestamp < threshold)
                {
                    stale.Add(pair.Key);
                }
            }

            foreach (var key in stale)
            {
                samples.Remove(key);
            }
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

        private static string GetGameObjectPath(GameObject gameObject)
        {
            var names = new List<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static SceneVector3 ToSceneVector3(Vector2 value)
        {
            return ToSceneVector3(new Vector3(value.x, value.y, 0f));
        }

        private static SceneVector3 ToSceneVector3(Vector3 value)
        {
            return new SceneVector3 { x = value.x, y = value.y, z = value.z };
        }

        private static PhysicsInspectRequest ParseRequest(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new PhysicsInspectRequest();
            }

            try
            {
                return JsonUtility.FromJson<PhysicsInspectRequest>(requestBody) ?? new PhysicsInspectRequest();
            }
            catch
            {
                return new PhysicsInspectRequest();
            }
        }
    }
}
