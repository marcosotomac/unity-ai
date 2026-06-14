using UnityEngine;

namespace UnityAI.ControlPlane.Runtime
{
    public sealed class BobbingMotion : MonoBehaviour
    {
        public Vector3 axis = Vector3.up;
        public float amplitude = 0.25f;
        public float frequency = 1f;
        public float phase;

        private Vector3 _origin;

        private void OnEnable()
        {
            _origin = transform.localPosition;
        }

        private void Update()
        {
            var normalizedAxis = axis.sqrMagnitude > 0.000001f ? axis.normalized : Vector3.up;
            var offset = Mathf.Sin((Time.time + phase) * Mathf.PI * 2f * frequency) * amplitude;
            transform.localPosition = _origin + normalizedAxis * offset;
        }
    }
}
