using UnityEngine;

namespace UnityAI.ControlPlane.Runtime
{
    public sealed class ContinuousRotation : MonoBehaviour
    {
        public Vector3 axis = Vector3.up;
        public float degreesPerSecond = 45f;
        public bool localSpace = true;

        private void Update()
        {
            var normalizedAxis = axis.sqrMagnitude > 0.000001f ? axis.normalized : Vector3.up;
            transform.Rotate(
                normalizedAxis,
                degreesPerSecond * Time.deltaTime,
                localSpace ? Space.Self : Space.World);
        }
    }
}
