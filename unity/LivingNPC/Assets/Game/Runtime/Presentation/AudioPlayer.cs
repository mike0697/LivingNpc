using System;
using System.Collections.Generic;
using System.IO;
using LivingNPC.Content;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// Plays the game's sounds from plain wav / ogg / mp3 files (like the images, so authors can swap them):
    /// one-shot effects, any number at once, and looping channels (the music, each ambient layer) that fade in and out.
    /// The simulation only says what should be heard; this class is the part that makes noise, so it can be replaced.
    /// Files load in the background: a sound starts a few frames after it was asked for the first time.
    /// </summary>
    public class AudioPlayer
    {
        class ClipEntry { public string Path; public AudioClip Clip; public UnityWebRequest Request; public bool Failed; }

        class Voice
        {
            public AudioSource Source;
            public CompiledSound Sound;
            public ClipEntry Entry;
            public float Level, Target, Speed;      // volume now, volume wanted, change per second
            public bool Playing;
            public bool Music;                      // else an ambient layer: each has its own volume in the settings
        }

        const float InstantSpeed = 1000f;

        /// <summary>Channel of the title screen's music (the game's loops use hashed ids, this one is out of their way).</summary>
        public const int MenuMusicChannel = int.MinValue + 7;

        static bool IsMusic(int channel) => channel == MenuMusicChannel || channel == Ids.MusicKey();

        readonly GameObject _root;
        readonly AudioSource _shots;
        readonly Dictionary<string, ClipEntry> _clips = new Dictionary<string, ClipEntry>();
        readonly Dictionary<int, Voice> _channels = new Dictionary<int, Voice>();
        readonly List<Voice> _dying = new List<Voice>();
        readonly List<(ClipEntry entry, float volume)> _waitingShots = new List<(ClipEntry, float)>();

        public AudioPlayer(Transform parent)
        {
            _root = new GameObject("LivingNPCAudio");
            _root.transform.SetParent(parent, false);
            // the game draws everything with uGUI and may have no camera at all, and without a listener nothing is heard
            if (Object.FindFirstObjectByType<AudioListener>() == null) _root.AddComponent<AudioListener>();
            _shots = _root.AddComponent<AudioSource>();
            _shots.playOnAwake = false;
            _shots.spatialBlend = 0;
        }

        // ------------------------------------------------------------------ what the game asks for

        /// <summary>Plays a sound effect once. Any number can overlap.</summary>
        public void PlayOnce(CompiledSound sound)
        {
            if (sound == null) return;
            var entry = Load(sound.Path);
            if (entry.Failed) return;
            if (entry.Clip != null) _shots.PlayOneShot(entry.Clip, sound.Volume);
            else _waitingShots.Add((entry, sound.Volume));
        }

        /// <summary>
        /// Makes <paramref name="channel"/> play <paramref name="sound"/> in a loop, or silent when null. Safe to call every refresh:
        /// the same file keeps playing untouched; another file crossfades in (using the new sound's fade), null fades the old one out.
        /// </summary>
        public void SetLoop(int channel, CompiledSound sound)
        {
            _channels.TryGetValue(channel, out var current);
            if (sound == null)
            {
                if (current != null) { Retire(current, current.Sound.Fade); _channels.Remove(channel); }
                return;
            }
            if (current != null && current.Sound.Path == sound.Path)
            {
                current.Sound = sound;
                current.Target = sound.Volume;
                current.Speed = Speed(sound.Volume, sound.Fade);
                return;
            }
            if (current != null) Retire(current, sound.Fade);
            var voice = NewVoice(sound);
            voice.Music = IsMusic(channel);
            _channels[channel] = voice;
        }

        /// <summary>Fades out the loops of channels the (reloaded) content no longer has.</summary>
        public void KeepOnly(ICollection<int> channels, float fade)
        {
            List<int> gone = null;
            foreach (var kv in _channels) if (!channels.Contains(kv.Key)) (gone ??= new List<int>()).Add(kv.Key);
            if (gone == null) return;
            foreach (int key in gone) { Retire(_channels[key], fade); _channels.Remove(key); }
        }

        /// <summary>Fades every loop out (back to the menu); nothing is left playing once they are silent.</summary>
        public void StopAll(float fade)
        {
            foreach (var v in _channels.Values) Retire(v, fade);
            _channels.Clear();
            _waitingShots.Clear();
        }

        /// <summary>The content was reloaded (F5): forget the files that nothing is playing so edited sounds are read again.</summary>
        public void Rebind()
        {
            var used = new HashSet<ClipEntry>();
            foreach (var v in _channels.Values) used.Add(v.Entry);
            foreach (var v in _dying) used.Add(v.Entry);
            var drop = new List<string>();
            foreach (var kv in _clips) if (!used.Contains(kv.Value)) drop.Add(kv.Key);
            foreach (var k in drop) { Release(_clips[k]); _clips.Remove(k); }
            _waitingShots.Clear();
        }

        // ------------------------------------------------------------------ per frame

        public void Update(float dt)
        {
            foreach (var e in _clips.Values) if (e.Request != null && e.Request.isDone) Finish(e);
            _shots.volume = GameSettings.Effects;          // scales every one-shot, also the ones already playing

            for (int i = 0; i < _waitingShots.Count; i++)
            {
                var (entry, volume) = _waitingShots[i];
                if (entry.Clip == null && !entry.Failed) continue;
                if (entry.Clip != null) _shots.PlayOneShot(entry.Clip, volume);
                _waitingShots.RemoveAt(i--);
            }

            foreach (var v in _channels.Values) Step(v, dt);
            for (int i = _dying.Count - 1; i >= 0; i--)
            {
                Step(_dying[i], dt);
                if (_dying[i].Level > 0) continue;
                UiKit.Kill(_dying[i].Source.gameObject);
                _dying.RemoveAt(i);
            }
        }

        public void Dispose()
        {
            foreach (var e in _clips.Values) Release(e);
            _clips.Clear(); _channels.Clear(); _dying.Clear(); _waitingShots.Clear();
            if (_root != null) UiKit.Kill(_root);
        }

        // ------------------------------------------------------------------ for tests and tools

        /// <summary>Path of the file the channel loops, null when it is silent.</summary>
        public string LoopPath(int channel) => _channels.TryGetValue(channel, out var v) ? v.Sound.Path : null;
        public int LoopCount => _channels.Count;

        // ------------------------------------------------------------------ internals

        static float Speed(float volume, float fade) => fade <= 0 ? InstantSpeed : Mathf.Max(volume, 0.01f) / fade;

        Voice NewVoice(CompiledSound sound)
        {
            var go = new GameObject("Loop");
            go.transform.SetParent(_root.transform, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false; src.loop = true; src.spatialBlend = 0; src.volume = 0;
            return new Voice
            {
                Source = src, Sound = sound, Entry = Load(sound.Path),
                Level = sound.Fade <= 0 ? sound.Volume : 0, Target = sound.Volume, Speed = Speed(sound.Volume, sound.Fade),
            };
        }

        void Retire(Voice v, float fade)
        {
            v.Target = 0;
            v.Speed = Speed(v.Sound.Volume, fade);
            _dying.Add(v);
        }

        void Step(Voice v, float dt)
        {
            if (!v.Playing && v.Entry.Clip != null)
            {
                v.Source.clip = v.Entry.Clip;
                v.Source.Play();
                v.Playing = true;
            }
            v.Level = Mathf.MoveTowards(v.Level, v.Target, v.Speed * dt);
            v.Source.volume = v.Level * (v.Music ? GameSettings.Music : GameSettings.Ambient);
        }

        static bool TypeOf(string path, out AudioType type)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".wav": type = AudioType.WAV; return true;
                case ".ogg": type = AudioType.OGGVORBIS; return true;
                case ".mp3": type = AudioType.MPEG; return true;
                default: type = AudioType.UNKNOWN; return false;
            }
        }

        ClipEntry Load(string path)
        {
            if (_clips.TryGetValue(path, out var entry)) return entry;
            entry = new ClipEntry { Path = path };
            _clips[path] = entry;
            if (!TypeOf(path, out var type) || !File.Exists(path))
            {
                entry.Failed = true;
                Debug.LogWarning($"[LivingNPC] sound not found or not supported: {path}");
                return entry;
            }
            entry.Request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type);
            entry.Request.SendWebRequest();
            return entry;
        }

        static void Finish(ClipEntry e)
        {
            var req = e.Request;
            e.Request = null;
            if (req.result == UnityWebRequest.Result.Success)
            {
                e.Clip = DownloadHandlerAudioClip.GetContent(req);
                if (e.Clip != null) e.Clip.name = Path.GetFileName(e.Path); else e.Failed = true;
            }
            else
            {
                e.Failed = true;
                Debug.LogWarning($"[LivingNPC] sound not loaded: {e.Path} ({req.error})");
            }
            req.Dispose();
        }

        static void Release(ClipEntry e)
        {
            if (e.Request != null) { e.Request.Dispose(); e.Request = null; }
            if (e.Clip != null) UiKit.Kill(e.Clip);
        }
    }
}
