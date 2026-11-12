using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// A side panel of the game screen (Presenti, Diario, Zaino) with a title bar: a click folds it down to a small tab,
    /// dragging the bar moves it. Where it is and whether it is folded are kept in <see cref="GameSettings"/>, so they
    /// survive closing the game. Positions are fractions of the screen, so they hold at any resolution.
    /// </summary>
    public class DraggablePanel : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        static readonly List<DraggablePanel> s_All = new List<DraggablePanel>();

        string _key, _title;
        RectTransform _panel, _body;
        Text _label;
        Vector2 _openMin, _openMax, _foldedMin, _foldedMax;
        bool _open = true;
        /// <summary>How far the panel was moved from its place, in fractions of the screen.</summary>
        Vector2 _offset;

        /// <summary>Adds the title bar to <paramref name="panel"/>; <paramref name="foldedMin"/>/<paramref name="foldedMax"/> are the anchors of the small tab.</summary>
        public static void Make(RectTransform panel, RectTransform body, string title, Vector2 foldedMin, Vector2 foldedMax)
        {
            var header = UiKit.Button(panel, title + "Toggle", "", 24, null, new Color(0.1f, 0.12f, 0.18f, 0.9f));
            var hrt = (RectTransform)header.transform;
            hrt.anchorMin = new Vector2(0, 1); hrt.anchorMax = new Vector2(1, 1);
            hrt.pivot = new Vector2(0.5f, 1);
            hrt.offsetMin = new Vector2(0, -48); hrt.offsetMax = Vector2.zero;

            var p = header.gameObject.AddComponent<DraggablePanel>();
            p._key = title; p._title = title;
            p._panel = panel; p._body = body;
            p._label = header.GetComponentInChildren<Text>();
            p._openMin = panel.anchorMin; p._openMax = panel.anchorMax;
            p._foldedMin = foldedMin; p._foldedMax = foldedMax;
            // a click is a click only if the pointer did not move: the input module drops it once a drag starts
            header.onClick.AddListener(p.Toggle);
            s_All.Add(p);
            p.Load();
        }

        /// <summary>Back to the saved places (after "Riposiziona riquadri").</summary>
        public static void ReloadAll()
        {
            foreach (var p in s_All) if (p != null) p.Load();
        }

        /// <summary>Opens or folds the panel with this title (a button of the bar); false when there is none.</summary>
        public static bool Toggle(string title)
        {
            foreach (var p in s_All)
                if (p != null && p._title == title) { p.Toggle(); return true; }
            return false;
        }

        /// <summary>The panel with this title is open (not folded).</summary>
        public static bool IsOpen(string title)
        {
            foreach (var p in s_All)
                if (p != null && p._title == title) return p._open;
            return false;
        }

        void OnDestroy() => s_All.Remove(this);

        void Load()
        {
            _offset = GameSettings.PanelOffset(_key, out bool folded);
            _open = !folded;
            Apply();
        }

        void Toggle()
        {
            _open = !_open;
            Remember();
            Apply();
        }

        void Remember()
        {
            GameSettings.SetPanel(_key, _offset, !_open);
            GameSettings.SaveIfChanged();
        }

        Vector2 Min => _open ? _openMin : _foldedMin;
        Vector2 Max => _open ? _openMax : _foldedMax;

        /// <summary>The offset that keeps the whole panel (as it is now, open or folded) on screen.</summary>
        Vector2 Clamp(Vector2 o) => new Vector2(Mathf.Clamp(o.x, -Min.x, 1f - Max.x), Mathf.Clamp(o.y, -Min.y, 1f - Max.y));

        void Apply()
        {
            _body.gameObject.SetActive(_open);
            var o = Clamp(_offset);          // a panel moved while folded may not fit once opened: shown inside, the place is kept
            _panel.anchorMin = Min + o;
            _panel.anchorMax = Max + o;
            _panel.offsetMin = _panel.offsetMax = Vector2.zero;
            _label.text = (_open ? "[-] " : "[+] ") + "<color=#f2c75a>" + _title + "</color>";
        }

        public void OnBeginDrag(PointerEventData e) { _offset = Clamp(_offset); }

        public void OnDrag(PointerEventData e)
        {
            // the panels are anchored to the whole canvas, which covers the screen: pixels / screen size = anchor units
            _offset = Clamp(_offset + new Vector2(e.delta.x / Screen.width, e.delta.y / Screen.height));
            Apply();
        }

        public void OnEndDrag(PointerEventData e) => Remember();
    }
}
