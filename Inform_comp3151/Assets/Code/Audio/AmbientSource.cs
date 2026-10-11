using UnityEngine;

namespace Inkform.Audio
{
    /// <summary>
    /// A looping world sound: gear whirr, laser hum, campfire crackle. Owns one AudioSource of its
    /// own (not a shared voice), configured from the Cue through AudioService.ConfigureSource:
    /// spatial Cues get Unity's 3D linear rolloff, so the loop pans and fades as the listener moves
    /// and goes silent beyond the Cue's Falloff Range, where Unity virtualizes it for free.
    /// Enabling plays the loop, disabling stops it — TimedVisibility toggles this component to
    /// switch a laser's hum with its beam.
    /// </summary>
    public class AmbientSource : MonoBehaviour
    {
        [Tooltip("Looping Cue for this emitter. Keep spatial on and set falloffRange to the " +
            "audible radius; the loop is forced on either way")]
        [SerializeField] private SoundCue cue;

        [Tooltip("Emitter offset from this transform, for placing the sound beside the visual")]
        [SerializeField] private Vector3 offset;

        private AudioSource source;

        void Awake()
        {
            // A child carries the source so the offset can place it; the emitter stays on the
            // world plane (z = 0) the spatial maths assumes, whatever this transform's z
            GameObject go = new GameObject("AmbientVoice");
            go.transform.SetParent(transform, false);
            source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
        }

        void OnEnable()
        {
            if (cue == null) return;
            Restart();
        }

        /// <summary>Assigns the loop at runtime, for emitters added from code (PatrolMover's loop
        /// slot). Starts it right away when this component is already enabled.</summary>
        public void Play(SoundCue loop)
        {
            cue = loop;
            if (cue != null && isActiveAndEnabled) Restart();
        }

        // Scene objects can enable before the persistent AudioService has woken up (editor cold
        // start into a room): re-route once everything has run Awake
        void Start()
        {
            if (cue != null && source.outputAudioMixerGroup == null)
                source.outputAudioMixerGroup = AudioService.OutputFor(cue);
        }

        void OnDisable()
        {
            if (source != null) source.Stop();
        }

        void LateUpdate()
        {
            if (offset != Vector3.zero) PlaceSource();   // follows a moving or rotating parent
        }

        private void Restart()
        {
            AudioClip clip = cue.PickClip();
            if (clip == null) return;

            // Always positional: a loop placed in the world must be loud near it and fade with
            // distance (silent beyond the Cue's Falloff Range), whatever the Cue's Spatial box says
            AudioService.ConfigureSource(source, cue, forceSpatial: true);
            source.clip = clip;
            source.loop = true;
            PlaceSource();
            source.Play();
        }

        private void PlaceSource()
        {
            Vector3 p = transform.position + offset;
            source.transform.position = new Vector3(p.x, p.y, 0f);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (cue == null) return;
            // The radius where the loop reaches silence, same number the runtime rolloff uses
            Gizmos.color = new Color(1f, 0.55f, 0.15f, 0.8f);
            Gizmos.DrawWireSphere(transform.position + offset, cue.falloffRange);
        }
#endif
    }
}
