using System;
using System.IO;
using UnityEngine;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// Reads a picture file into a readable RGBA texture: PNG and JPG through Unity, WebP through libwebp (package com.netpyoung.webp).
    /// The format comes from the file's bytes, not its name, so a renamed file still loads.
    /// </summary>
    public static class ImageFile
    {
        public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp" };

        /// <summary>The texture, or null when the file cannot be decoded.</summary>
        public static Texture2D Load(string path, bool mipmaps)
        {
            var data = File.ReadAllBytes(path);
            Texture2D tex;
            if (IsWebP(data))
            {
                try
                {
                    // decoded without mipmaps (the package sizes its mip buffer by 4/3, too small for very thin pictures),
                    // then the chain is built by Apply
                    WebP.Texture2DExt.GetWebPDimensions(data, out int w, out int h);
                    var rgba = WebP.Texture2DExt.LoadRGBAFromWebP(data, ref w, ref h, false, out var err);
                    if (err != WebP.Error.Success || rgba == null) return null;
                    tex = new Texture2D(w, h, TextureFormat.RGBA32, mipmaps);
                    tex.SetPixelData(rgba, 0);
                    tex.Apply(mipmaps, false);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[LivingNPC] WebP picture not readable: {path} ({e.Message})");
                    return null;
                }
            }
            else
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipmaps);
                if (!tex.LoadImage(data, false)) { UiKit.Kill(tex); tex = null; }
            }
            if (tex != null) tex.name = Path.GetFileName(path);
            return tex;
        }

        /// <summary>"RIFF" ....  "WEBP"</summary>
        static bool IsWebP(byte[] d) =>
            d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P';
    }
}
