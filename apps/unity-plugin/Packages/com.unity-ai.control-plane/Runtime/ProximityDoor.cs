using UnityEngine;

namespace UnityAI.ControlPlane.Runtime
{
    public sealed class ProximityDoor : MonoBehaviour
    {
        public Transform interactor;
        public string interactorTag = "Player";
        public float activationDistance = 2f;
        public float deactivationDistance = 2.5f;
        public Vector3 openLocalOffset = new(0f, 2.5f, 0f);
        public float movementSpeed = 3f;
        public bool startsOpen;
        public bool closeWhenOutOfRange = true;

        private Transform _cachedInteractor;
        private Vector3 _closedLocalPosition;
        private bool _isOpen;

        public bool IsOpen => _isOpen;

        private void OnEnable()
        {
            _closedLocalPosition = transform.localPosition;
            _isOpen = startsOpen;
            if (startsOpen)
            {
                transform.localPosition = _closedLocalPosition + openLocalOffset;
            }
        }

        private void Update()
        {
            var resolvedInteractor = ProximityInteractorResolver.Resolve(
                interactor,
                interactorTag,
                transform.position,
                ref _cachedInteractor);
            if (resolvedInteractor != null)
            {
                var distance = Vector3.Distance(transform.position, resolvedInteractor.position);
                if (_isOpen)
                {
                    if (closeWhenOutOfRange && distance > Mathf.Max(activationDistance, deactivationDistance))
                    {
                        _isOpen = false;
                    }
                }
                else if (distance <= Mathf.Max(0.01f, activationDistance))
                {
                    _isOpen = true;
                }
            }
            else if (closeWhenOutOfRange)
            {
                _isOpen = false;
            }

            var targetPosition = _closedLocalPosition + (_isOpen ? openLocalOffset : Vector3.zero);
            transform.localPosition = Vector3.MoveTowards(
                transform.localPosition,
                targetPosition,
                Mathf.Max(0.01f, movementSpeed) * Time.deltaTime);
        }
    }
}
