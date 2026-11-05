using UnityEngine;
using UnityEngine.UI;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// Marks a button made by <see cref="UiKit.Button"/>: it takes the pictures of the current <see cref="UiKit.Skin"/>
    /// (title.json "buttons"), and goes back to its plain colour when there is none. Clickable pictures (characters on
    /// the map) are not marked and never change.
    /// </summary>
    public class SkinnedButton : MonoBehaviour
    {
        public Color BaseColor;
        public Color BaseTextColor = Color.white;

        public void Apply()
        {
            var b = GetComponent<Button>();
            var img = GetComponent<Image>();
            if (b == null || img == null) return;
            var skin = UiKit.Skin;
            var text = GetComponentInChildren<Text>(true);
            if (skin == null)
            {
                img.sprite = null;
                img.type = Image.Type.Simple;
                img.pixelsPerUnitMultiplier = 1;
                img.color = BaseColor;
                b.transition = Selectable.Transition.ColorTint;
                if (text != null) text.color = BaseTextColor;
                return;
            }
            if (text != null) text.color = skin.TextColor;
            img.sprite = skin.Normal;
            img.type = skin.Border > 0 ? Image.Type.Sliced : Image.Type.Simple;
            FitEnds(img, skin);
            img.color = Color.white;
            b.transition = Selectable.Transition.SpriteSwap;
            b.spriteState = new SpriteState { highlightedSprite = skin.Hover, pressedSprite = skin.Hover, selectedSprite = skin.Hover, disabledSprite = skin.Normal };
            _shownEnabled = null;
        }

        /// <summary>
        /// The ends of the picture grow and shrink with the button's height, as if the whole picture were scaled to that
        /// height: a sliced Image draws its border at border / pixelsPerUnit, so pixelsPerUnit = picture height / button height.
        /// </summary>
        static void FitEnds(Image img, ButtonSkin skin)
        {
            float h = ((RectTransform)img.transform).rect.height;
            if (skin.Border <= 0 || h <= 0.5f || skin.NormalTexture == null) { img.pixelsPerUnitMultiplier = 1; return; }
            img.pixelsPerUnitMultiplier = Mathf.Max(0.01f, skin.NormalTexture.height / h);
        }

        void OnRectTransformDimensionsChange()
        {
            var skin = UiKit.Skin;
            var img = GetComponent<Image>();
            if (skin != null && img != null && img.sprite == skin.Normal) FitEnds(img, skin);
        }

        static readonly Color s_Disabled = new Color(0.6f, 0.6f, 0.6f, 0.65f);
        bool? _shownEnabled;

        /// <summary>A sprite swap has no tint, so a button that cannot be pressed (during a dialogue) is greyed here.</summary>
        void LateUpdate()
        {
            if (UiKit.Skin == null) return;
            var b = GetComponent<Button>();
            if (b == null) return;
            bool on = b.IsInteractable();
            if (_shownEnabled == on) return;
            _shownEnabled = on;
            var img = GetComponent<Image>();
            if (img != null) img.color = on ? Color.white : s_Disabled;
        }
    }
}
