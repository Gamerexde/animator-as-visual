#if UNITY_EDITOR
using UnityEngine;
using VRC.SDKBase;

namespace pi.AnimatorAsVisual
{
    public abstract class AavGeneratorHook : MonoBehaviour, IEditorOnly
    {
        public abstract void PreApply(GameObject avatar, AavGenerator generator);
        public abstract void Apply(GameObject avatar, AavGenerator generator);

        string Name { get; }
    }
}
#endif