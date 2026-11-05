using UnityEngine;
using UnityEngine.UI;

namespace LivingNPC.Presentation
{
    /// <summary>Makes a RawImage clickable only where its picture is not transparent: the character, not its bounding box.</summary>
    [RequireComponent(typeof(RawImage))]
    public class AlphaHit : MonoBehaviour, ICanvasRaycastFilter
    {
        public float MinAlpha = 0.1f;

        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera)
        {
            var raw = GetComponent<RawImage>();
            var tex = raw != null ? raw.texture as Texture2D : null;
            if (tex == null) return true;
            var rt = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screenPoint, eventCamera, out var local)) return false;
            var r = rt.rect;
            float u = (local.x - r.x) / r.width, v = (local.y - r.y) / r.height;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
            try { return tex.GetPixelBilinear(u, v).a >= MinAlpha; }
            catch (UnityException) { return true; }     // texture not readable: fall back to the whole rectangle
        }
    }
}
