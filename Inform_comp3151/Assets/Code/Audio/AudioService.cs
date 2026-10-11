using System.Collections.Generic;
using Inkform.Settings;
using UnityEngine;
using UnityEngine.Audio;

namespace Inkform.Audio
{
    /// <summary>
    /// The game's audio front door, built only from what Unity's audio supports on every target
    /// including WebGL: AudioSource volume/pitch/3D linear rolloff, AudioMixer group volumes, and
    /// AudioListener pause/volume. No filters, mixer effects or panStereo — WebGL has none of them.
    ///
    /// - One-shots (Play): a fixed set of voices. CueLimiter applies each Cue's cooldown and
    ///   concurrency cap; when every voice is busy, the post takes over the oldest voice of the
    ///   same or a less important lane, or is dropped. Play hands out no AudioSource, so nothing
    ///   outside can hold a voice that has since been recycled for another sound.
    /// - Music (PlayMusic / StopMusic): two dedicated crossfading sources (MusicPlayer).
    /// - Loops placed in the world are not here: each AmbientSource owns its own AudioSource and
    ///   Unity virtualizes the ones out of earshot.
    /// - Volumes: the settings sliders land on the mixer's exposed MasterVolume / MusicVolume /
    ///   SfxVolume (dB). Every source routes into the mixer through OutputFor.
    ///
    /// Static entry points are null-safe: an empty Cue slot or a scene without the service simply
    /// stays silent. Lives on the GameManager; PersistentGameRoot keeps that host across scenes.
    /// </summary>
    // Before UIManager (order 0): the boot sting is posted from UIManager.Awake
    [DefaultExecutionOrder(-1000)]
    public class AudioService : MonoBehaviour
    {
        public static AudioService Instance { get; private set; }

        [Header("Mixer")]
        [Tooltip("The game mixer (Assets/Audio/Inkform.mixer). Its exposed MasterVolume / MusicVolume / SfxVolume follow the settings sliders")]
        [SerializeField] private AudioMixer mixer;
        [Tooltip("Default output of Music-category Cues without an Output of their own")]
        [SerializeField] private AudioMixerGroup musicGroup;
        [Tooltip("Default output of every other Cue without an Output of their own")]
        [SerializeField] private AudioMixerGroup sfxGroup;

        [Header("Voices")]
        [Tooltip("One-shot voices. When all are busy a post takes over the oldest voice of its own or a lower lane, or is dropped")]
        [SerializeField, Min(1)] private int voiceCount = 24;

        private const string MasterParam = "MasterVolume";
        private const string MusicParam = "MusicVolume";
        private const string SfxParam = "SfxVolume";

        private struct Voice
        {
            public AudioSource src;
            public SoundCue cue;        // null = free
            public float startedAt;     // Time.unscaledTime
        }

        private Voice[] voices = new Voice[0];
        private readonly CueLimiter limiter = new CueLimiter();
        private readonly List<CueLimiter.VoiceFact> facts = new List<CueLimiter.VoiceFact>();
        private MusicPlayer music;

        /// <summary>One-shot voices currently sounding (diagnostics: PerformanceRecorder).</summary>
        public int ActiveVoiceCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < voices.Length; i++)
                    if (voices[i].cue != null) count++;
                return count;
            }
        }

        /// <summary>One-shot voices currently carrying this Cue.</summary>
        public int ActiveCountOf(SoundCue cue) => limiter.ActiveCount(cue);

        // Static fields do not clear on scene reload; with Domain Reload off, a stale Instance from
        // the previous run would make every later duplicate service destroy itself on sight
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        // ---- Static entry points ----

        /// <summary>Plays a one-shot. A position only matters for spatial Cues; without one they
        /// play 2D at the camera like any other sound.</summary>
        public static void Play(SoundCue cue, Vector2? position = null)
        {
            if (cue == null || Instance == null) return;
            Instance.PlayOneShot(cue, position);
        }

        /// <summary>Crossfades the background music to a Cue (category Music). Same-track requests
        /// are no-ops; music never competes with one-shots for a voice.</summary>
        public static void PlayMusic(SoundCue cue, float fadeSeconds = 1.5f)
        {
            if (Instance != null && Instance.music != null) Instance.music.Play(cue, fadeSeconds);
        }

        /// <summary>Fades the music out over fadeSeconds.</summary>
        public static void StopMusic(float fadeSeconds = 1.5f)
        {
            if (Instance != null && Instance.music != null) Instance.music.Stop(fadeSeconds);
        }

        /// <summary>The mixer group a Cue plays through: its own Output, else the group its
        /// Category maps to. Null without a service — the source then feeds the listener directly.</summary>
        public static AudioMixerGroup OutputFor(SoundCue cue)
        {
            if (cue == null) return null;
            if (cue.output != null) return cue.output;
            if (Instance == null) return null;
            return cue.category == SoundCue.Category.Music ? Instance.musicGroup : Instance.sfxGroup;
        }

        /// <summary>Applies a Cue's playback settings to a source — routing, volume, pitch, pause
        /// behaviour, priority and, for spatial Cues, the 3D linear rolloff (see AudioSpatial).
        /// Shared by the one-shot voices and AmbientSource so both hear a Cue the same way.
        /// forceSpatial makes a world emitter positional even when its Cue's Spatial box is off.</summary>
        public static void ConfigureSource(AudioSource src, SoundCue cue, bool forceSpatial = false)
        {
            src.outputAudioMixerGroup = OutputFor(cue);
            src.volume = cue.volume;
            src.pitch = cue.PickPitch();
            src.ignoreListenerPause = cue.ignoreListenerPause;
            src.priority = (int)cue.priority;
            src.dopplerLevel = 0f;      // a moving camera must not bend the pitch
            if (cue.spatial || forceSpatial)
            {
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = AudioSpatial.MinDistance;
                src.maxDistance = AudioSpatial.MaxDistanceFor(cue.falloffRange);
            }
            else
            {
                src.spatialBlend = 0f;
            }
        }

        // ---- Lifecycle ----

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            // No DontDestroyOnLoad here: the GameManager host is already claimed by PersistentGameRoot

            voices = new Voice[Mathf.Max(1, voiceCount)];
            for (int i = 0; i < voices.Length; i++) voices[i].src = CreateVoiceSource(i);

            music = MusicPlayer.Create(transform);
        }

        void OnEnable() => SettingsStore.Changed += ApplyVolumes;

        void OnDisable() => SettingsStore.Changed -= ApplyVolumes;

        // Not Awake: AudioMixer.SetFloat issued during Awake can be overwritten by the mixer's own
        // initialization
        void Start() => ApplyVolumes();

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // Finished voices go back to the free set. A voice silenced by AudioListener.pause has
        // not finished, and a clip still loading has not started — neither is recycled
        void Update()
        {
            for (int i = 0; i < voices.Length; i++)
            {
                Voice voice = voices[i];
                if (voice.cue == null) continue;

                if (voice.src == null)
                {
                    limiter.Released(voice.cue);
                    voices[i] = new Voice { src = CreateVoiceSource(i) };
                    continue;
                }
                if (voice.src.isPlaying) continue;
                if (AudioListener.pause && !voice.src.ignoreListenerPause) continue;
                if (voice.src.clip != null && voice.src.clip.loadState == AudioDataLoadState.Loading) continue;

                Release(i);
            }
        }

        // ---- One-shots ----

        private void PlayOneShot(SoundCue cue, Vector2? position)
        {
            float now = Time.unscaledTime;
            if (!limiter.CanStart(cue, now)) return;

            AudioClip clip = cue.PickClip();
            if (clip == null) return;           // Cue has no clip, or every slot is empty

            int slot = AcquireVoice(cue.priority);
            if (slot < 0) return;               // every voice busy with something at least as important

            AudioSource src = voices[slot].src;
            ConfigureSource(src, cue);
            src.clip = clip;
            src.loop = false;                   // one-shots end on their own; loops are AmbientSource / music
            if (cue.spatial && position.HasValue)
            {
                // On the world plane at the emitter: the camera-side listener hears the plane offset
                src.transform.position = new Vector3(position.Value.x, position.Value.y, 0f);
            }
            else
            {
                src.spatialBlend = 0f;          // spatial Cue posted without a position: play it at the camera
            }

            src.Play();
            voices[slot] = new Voice { src = src, cue = cue, startedAt = now };
            limiter.Started(cue, now);
        }

        private int AcquireVoice(CuePriority incoming)
        {
            for (int i = 0; i < voices.Length; i++)
            {
                if (voices[i].cue != null) continue;
                if (voices[i].src == null) voices[i].src = CreateVoiceSource(i);
                return i;
            }

            facts.Clear();
            for (int i = 0; i < voices.Length; i++)
                facts.Add(new CueLimiter.VoiceFact { priority = voices[i].cue.priority, startedAt = voices[i].startedAt });

            int victim = CueLimiter.PickVictim(facts, incoming);
            if (victim >= 0) Release(victim);
            return victim;
        }

        private void Release(int i)
        {
            Voice voice = voices[i];
            if (voice.src != null)
            {
                voice.src.Stop();
                voice.src.clip = null;
            }
            limiter.Released(voice.cue);
            voices[i] = new Voice { src = voice.src };
        }

        private AudioSource CreateVoiceSource(int index)
        {
            GameObject go = new GameObject($"Voice_{index}");
            go.transform.SetParent(transform, false);
            AudioSource src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            return src;
        }

        // ---- Volumes ----

        private void ApplyVolumes()
        {
            if (mixer == null) return;
            mixer.SetFloat(MasterParam, AudioLevels.LinearToDb(SettingsStore.MasterVolume));
            mixer.SetFloat(MusicParam, AudioLevels.LinearToDb(SettingsStore.MusicVolume));
            mixer.SetFloat(SfxParam, AudioLevels.LinearToDb(SettingsStore.SfxVolume));
        }
    }
}
