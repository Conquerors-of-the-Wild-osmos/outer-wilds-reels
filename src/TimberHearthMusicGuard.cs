using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CustomReels
{
    // A scene-wide, track-specific guard: music can start after spawn settling,
    // and its source need not be parented to TimberHearth_Body.
    internal sealed class TimberHearthMusicGuard
    {
        private readonly Dictionary<AudioSource, bool> _originalMute = new Dictionary<AudioSource, bool>();
        private readonly Dictionary<AudioSource, AudioClip> _reportedClips = new Dictionary<AudioSource, AudioClip>();
        private readonly HashSet<AudioClip> _themeClips = new HashSet<AudioClip>();
        private OWAudioSource[] _musicSources = Array.Empty<OWAudioSource>();
        private AudioSource[] _plainThemeSources = Array.Empty<AudioSource>();
        private Transform _ring;
        private Transform _interior;
        private Action<string> _log;
        private float _nextScan;
        private bool _inside;

        internal void Begin(Transform ring, Action<string> log)
        {
            Reset();
            _ring = ring;
            _interior = ring.Find("Sector_RingInterior");
            _log = log;
        }

        internal void Tick()
        {
            if (_ring == null || _interior == null) return;
            try
            {
                var player = Locator.GetPlayerBody();
                var sectors = Locator.GetPlayerSectorDetector();
                // Bound the sector test geographically too: a stale sector entry
                // after another mod's teleport must not silence Timber Hearth.
                var inside = player != null && sectors != null && sectors._sectorList != null
                    && Vector3.Distance(player.GetPosition(), _ring.position) < 1000f
                    && sectors._sectorList.Any(s => s != null && s.transform.IsChildOf(_interior));
                if (!inside)
                {
                    RestoreAll();
                    if (_inside) _log("Music guard: left Ring interior; source mute states restored.");
                    _inside = false;
                    _reportedClips.Clear();
                    return;
                }
                if (!_inside) _log("Music guard: active in Ring interior; target=TH_Village.");
                _inside = true;
                if (Time.realtimeSinceStartup >= _nextScan)
                {
                    _nextScan = Time.realtimeSinceStartup + 2f;
                    RefreshSources();
                }

                var targets = new HashSet<AudioSource>(_plainThemeSources.Where(s => s != null && IsThemeClip(s.clip)));
                foreach (var source in _musicSources)
                {
                    if (source == null || source._audioSource == null) continue;
                    if (source.audioLibraryClip == AudioType.TH_Village || IsThemeClip(source.clip))
                        targets.Add(source._audioSource);
                }
                // A shared source may switch to a different track during the loop.
                foreach (var source in _originalMute.Keys.Where(s => s == null || !targets.Contains(s)).ToArray())
                    Restore(source);
                foreach (var source in targets)
                {
                    if (!_originalMute.ContainsKey(source))
                    {
                        _originalMute.Add(source, source.mute);
                        _log($"Music guard muted: type=TH_Village, clip={source.clip?.name}, source={Path(source.transform)}");
                    }
                    source.mute = true;
                }
                foreach (var source in _musicSources)
                    if (source != null && source._audioSource != null)
                        ReportPlaying(source._audioSource, source.audioLibraryClip.ToString());
                foreach (var source in _plainThemeSources)
                    if (source != null) ReportPlaying(source, "TH_Village clip");
            }
            catch (Exception error)
            {
                _log("Music guard failed: " + error);
                Reset();
            }
        }

        private void RefreshSources()
        {
            var manager = UnityEngine.Object.FindObjectOfType<AudioManager>();
            var clips = manager != null ? manager.GetAudioClipArray(AudioType.TH_Village) : null;
            if (clips != null)
                foreach (var clip in clips)
                    if (clip != null) _themeClips.Add(clip);
            _musicSources = Resources.FindObjectsOfTypeAll<OWAudioSource>()
                .Where(s => s.gameObject.scene.IsValid() && s.gameObject.scene.isLoaded
                    && (s.GetTrack() == OWAudioMixer.TrackName.Music
                        || s.GetTrack() == OWAudioMixer.TrackName.EndTimes_Music
                        || s.GetTrack() == OWAudioMixer.TrackName.SlideReelMusic
                        || s.audioLibraryClip == AudioType.TH_Village || IsThemeClip(s._audioSource?.clip))).ToArray();
            // Also cover sources that play the library clip directly without OWAudioSource.
            _plainThemeSources = Resources.FindObjectsOfTypeAll<AudioSource>()
                .Where(s => s.gameObject.scene.IsValid() && s.gameObject.scene.isLoaded && IsThemeClip(s.clip)).ToArray();
        }

        private bool IsThemeClip(AudioClip clip) => clip != null && _themeClips.Contains(clip);

        private void ReportPlaying(AudioSource source, string type)
        {
            if (!source.isPlaying)
            {
                _reportedClips.Remove(source);
                return;
            }
            if (_reportedClips.TryGetValue(source, out var previous) && previous == source.clip) return;
            _reportedClips[source] = source.clip;
            _log($"Music playing: type={type}, clip={source.clip?.name}, muted={source.mute}, source={Path(source.transform)}");
        }

        private static string Path(Transform node)
        {
            var path = node.name;
            while (node.parent != null)
            {
                node = node.parent;
                path = node.name + "/" + path;
            }
            return path;
        }

        private void Restore(AudioSource source)
        {
            if (source != null) source.mute = _originalMute[source];
            _originalMute.Remove(source);
        }

        private void RestoreAll()
        {
            foreach (var source in _originalMute.Keys.ToArray()) Restore(source);
        }

        internal void Reset()
        {
            RestoreAll();
            _reportedClips.Clear();
            _themeClips.Clear();
            _musicSources = Array.Empty<OWAudioSource>();
            _plainThemeSources = Array.Empty<AudioSource>();
            _ring = null;
            _interior = null;
            _inside = false;
            _nextScan = 0;
        }
    }
}
