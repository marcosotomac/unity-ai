using System;
using UnityEngine;

namespace UnityAI.ControlPlane.Runtime
{
    public enum PickupCollectAction
    {
        Deactivate,
        Destroy
    }

    public sealed class ProximityPickup : MonoBehaviour
    {
        public Transform interactor;
        public string interactorTag = "Player";
        public float activationDistance = 1.25f;
        public string pickupId = "pickup";
        public int value = 1;
        public PickupCollectAction collectAction = PickupCollectAction.Deactivate;
        public Vector3 spinAxis = Vector3.up;
        public float spinDegreesPerSecond = 90f;
        public float bobAmplitude = 0.15f;
        public float bobFrequency = 1f;

        private Transform _cachedInteractor;
        private Vector3 _origin;
        private bool _collected;

        public bool Collected => _collected;
        public static event Action<ProximityPickup> CollectedGlobally;

        private void OnEnable()
        {
            _origin = transform.localPosition;
            _collected = false;
        }

        private void Update()
        {
            if (_collected)
            {
                return;
            }

            var normalizedAxis = spinAxis.sqrMagnitude > 0.000001f ? spinAxis.normalized : Vector3.up;
            transform.Rotate(normalizedAxis, spinDegreesPerSecond * Time.deltaTime, Space.Self);
            transform.localPosition = _origin + Vector3.up * (
                Mathf.Sin(Time.time * Mathf.PI * 2f * Mathf.Max(0f, bobFrequency))
                * Mathf.Max(0f, bobAmplitude));

            var resolvedInteractor = ProximityInteractorResolver.Resolve(
                interactor,
                interactorTag,
                transform.position,
                ref _cachedInteractor);
            if (resolvedInteractor != null
                && Vector3.Distance(transform.position, resolvedInteractor.position) <= Mathf.Max(0.01f, activationDistance))
            {
                Collect();
            }
        }

        public void Collect()
        {
            if (_collected)
            {
                return;
            }

            _collected = true;
            CollectedGlobally?.Invoke(this);
            if (collectAction == PickupCollectAction.Destroy)
            {
                Destroy(gameObject);
            }
            else
            {
                gameObject.SetActive(false);
            }
        }
    }
}
