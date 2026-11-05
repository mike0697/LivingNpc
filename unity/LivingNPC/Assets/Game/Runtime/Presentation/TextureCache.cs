using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LivingNPC.Presentation
{
    /// <summary>Loads map images from plain files (so users/LLM tools can swap them) and keeps only what is in use.</summary>
    public static class TextureCache
    {
        static readonly Dictionary<string, Texture2D> s_Cache = new Dictionary<string, Texture2D>();

        public static Texture2D Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (s_Cache.TryGetValue(path, out var tex)) return tex;
            if (!File.Exists(path)) { s_Cache[path] = null; return null; }
            tex = ImageFile.Load(path, false);
            if (tex != null) tex.filterMode = FilterMode.Bilinear;
            s_Cache[path] = tex;
            return tex;
        }

        /// <summary>Destroys every cached texture whose path is not in <paramref name="keep"/>.</summary>
        public static void Trim(ICollection<string> keep)
        {
            var drop = new List<string>();
            foreach (var kv in s_Cache) if (!keep.Contains(kv.Key)) drop.Add(kv.Key);
            foreach (var k in drop)
            {
                if (s_Cache[k] != null) UiKit.Kill(s_Cache[k]);
                s_Cache.Remove(k);
            }
        }

        public static void Clear() => Trim(new string[0]);
    }
}
