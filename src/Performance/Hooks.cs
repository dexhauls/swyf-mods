using UnityEngine;

namespace Performance
{
    /// <summary>Runs before every other script's Update.</summary>
    [DefaultExecutionOrder(-32000)]
    internal sealed class EarlyHook : MonoBehaviour
    {
        private void Update() => Plugin.Instance?.EarlyUpdate();
    }

    /// <summary>Runs after every other script's LateUpdate, right before rendering.</summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class LateHook : MonoBehaviour
    {
        private void LateUpdate() => Plugin.Instance?.VeryLateUpdate();
    }
}
