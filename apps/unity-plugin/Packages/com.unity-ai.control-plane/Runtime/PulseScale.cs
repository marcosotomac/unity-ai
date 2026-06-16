using UnityEngine;

namespace UnityAI.ControlPlane.Runtime
{
    public sealed class PulseScale : MonoBehaviour
    {
        public Vector3 multiplier = Vector3.one * 0.1f;
        public float frequency = 1f;
        public float phase;

        private Vector3 _baseScale;

        private void OnEnable()
        {
            _baseScale = transform.localScale;
        }

        private void Update()
        {
            var amount = Mathf.Sin((Time.time + phase) * Mathf.PI * 2f * frequency);
            transform.localScale = _baseScale + Vector3.Scale(_baseScale, multiplier) * amount;
        }
    }
}
