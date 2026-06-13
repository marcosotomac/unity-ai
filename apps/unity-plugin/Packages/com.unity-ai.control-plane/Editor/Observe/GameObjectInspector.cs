using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class GameObjectInspectRequest
    {
        public GameObjectInspectInput input = new();
    }

    [Serializable]
    public sealed class GameObjectInspectInput
    {
        public string path;
        public bool includeProperties = true;
        public int maxProperties = 500;
        public int maxPropertyDepth = 8;
    }

    [Serializable]
    public sealed class SerializedPropertyInfo
    {
        public string path;
        public string displayName;
        public string type;
        public int depth;
        public bool isArray;
        public bool editable;
        public string value;
        public string objectReferencePath;
    }

    [Serializable]
    public sealed class ComponentInspectInfo
    {
        public int index;
        public int componentTypeIndex;
        public string typeName;
        public string fullTypeName;
        public string assemblyName;
        public string scriptPath;
        public bool missing;
        public int returnedPropertyCount;
        public bool propertiesTruncated;
        public string error;
        public SerializedPropertyInfo[] properties;
    }

    [Serializable]
    public sealed class GameObjectInspectReport
    {
        public string path;
        public bool found;
        public string name;
        public string tag;
        public int layer;
        public string layerName;
        public bool activeSelf;
        public bool activeInHierarchy;
        public bool isStatic;
        public SceneVector3 position;
        public SceneVector3 rotationEuler;
        public SceneVector3 scale;
        public int componentCount;
        public ComponentInspectInfo[] components;
        public string capturedAtUtc;
    }

    public static class GameObjectInspector
    {
        public static GameObjectInspectReport Inspect(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new GameObjectInspectInput();
            var path = (input.path ?? string.Empty).Trim().Replace('\\', '/');
            var target = FindByPath(path);
            var report = new GameObjectInspectReport
            {
                path = path,
                found = target != null,
                capturedAtUtc = DateTime.UtcNow.ToString("O"),
                components = Array.Empty<ComponentInspectInfo>()
            };

            if (target == null)
            {
                return report;
            }

            var maxProperties = input.maxProperties <= 0 ? 500 : Math.Min(input.maxProperties, 2000);
            var maxPropertyDepth = Math.Max(0, Math.Min(input.maxPropertyDepth, 20));
            var components = target.GetComponents<Component>();
            var inspectedComponents = new List<ComponentInspectInfo>();
            var typeCounts = new Dictionary<string, int>();
            var remainingProperties = maxProperties;

            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                var typeKey = component != null ? component.GetType().FullName ?? component.GetType().Name : "MissingComponent";
                typeCounts.TryGetValue(typeKey, out var componentTypeIndex);
                typeCounts[typeKey] = componentTypeIndex + 1;
                var info = InspectComponent(component, index, componentTypeIndex, input.includeProperties, maxPropertyDepth, ref remainingProperties);
                inspectedComponents.Add(info);
            }

            report.name = target.name;
            report.tag = target.tag;
            report.layer = target.layer;
            report.layerName = LayerMask.LayerToName(target.layer);
            report.activeSelf = target.activeSelf;
            report.activeInHierarchy = target.activeInHierarchy;
            report.isStatic = target.isStatic;
            report.position = ToSceneVector3(target.transform.localPosition);
            report.rotationEuler = ToSceneVector3(target.transform.localEulerAngles);
            report.scale = ToSceneVector3(target.transform.localScale);
            report.componentCount = components.Length;
            report.components = inspectedComponents.ToArray();
            return report;
        }

        private static ComponentInspectInfo InspectComponent(Component component, int index, int componentTypeIndex, bool includeProperties, int maxPropertyDepth, ref int remainingProperties)
        {
            if (component == null)
            {
                return new ComponentInspectInfo
                {
                    index = index,
                    componentTypeIndex = componentTypeIndex,
                    typeName = "MissingComponent",
                    fullTypeName = string.Empty,
                    assemblyName = string.Empty,
                    scriptPath = string.Empty,
                    missing = true,
                    properties = Array.Empty<SerializedPropertyInfo>()
                };
            }

            var type = component.GetType();
            var info = new ComponentInspectInfo
            {
                index = index,
                componentTypeIndex = componentTypeIndex,
                typeName = type.Name,
                fullTypeName = type.FullName ?? type.Name,
                assemblyName = type.Assembly.GetName().Name,
                scriptPath = GetScriptPath(component),
                missing = false,
                properties = Array.Empty<SerializedPropertyInfo>()
            };

            if (!includeProperties || remainingProperties <= 0)
            {
                info.propertiesTruncated = includeProperties && remainingProperties <= 0;
                return info;
            }

            try
            {
                var serializedObject = new SerializedObject(component);
                var iterator = serializedObject.GetIterator();
                var properties = new List<SerializedPropertyInfo>();

                while (remainingProperties > 0 && iterator.NextVisible(true))
                {
                    if (iterator.depth > maxPropertyDepth)
                    {
                        continue;
                    }

                    properties.Add(ToPropertyInfo(iterator));
                    remainingProperties--;
                }

                info.returnedPropertyCount = properties.Count;
                info.propertiesTruncated = remainingProperties <= 0;
                info.properties = properties.ToArray();
            }
            catch (Exception exception)
            {
                info.error = exception.Message;
            }

            return info;
        }

        private static SerializedPropertyInfo ToPropertyInfo(SerializedProperty property)
        {
            return new SerializedPropertyInfo
            {
                path = property.propertyPath,
                displayName = property.displayName,
                type = property.propertyType.ToString(),
                depth = property.depth,
                isArray = property.isArray,
                editable = property.editable,
                value = GetPropertyValue(property),
                objectReferencePath = GetObjectReferencePath(property)
            };
        }

        private static string GetPropertyValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                    return property.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return property.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.String:
                    return property.stringValue ?? string.Empty;
                case SerializedPropertyType.Color:
                    return FormatVector(property.colorValue.r, property.colorValue.g, property.colorValue.b, property.colorValue.a);
                case SerializedPropertyType.ObjectReference:
                    return property.objectReferenceValue != null ? property.objectReferenceValue.name : "null";
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length
                        ? property.enumDisplayNames[property.enumValueIndex]
                        : property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return FormatVector(property.vector2Value.x, property.vector2Value.y);
                case SerializedPropertyType.Vector3:
                    return FormatVector(property.vector3Value.x, property.vector3Value.y, property.vector3Value.z);
                case SerializedPropertyType.Vector4:
                    return FormatVector(property.vector4Value.x, property.vector4Value.y, property.vector4Value.z, property.vector4Value.w);
                case SerializedPropertyType.Rect:
                    return FormatVector(property.rectValue.x, property.rectValue.y, property.rectValue.width, property.rectValue.height);
                case SerializedPropertyType.ArraySize:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Character:
                    return ((char)property.intValue).ToString();
                case SerializedPropertyType.AnimationCurve:
                    return property.animationCurveValue != null ? $"keys:{property.animationCurveValue.length}" : "null";
                case SerializedPropertyType.Bounds:
                    var bounds = property.boundsValue;
                    return $"center={FormatVector(bounds.center.x, bounds.center.y, bounds.center.z)};size={FormatVector(bounds.size.x, bounds.size.y, bounds.size.z)}";
                case SerializedPropertyType.Quaternion:
                    var quaternion = property.quaternionValue;
                    return FormatVector(quaternion.x, quaternion.y, quaternion.z, quaternion.w);
                case SerializedPropertyType.ExposedReference:
                    return property.exposedReferenceValue != null ? property.exposedReferenceValue.name : "null";
                case SerializedPropertyType.FixedBufferSize:
                    return property.fixedBufferSize.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2Int:
                    return FormatVector(property.vector2IntValue.x, property.vector2IntValue.y);
                case SerializedPropertyType.Vector3Int:
                    return FormatVector(property.vector3IntValue.x, property.vector3IntValue.y, property.vector3IntValue.z);
                case SerializedPropertyType.RectInt:
                    var rect = property.rectIntValue;
                    return FormatVector(rect.x, rect.y, rect.width, rect.height);
                case SerializedPropertyType.BoundsInt:
                    var boundsInt = property.boundsIntValue;
                    return $"position={FormatVector(boundsInt.position.x, boundsInt.position.y, boundsInt.position.z)};size={FormatVector(boundsInt.size.x, boundsInt.size.y, boundsInt.size.z)}";
                case SerializedPropertyType.ManagedReference:
                    return property.managedReferenceFullTypename ?? "null";
                case SerializedPropertyType.Hash128:
                    return property.hash128Value.ToString();
                default:
                    return property.type ?? string.Empty;
            }
        }

        private static string GetObjectReferencePath(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.ObjectReference || property.objectReferenceValue == null)
            {
                return string.Empty;
            }

            var assetPath = AssetDatabase.GetAssetPath(property.objectReferenceValue);
            if (!string.IsNullOrEmpty(assetPath))
            {
                return assetPath;
            }

            if (property.objectReferenceValue is GameObject gameObject)
            {
                return GetGameObjectPath(gameObject);
            }

            if (property.objectReferenceValue is Component component)
            {
                return GetGameObjectPath(component.gameObject) + "#" + component.GetType().FullName;
            }

            return string.Empty;
        }

        private static string GetScriptPath(Component component)
        {
            if (!(component is MonoBehaviour monoBehaviour))
            {
                return string.Empty;
            }

            var script = MonoScript.FromMonoBehaviour(monoBehaviour);
            return script != null ? AssetDatabase.GetAssetPath(script) : string.Empty;
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var segments = path.Split('/');
            var roots = EditorSceneManager.GetActiveScene().GetRootGameObjects();
            GameObject current = null;

            foreach (var root in roots)
            {
                if (root.name == segments[0])
                {
                    current = root;
                    break;
                }
            }

            if (current == null)
            {
                return null;
            }

            for (var index = 1; index < segments.Length; index++)
            {
                var child = current.transform.Find(segments[index]);
                if (child == null)
                {
                    return null;
                }

                current = child.gameObject;
            }

            return current;
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
            return string.Join("/", names.ToArray());
        }

        private static SceneVector3 ToSceneVector3(Vector3 value)
        {
            return new SceneVector3 { x = value.x, y = value.y, z = value.z };
        }

        private static string FormatVector(params float[] values)
        {
            var formatted = new string[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                formatted[index] = values[index].ToString("R", CultureInfo.InvariantCulture);
            }

            return string.Join(",", formatted);
        }

        private static string FormatVector(params int[] values)
        {
            var formatted = new string[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                formatted[index] = values[index].ToString(CultureInfo.InvariantCulture);
            }

            return string.Join(",", formatted);
        }

        private static GameObjectInspectRequest ParseRequest(string requestBody)
        {
            try
            {
                return string.IsNullOrWhiteSpace(requestBody)
                    ? new GameObjectInspectRequest()
                    : JsonUtility.FromJson<GameObjectInspectRequest>(requestBody) ?? new GameObjectInspectRequest();
            }
            catch
            {
                return new GameObjectInspectRequest();
            }
        }
    }
}
