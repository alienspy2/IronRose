using System.Collections.Generic;

namespace RoseEngine
{
    public class MeshRenderer : Component
    {
        public Material? material { get; set; }
        public bool enabled { get; set; } = true;

        internal static readonly List<MeshRenderer> _allRenderers = new();

        internal override void OnAddedToGameObject()
        {
            _allRenderers.Add(this);
        }

        internal override void OnComponentDestroy() => _allRenderers.Remove(this);

        internal static void ClearAll() => _allRenderers.Clear();
    }
}
