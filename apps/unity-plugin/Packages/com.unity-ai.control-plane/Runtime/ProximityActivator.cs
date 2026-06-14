using System;
using UnityEngine;

namespace UnityAI.ControlPlane.Runtime
{
    public enum ProximityTargetAction
    {
        Activate,
        Deactivate,
        Toggle
    }

    public sealed class ProximityActivator : MonoBehaviour
    {
        public Transform interactor;
        public string interactorTag = "Player";
        public float activationDistance = 2f;
        public GameObject[] targets = Array.Empty<GameObject>();
        public ProximityTargetAction action = ProximityTargetAction.Activate;
        public bool oneShot = true;
        public bool revertOnExit;

        private Transform _cachedInteractor;
        private bool _inside;
        private bool _triggered;

        public bool Triggered => _triggered;

        private void OnEnable()
        {
            _inside = false;
            _triggered = false;
        }

        private void Update()
        {
            var resolvedInteractor = ProximityInteractorResolver.Resolve(
                interactor,
                interactorTag,
                transform.position,
                ref _cachedInteractor);
            var inside = resolvedInteractor != null
                && Vector3.Distance(transform.position, resolvedInteractor.position) <= Mathf.Max(0.01f, activationDistance);

            if (inside && !_inside && (!_triggered || !oneShot))
            {
                Apply(action);
                _triggered = true;
            }
            else if (!inside && _inside && revertOnExit && !oneShot)
            {
                Apply(Inverse(action));
                _triggered = false;
            }

            _inside = inside;
        }

        private void Apply(ProximityTargetAction targetAction)
        {
            foreach (var target in targets ?? Array.Empty<GameObject>())
            {
                if (target == null)
                {
                    continue;
                }

                switch (targetAction)
                {
                    case ProximityTargetAction.Activate:
                        target.SetActive(true);
                        break;
                    case ProximityTargetAction.Deactivate:
                        target.SetActive(false);
                        break;
                    case ProximityTargetAction.Toggle:
                        target.SetActive(!target.activeSelf);
                        break;
                }
            }
        }

        private static ProximityTargetAction Inverse(ProximityTargetAction targetAction)
        {
            return targetAction switch
            {
                ProximityTargetAction.Activate => ProximityTargetAction.Deactivate,
                ProximityTargetAction.Deactivate => ProximityTargetAction.Activate,
                _ => ProximityTargetAction.Toggle
            };
        }
    }
}
