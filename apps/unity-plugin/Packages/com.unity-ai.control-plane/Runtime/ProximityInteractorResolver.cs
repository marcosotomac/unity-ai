using UnityEngine;

namespace UnityAI.ControlPlane.Runtime
{
    internal static class ProximityInteractorResolver
    {
        public static Transform Resolve(
            Transform explicitInteractor,
            string tag,
            Vector3 origin,
            ref Transform cachedInteractor)
        {
            if (explicitInteractor != null && explicitInteractor.gameObject.activeInHierarchy)
            {
                return explicitInteractor;
            }

            if (cachedInteractor != null && cachedInteractor.gameObject.activeInHierarchy)
            {
                return cachedInteractor;
            }

            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            try
            {
                Transform nearest = null;
                var nearestDistance = float.PositiveInfinity;
                foreach (var candidate in GameObject.FindGameObjectsWithTag(tag.Trim()))
                {
                    var distance = (candidate.transform.position - origin).sqrMagnitude;
                    if (distance >= nearestDistance)
                    {
                        continue;
                    }

                    nearest = candidate.transform;
                    nearestDistance = distance;
                }

                cachedInteractor = nearest;
                return nearest;
            }
            catch (UnityException)
            {
                return null;
            }
        }
    }
}
