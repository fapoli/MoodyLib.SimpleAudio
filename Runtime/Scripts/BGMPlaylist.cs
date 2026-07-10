using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace MoodyLib.SimpleAudio {

    [Serializable]
    public class BGMTrack {
        public string label;
        public AudioClip clip;
        public string title;
        public string author;
    }

    public class BGMPlaylist : MonoBehaviour {
        public BGMTrack currentTrack => IsValidTrackIndex(_currentTrackIndex) ? tracks[_currentTrackIndex] : null;
        public bool isMuted => _isMuted;
        public bool isTransitioning => _trackTransitionCoroutine != null;

        [Header("Playlist")]
        public List<BGMTrack> tracks = new List<BGMTrack>();
        public bool randomOrder;
        public bool persistBetweenScenes;

        [Header("Fade")]
        public float fadeDuration = 0.5f;
        public float skipFadeDuration = 0.25f;
        public float muteFadeDuration = 0.25f;
        public float startupFadeDuration = 1f;
        [Range(0f, 1f)]
        public float volumeScale = 1f;

        private AudioSource _audioSourceA;
        private AudioSource _audioSourceB;
        private AudioSource _activeAudioSource;
        private AudioSource _inactiveAudioSource;
        private Coroutine _trackTransitionCoroutine;
        private Coroutine _masterVolumeCoroutine;
        private int _currentTrackIndex = -1;
        private int _lastRandomTrackIndex = -1;
        private float _activeBlend = 1f;
        private float _inactiveBlend;
        private float _masterVolume = 1f;
        private bool _isMuted;

        private void Awake() {
            var existingPlaylists = FindObjectsByType<BGMPlaylist>();
            if (existingPlaylists.Length > 1) {
                foreach (var existing in existingPlaylists) {
                    if (existing == this) continue;
                    existing.ReplacePlaylist(this);
                    break;
                }

                Destroy(gameObject);
                return;
            }

            if (persistBetweenScenes) {
                DontDestroyOnLoad(gameObject);
            }
        }

        /// <summary>
        /// Takes over this (surviving, persisted) instance's playlist config from a newly-loaded scene's
        /// BGMPlaylist right before that one gets destroyed as a duplicate, and starts playing from it -
        /// so a new scene's music actually takes effect instead of being silently discarded.
        /// </summary>
        private void ReplacePlaylist(BGMPlaylist source) {
            tracks = source.tracks;
            randomOrder = source.randomOrder;
            fadeDuration = source.fadeDuration;
            skipFadeDuration = source.skipFadeDuration;
            muteFadeDuration = source.muteFadeDuration;
            startupFadeDuration = source.startupFadeDuration;
            volumeScale = source.volumeScale;

            _currentTrackIndex = -1;
            _lastRandomTrackIndex = -1;

            if (!HasPlayableTracks()) {
                Debug.LogWarning("BGMPlaylist: No playable tracks assigned to the replacement playlist.");
                return;
            }

            var nextTrackIndex = GetNextTrackIndex();
            if (nextTrackIndex >= 0) {
                TransitionToTrack(nextTrackIndex, fadeDuration);
            }
        }

        private void Start() {
            if (!HasPlayableTracks()) {
                Debug.LogWarning("BGMPlaylist: No playable tracks assigned to the playlist.");
                enabled = false;
                return;
            }

            EnsureAudioSources();

            _activeAudioSource = _audioSourceA;
            _inactiveAudioSource = _audioSourceB;
            _activeBlend = 1f;
            _inactiveBlend = 0f;
            ApplyVolumes();

            var firstTrackIndex = GetNextTrackIndex();
            if (firstTrackIndex >= 0) {
                _masterVolume = 0f;
                StartTrackImmediately(firstTrackIndex);
                StartMasterVolumeFade(1f, startupFadeDuration);
            }
        }

        private void Update() {
            if (_activeAudioSource == null || isTransitioning)
                return;

            if (_activeAudioSource.clip == null || !_activeAudioSource.isPlaying) {
                var nextTrackIndex = GetNextTrackIndex();
                if (nextTrackIndex >= 0) {
                    StartTrackImmediately(nextTrackIndex);
                }

                return;
            }

            var safeFadeDuration = Mathf.Max(0.01f, fadeDuration);
            var crossFadeStartTime = Mathf.Max(0f, _activeAudioSource.clip.length - safeFadeDuration);
            if (_activeAudioSource.time >= crossFadeStartTime) {
                TransitionToTrack(GetNextTrackIndex(), safeFadeDuration);
            }
        }

        public void Skip() {
            Skip(skipFadeDuration);
        }

        public void Skip(float fadeDurationOverride) {
            TransitionToTrack(GetNextTrackIndex(), fadeDurationOverride);
        }

        public void Play(string label) {
            Play(label, fadeDuration);
        }

        public void Play(string label, float fadeDurationOverride) {
            if (string.IsNullOrWhiteSpace(label)) {
                Debug.LogWarning("BGMPlaylist: Play(label) was called with an empty label.");
                return;
            }

            var trackIndex = FindTrackIndexByLabel(label);
            if (trackIndex < 0) {
                Debug.LogWarning($"BGMPlaylist: No track found for label '{label}'.");
                return;
            }

            if (_activeAudioSource == null || _activeAudioSource.clip == null || !_activeAudioSource.isPlaying) {
                StartTrackImmediately(trackIndex);
                return;
            }

            TransitionToTrack(trackIndex, fadeDurationOverride);
        }

        public void ToggleMute() {
            SetMuted(!_isMuted);
        }

        public void SetMuted(bool muted) {
            _isMuted = muted;
            StartMasterVolumeFade(muted ? 0f : 1f, muteFadeDuration);
        }

        private void StartTrackImmediately(int trackIndex) {
            if (!IsValidTrackIndex(trackIndex) || _activeAudioSource == null || _inactiveAudioSource == null)
                return;

            if (_trackTransitionCoroutine != null) {
                StopCoroutine(_trackTransitionCoroutine);
                _trackTransitionCoroutine = null;
            }

            var track = tracks[trackIndex];

            _activeAudioSource.Stop();
            _activeAudioSource.clip = track.clip;
            _activeAudioSource.time = 0f;
            _activeAudioSource.Play();

            _inactiveAudioSource.Stop();
            _inactiveAudioSource.clip = null;

            _currentTrackIndex = trackIndex;
            _lastRandomTrackIndex = trackIndex;
            _activeBlend = 1f;
            _inactiveBlend = 0f;
            ApplyVolumes();
        }

        private void TransitionToTrack(int trackIndex, float duration) {
            if (!IsValidTrackIndex(trackIndex) || _activeAudioSource == null || _inactiveAudioSource == null)
                return;

            if (isTransitioning)
                return;

            if (_activeAudioSource.clip == null || !_activeAudioSource.isPlaying) {
                StartTrackImmediately(trackIndex);
                return;
            }

            var safeDuration = Mathf.Max(0.01f, duration);
            _trackTransitionCoroutine = StartCoroutine(TrackTransitionRoutine(trackIndex, safeDuration));
        }

        private IEnumerator TrackTransitionRoutine(int trackIndex, float duration) {
            var from = _activeAudioSource;
            var to = _inactiveAudioSource;
            var targetTrack = tracks[trackIndex];

            to.Stop();
            to.clip = targetTrack.clip;
            to.time = 0f;
            to.Play();

            var startFromBlend = _activeBlend;
            var elapsed = 0f;

            while (elapsed < duration) {
                var t = elapsed / duration;
                _activeBlend = Mathf.Lerp(startFromBlend, 0f, t);
                _inactiveBlend = Mathf.Lerp(0f, 1f, t);
                ApplyVolumes();

                elapsed += Time.deltaTime;
                yield return null;
            }

            from.Stop();
            from.clip = null;

            _activeAudioSource = to;
            _inactiveAudioSource = from;
            _currentTrackIndex = trackIndex;
            _lastRandomTrackIndex = trackIndex;
            _activeBlend = 1f;
            _inactiveBlend = 0f;
            ApplyVolumes();

            _trackTransitionCoroutine = null;
        }

        private void StartMasterVolumeFade(float targetVolume, float duration) {
            if (_masterVolumeCoroutine != null) {
                StopCoroutine(_masterVolumeCoroutine);
            }

            var safeDuration = Mathf.Max(0.01f, duration);
            _masterVolumeCoroutine = StartCoroutine(MasterVolumeFadeRoutine(targetVolume, safeDuration));
        }

        private IEnumerator MasterVolumeFadeRoutine(float targetVolume, float duration) {
            var startVolume = _masterVolume;
            var elapsed = 0f;

            while (elapsed < duration) {
                var t = elapsed / duration;
                _masterVolume = Mathf.Lerp(startVolume, targetVolume, t);
                ApplyVolumes();

                elapsed += Time.deltaTime;
                yield return null;
            }

            _masterVolume = targetVolume;
            ApplyVolumes();
            _masterVolumeCoroutine = null;
        }

        private void ApplyVolumes() {
            if (_activeAudioSource == null || _inactiveAudioSource == null)
                return;

            _activeAudioSource.volume = _activeBlend * volumeScale * _masterVolume;
            _inactiveAudioSource.volume = _inactiveBlend * volumeScale * _masterVolume;
        }

        private int GetNextTrackIndex() {
            if (!HasPlayableTracks())
                return -1;

            if (randomOrder)
                return GetRandomTrackIndex();

            var nextIndex = _currentTrackIndex + 1;
            if (nextIndex >= tracks.Count)
                nextIndex = 0;

            return FindNextPlayableIndex(nextIndex);
        }

        private int GetRandomTrackIndex() {
            var playableIndexes = new List<int>();
            for (var i = 0; i < tracks.Count; i++) {
                if (IsValidTrackIndex(i))
                    playableIndexes.Add(i);
            }

            if (playableIndexes.Count == 0)
                return -1;

            if (playableIndexes.Count == 1)
                return playableIndexes[0];

            int nextIndex;
            do {
                nextIndex = playableIndexes[Random.Range(0, playableIndexes.Count)];
            }
            while (nextIndex == _lastRandomTrackIndex);

            return nextIndex;
        }

        private int FindNextPlayableIndex(int startIndex) {
            for (var offset = 0; offset < tracks.Count; offset++) {
                var index = (startIndex + offset) % tracks.Count;
                if (IsValidTrackIndex(index))
                    return index;
            }

            return -1;
        }

        private int FindTrackIndexByLabel(string label) {
            for (var i = 0; i < tracks.Count; i++) {
                var track = tracks[i];
                if (track == null || track.clip == null || string.IsNullOrWhiteSpace(track.label))
                    continue;

                if (string.Equals(track.label, label, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private bool HasPlayableTracks() {
            if (tracks == null || tracks.Count == 0)
                return false;

            for (var i = 0; i < tracks.Count; i++) {
                if (IsValidTrackIndex(i))
                    return true;
            }

            return false;
        }

        private bool IsValidTrackIndex(int trackIndex) {
            return tracks != null &&
                   trackIndex >= 0 &&
                   trackIndex < tracks.Count &&
                   tracks[trackIndex] != null &&
                   tracks[trackIndex].clip != null;
        }

        private void EnsureAudioSources() {
            var sources = GetComponents<AudioSource>();

            if (sources.Length >= 2) {
                _audioSourceA = sources[0];
                _audioSourceB = sources[1];
            }
            else if (sources.Length == 1) {
                _audioSourceA = sources[0];
                _audioSourceB = gameObject.AddComponent<AudioSource>();
            }
            else {
                _audioSourceA = gameObject.AddComponent<AudioSource>();
                _audioSourceB = gameObject.AddComponent<AudioSource>();
            }

            ConfigureAudioSource(_audioSourceA);
            ConfigureAudioSource(_audioSourceB);
        }

        private void ConfigureAudioSource(AudioSource source) {
            source.playOnAwake = false;
            source.loop = false;
        }

    }
}
