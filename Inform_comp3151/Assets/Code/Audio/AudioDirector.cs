using Inkform.Bus;
using Inkform.Item;
using Inkform.Player;
using UnityEngine;

namespace Inkform.Audio
{
    /// <summary>
    /// Audio director: translates "what happened in the game" into "which sound to play".
    /// Same pattern as FxDirector — subscribes to buses, centralizes which SoundCue each event maps to
    /// in this one Inspector; PlayerHandler / BreakablePart never need to know the audio system exists.
    /// Leaving a slot empty is legal: AudioService skips it silently; drop a Cue into the slot later and
    /// it sounds without touching code.
    /// </summary>
    public class AudioDirector : MonoBehaviour
    {
        [Header("Hazard")]
        [SerializeField] private SoundCue blast;        // explosion / successful bomb-powered dash
        [SerializeField] private SoundCue wallBreak;    // breakable wall shatter
        [SerializeField] private SoundCue bombTick;     // bomb warning frame advance / dash trigger

        [Header("Item")]
        [SerializeField] private SoundCue itemEaten;    // swallow
        [SerializeField] private SoundCue itemSpit;     // spit out
        // The capacity crystal (CapacityUpgradeFlight): touched, it flies into the HUD over 1.65s by
        // default — the world is frozen meanwhile but audio is not — and the backpack grows on landing
        [Tooltip("Touching a capacity crystal — one sound for its whole flight into the HUD (SFX_GetCrt, 1.64s)")]
        [SerializeField] private SoundCue inventoryCapacityUpgrade;
        [Tooltip("The crystal lands in the HUD backpack and the capacity goes up")]
        [SerializeField] private SoundCue capacityLand;

        [Header("Player")]
        [SerializeField] private SoundCue jump;
        [SerializeField] private SoundCue land;
        [SerializeField] private SoundCue ropeFire;    // grapple fired (instant anchor follows the same frame)
        [SerializeField] private SoundCue ropeHit;     // grapple anchored on terrain / grabbed a carriable
        [SerializeField] private SoundCue ropeCancel;  // pull deliberately broken (re-press, dash, jump off)
        [SerializeField] private SoundCue ropeRelease; // pull ended on its own (arrived, stuck, swallow failed)
        [SerializeField] private SoundCue dashTrail;   // afterimage trail on a successful dash (DashAfterimage)
        [Tooltip("Played every Footstep Interval while walking on the ground; the Cue picks a random clip each step")]
        [SerializeField] private SoundCue footstep;
        [Tooltip("Seconds between two footsteps while walking on the ground")]
        [SerializeField] private float footstepInterval = 0.3f;

        private float nextFootstepTime;

        // Death sounds are not here: they dispatch by cause rather than by concern (spiked vs fallen
        // should sound different), so the Cue lives on the DeathStrategy asset and is played by the strategy.
        [Header("Life")]
        [SerializeField] private SoundCue respawn;      // respawn at checkpoint
        [SerializeField] private SoundCue checkpoint;   // stepped on checkpoint

        void OnEnable()
        {
            HazardBus.Blast += OnBlast;
            HazardBus.Broken += OnBroken;
            HazardBus.Ticked += OnTicked;
            ItemBus.ItemStored += OnItemEaten;
            ItemBus.ItemReleased += OnItemReleased;
            ItemBus.InventoryCapacityUpgraded += OnInventoryCapacityUpgraded;
            ItemBus.CapacityFlight += OnCapacityFlight;
            PlayerBus.StateChanged += OnPlayerState;
            PlayerBus.DashAttempted += OnDashAttempted;
            RopeGunBus.Fired += OnRopeFired;
            RopeGunBus.Hit += OnRopeHit;
            RopeGunBus.RopeCancelled += OnRopeCancelled;
            RopeGunBus.RopeReleased += OnRopeReleased;
            LifeBus.Respawned += OnRespawned;
            LifeBus.CheckpointSet += OnCheckpointSet;
        }

        void OnDisable()
        {
            HazardBus.Blast -= OnBlast;
            HazardBus.Broken -= OnBroken;
            HazardBus.Ticked -= OnTicked;
            ItemBus.ItemStored -= OnItemEaten;
            ItemBus.ItemReleased -= OnItemReleased;
            ItemBus.InventoryCapacityUpgraded -= OnInventoryCapacityUpgraded;
            ItemBus.CapacityFlight -= OnCapacityFlight;
            PlayerBus.StateChanged -= OnPlayerState;
            PlayerBus.DashAttempted -= OnDashAttempted;
            RopeGunBus.Fired -= OnRopeFired;
            RopeGunBus.Hit -= OnRopeHit;
            RopeGunBus.RopeCancelled -= OnRopeCancelled;
            RopeGunBus.RopeReleased -= OnRopeReleased;
            LifeBus.Respawned -= OnRespawned;
            LifeBus.CheckpointSet -= OnCheckpointSet;
        }

        // Footsteps: PlayerState.Move only exists on the ground with move input (AnimStateResolver),
        // so it already means "walking on the ground". Time.time stops while paused (timeScale 0).
        // The dead check matters: PlayerHandler stops updating the state while dead, so it can stay Move
        void Update()
        {
            if (PlayerBus.State != PlayerState.Move || LifeBus.IsDead)
            {
                nextFootstepTime = 0f;   // the next walk starts with a step right away
                return;
            }

            if (Time.time < nextFootstepTime) return;
            nextFootstepTime = Time.time + footstepInterval;
            Play(footstep);              // the player's own sound, same stance as jump/land
        }

        // Explosions must only listen to Blast — Exploded fires per victim in a foreach, N victims = N sounds
        private void OnBlast(Vector2 center, float radius, float force) => Play(blast, center);

        private void OnBroken(Vector2 center) => Play(wallBreak, center);

        // Proximity warning and fuse countdown share this one sound. step / total are unused for now,
        // kept so "higher pitch as it gets closer" needs no bus signature change later
        private void OnTicked(Vector2 pos, int step, int total) => Play(bombTick, pos);

        private void OnDashAttempted(Vector2 pos, bool succeeded)
        {
            Play(bombTick, pos);
            if (!succeeded) return;
            Play(blast, pos);
            Play(dashTrail, pos);   // the trail starts the same frame a successful dash does
        }

        // The rope's own sounds, always on the player — no position, same stance as jump/land
        private void OnRopeFired(Vector2 dir) => Play(ropeFire);

        private void OnRopeHit(Vector2 dir) => Play(ropeHit);

        private void OnRopeCancelled() => Play(ropeCancel);

        private void OnRopeReleased() => Play(ropeRelease);

        private void OnItemEaten(InventoryItemDefinition item) => Play(itemEaten);

        private void OnInventoryCapacityUpgraded(Vector2 pos, int capacityIncrease) =>
            Play(inventoryCapacityUpgrade, pos);

        // Landing is on the HUD, not in the world: no position. A flight torn down by a scene
        // change (Cancel) never lands, so it stays silent.
        private void OnCapacityFlight(CapacityFlightPhase phase, int capacityIncrease)
        {
            if (phase == CapacityFlightPhase.Land) Play(capacityLand);
        }

        // Both of these are "the player's own sounds", always at the camera center, so like
        // jump/land they pass no position. The matching Cue assets should keep spatial off —
        // see the Tooltip in SoundCue.cs
        private void OnRespawned(GameObject victim, Vector2 pos) => Play(respawn);

        private void OnCheckpointSet(Vector2 pos) => Play(checkpoint);

        // The spit origin is always on the player ≈ camera center, so the attenuation factor is
        // necessarily near 1 — passing the position is only for "pass position when we have one"
        // consistency; audibly identical to passing nothing
        private void OnItemReleased(InventoryItemDefinition item, Vector2 pos, Vector2 velocity) => Play(itemSpit, pos);

        // PlayerBus already dedups (broadcasts only when a state actually changes), so no per-frame spam.
        // Eat/Release animations were removed (dash is pure dash), leaving only jump and land
        private void OnPlayerState(PlayerState state)
        {
            switch (state)
            {
                case PlayerState.JumpUp: Play(jump); break;
                case PlayerState.Land: Play(land); break;
            }
        }

        // Position only says "where the sound happens"; whether it pans and fades with distance is
        // decided by SoundCue.spatial — Cues without it are unaffected by passing one
        private static void Play(SoundCue cue, Vector2? position = null) => AudioService.Play(cue, position);
    }
}
