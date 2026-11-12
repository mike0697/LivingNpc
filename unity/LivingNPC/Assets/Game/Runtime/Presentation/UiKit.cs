using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace LivingNPC.Presentation
{
    /// <summary>Tiny code-only uGUI helpers (no prefabs or scene setup needed).</summary>
    public static class UiKit
    {
        static Font s_Font;
        public static Font Font => s_Font != null ? s_Font : (s_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

        public static readonly Color PanelColor = new Color(0.05f, 0.06f, 0.09f, 0.78f);
        public static readonly Color ButtonColor = new Color(0.14f, 0.17f, 0.24f, 0.92f);
        public static readonly Color AccentColor = new Color(0.95f, 0.78f, 0.35f, 1f);

        public static RectTransform Rect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        public static RectTransform Panel(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Color? color = null)
        {
            var rt = Rect(parent, name, anchorMin, anchorMax);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color ?? PanelColor;
            return rt;
        }

        public static Text Label(Transform parent, string name, int size, TextAnchor align, Vector2 anchorMin, Vector2 anchorMax)
        {
            var rt = Rect(parent, name, anchorMin, anchorMax);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font; t.fontSize = size; t.alignment = align; t.color = Color.white;
            t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.raycastTarget = false;
            return t;
        }

        public static Button Button(Transform parent, string name, string label, int size, UnityAction onClick, Color? color = null)
        {
            var rt = Rect(parent, name, Vector2.zero, Vector2.one);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color ?? ButtonColor;
            var b = rt.gameObject.AddComponent<Button>();
            var colors = b.colors;
            colors.highlightedColor = new Color(1.3f, 1.3f, 1.3f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            b.colors = colors;
            b.targetGraphic = img;
            if (onClick != null) b.onClick.AddListener(onClick);
            var t = Label(rt, "Text", size, TextAnchor.MiddleCenter, Vector2.zero, Vector2.one);
            t.text = label;
            t.resizeTextForBestFit = true; t.resizeTextMinSize = 10; t.resizeTextMaxSize = size;
            var skinned = rt.gameObject.AddComponent<SkinnedButton>();
            skinned.BaseColor = img.color;
            skinned.BaseTextColor = t.color;
            if (Skin != null) skinned.Apply();
            return b;
        }

        /// <summary>
        /// A vertical list that scrolls with the wheel or by dragging: returns its content (a VerticalLayoutGroup that grows
        /// with its children, which need a LayoutElement or a preferred height of their own).
        /// </summary>
        public static RectTransform Scroll(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, out ScrollRect scroll, float spacing = 6)
        {
            var view = Rect(parent, name, anchorMin, anchorMax);
            view.gameObject.AddComponent<RectMask2D>();
            view.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0);       // catches the wheel between the rows
            var content = Rect(view, "Content", new Vector2(0, 1), new Vector2(1, 1));
            content.pivot = new Vector2(0.5f, 1);
            var v = content.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing; v.childControlHeight = true; v.childControlWidth = true; v.childForceExpandHeight = false; v.childForceExpandWidth = true;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.content = content; scroll.viewport = view;
            scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 40;
            return content;
        }

        /// <summary>The pictures every button of the game uses (title.json "buttons"), or null for the plain coloured ones.</summary>
        public static ButtonSkin Skin { get; private set; }

        /// <summary>Changes the pictures of all the buttons, the existing ones (also hidden) and the ones made later.</summary>
        public static void SetSkin(ButtonSkin skin)
        {
            var old = Skin;
            Skin = skin;
            foreach (var b in Object.FindObjectsByType<SkinnedButton>(FindObjectsInactive.Include, FindObjectsSortMode.None)) b.Apply();
            if (old != null && old != skin) old.Dispose();
        }

        public static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) Kill(t.GetChild(i).gameObject);
        }

        /// <summary>Destroy that also works outside play mode (headless smoke test).</summary>
        public static void Kill(Object o)
        {
            if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o);
        }
    }

    /// <summary>
    /// The two pictures of a button (normal; hover / pressed / selected) read from plain files, with their sprites. It owns
    /// its textures (not the shared TextureCache, which the map view trims), so <see cref="Dispose"/> frees them.
    /// </summary>
    public sealed class ButtonSkin
    {
        public Texture2D NormalTexture, HoverTexture;
        public Sprite Normal, Hover;
        public int Border;
        public Color TextColor = Color.white;

        /// <summary>Null when there is no normal picture or it cannot be read.</summary>
        public static ButtonSkin Load(LivingNPC.Content.ButtonImages images)
        {
            if (images == null) return null;
            var normal = Read(images.Normal);
            if (normal == null) return null;
            var hover = images.Hover == images.Normal ? null : Read(images.Hover);
            var skin = new ButtonSkin { NormalTexture = normal, HoverTexture = hover ?? normal, TextColor = images.TextColor ?? Color.white };
            // a 9-slice edge can never be more than half the picture
            skin.Border = EdgeFor(normal, images.Border);
            skin.Normal = MakeSprite(skin.NormalTexture, skin.Border);
            skin.Hover = skin.HoverTexture == normal ? skin.Normal : MakeSprite(skin.HoverTexture, EdgeFor(skin.HoverTexture, images.Border));
            return skin;
        }

        /// <summary>The ends cut from the picture: at most half its width.</summary>
        static int EdgeFor(Texture2D tex, int border) => Mathf.Max(0, Mathf.Min(border, tex.width / 2 - 1));

        static Texture2D Read(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            var tex = ImageFile.Load(path, true);
            if (tex == null)
            {
                Debug.LogWarning($"[LivingNPC] picture not readable: {path}");
                return null;
            }
            // Buttons are drawn much smaller than their picture: the transparent pixels around the shape usually hold
            // white (or black), which filtering blends into the edge as a 1-2 px halo. Spread the edge colour into them
            // (what Unity's importer does with "Alpha Is Transparency") and build mipmaps so shrinking stays smooth.
            var px = tex.GetPixels32();
            BleedAlpha(px, tex.width, tex.height);
            tex.SetPixels32(px);
            tex.filterMode = FilterMode.Trilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 1;
            tex.Apply(true, false);
            return tex;
        }

        /// <summary>
        /// Gives every fully transparent pixel the average colour of its nearest visible neighbours (alpha stays 0),
        /// a ring at a time, so neither filtering nor mipmaps ever mix in the colour hidden under the transparency.
        /// </summary>
        public static void BleedAlpha(Color32[] px, int w, int h, int passes = 16)
        {
            var done = new bool[px.Length];
            for (int i = 0; i < px.Length; i++) done[i] = px[i].a > 0;
            var next = new System.Collections.Generic.List<int>();
            for (int pass = 0; pass < passes; pass++)
            {
                next.Clear();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (done[i]) continue;
                        int r = 0, g = 0, b = 0, n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                int j = ny * w + nx;
                                if (!done[j]) continue;
                                r += px[j].r; g += px[j].g; b += px[j].b; n++;
                            }
                        if (n == 0) continue;
                        px[i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 0);
                        next.Add(i);
                    }
                if (next.Count == 0) break;
                foreach (int i in next) done[i] = true;        // a ring at a time: only colours from the previous ring are read
            }
        }

        /// <summary>
        /// Only the left and right ends are kept whole (3-slice): the picture is scaled to the button's height as a
        /// whole, so its bottom edge stays at the bottom, and the middle stretches sideways (see SkinnedButton).
        /// </summary>
        static Sprite MakeSprite(Texture2D tex, int border)
            => Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(border, 0, border, 0));

        public void Dispose()
        {
            if (Hover != null && Hover != Normal) UiKit.Kill(Hover);
            if (Normal != null) UiKit.Kill(Normal);
            if (HoverTexture != null && HoverTexture != NormalTexture) UiKit.Kill(HoverTexture);
            if (NormalTexture != null) UiKit.Kill(NormalTexture);
            Normal = Hover = null; NormalTexture = HoverTexture = null;
        }
    }
}
