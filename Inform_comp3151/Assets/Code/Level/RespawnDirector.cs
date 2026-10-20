using Inkform.Bus;
using Inkform.Fx;
using Inkform.Input;
using Inkform.Life;
using Inkform.Player;
using Inkform.Save;
using Inkform.Tool;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Inkform.Level
{
    /// <summary>
    /// Respawn director: creates a missing scene player at the resolved arrival point, remembers the
    /// current checkpoint, and after death pauses briefly before putting the deceased back. It never
    /// reloads for a respawn, so cross-scene singletons like AudioService never need rebuilding.
    /// Attach to GameManager (already the host of InputHandler / AudioDirector / AudioService).
    /// </summary>
    public class RespawnDirector : MonoBehaviour
    {
        [Header("Respawn")]
        // Fallback value: used when the cause has no strategy configured. Normally the pause length is
        // decided by DeathStrategy.RespawnDelay — "how quickly you come back" is a property of the
        // death method (falling should respawn faster than being spiked), not of the respawn system
        [SerializeField] private float fallbackDelay = 0.9f;     // death-to-respawn pause; Celeste is about 1 second

        [Header("Player")]
        // Rooms graduated to runtime spawning carry no placed Player: when a scene has none, this
        // prefab is instantiated at its start point instead. Rooms that still hold a placed Player
        // keep working untouched — the instance is found and reused (see InitForScene).
        [SerializeField] private PlayerHandler playerPrefab;

        private Vector2 checkpoint;
        private GameObject pending;         // the deceased waiting to respawn, null = nobody waiting

        // Must use unscaled timing: on the death frame the death strategy requests hitstop, crushing
        // timeScale to 0 — a plain Timer's Time.time is frozen then, waiting for it to expire is
        // waiting forever
        private float respawnRemaining;

        // Same lazy cache as PlayerBus.Player: after a scene change the old
        // reference becomes a Unity fake-null and is re-looked-up on next use, so no ResetStatics needed
        private DeathDirector deathCache;

        private DeathDirector Deaths
        {
            get
            {
                // FindAnyObjectByType rather than FindFirstObjectByType: the latter depends on instance
                // ID ordering and is deprecated (the order was never stable anyway; which one is found
                // first is irrelevant here)
                if (deathCache == null) deathCache = FindAnyObjectByType<DeathDirector>();
                return deathCache;
            }
        }

        // Set by sceneLoaded, consumed one frame later in Update. See OnSceneLoaded for why.
        private bool sceneInitPending;
        private bool sceneInitHeld;

        void OnEnable()
        {
            LifeBus.Died += OnDied;
            LifeBus.CheckpointSet += OnCheckpointSet;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDisable()
        {
            EndArrivalWalk();   // never leave gameplay input locked behind
            LifeBus.Died -= OnDied;
            LifeBus.CheckpointSet -= OnCheckpointSet;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        // The GameManager hosting this survives scene switches (PersistentGameRoot calls
        // DontDestroyOnLoad on its host), so Start() only ever ran against the *first* scene. Booting
        // straight into a level hid that, but the menu flow made MainMenu the first scene: no player
        // was found there, Start() bailed out, and `checkpoint` stayed at its default (0,0) — dying
        // in a level before touching any checkpoint teleported the player to the world origin.
        //
        // Deferred by a frame rather than run here: sceneLoaded fires before the new scene's Start()
        // methods, and the init below actually teleports the player and snaps the camera — doing that
        // ahead of the player's own Start() risks being overwritten. Update runs after all Starts,
        // matching the timing this used to have.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => sceneInitPending = true;

        // sceneLoaded never fires for the startup scene, so the first scene is initialized here
        void Start()
        {
            if (!sceneInitHeld) InitForScene();
        }

        private void InitForScene()
        {
            // Loaded save: the coordinate the save recorded outranks every spawn point in the scene —
            // it is the whole point of continuing a run. Checked first so neither the door-side spawn
            // nor the start point can win, and consumed here so it applies to this scene only.
            Vector2? loaded = SceneDirector.Instance != null ? SceneDirector.Instance.ConsumePendingSpawnPos() : null;
            if (loaded.HasValue)
            {
                checkpoint = loaded.Value;
                PlaceInitialPlayer(checkpoint, true);
                return;
            }

            // Startup respawn point: prefer the checkpoint with isStartPoint checked and teleport the
            // player there; with none checked the game still plays — falls back to "wherever the
            // player was placed in the scene"
            Checkpoint start = FindStartPoint();

            // Door-side spawn: the door the player walked through names the arrival checkpoint
            // (Checkpoint.spawnId) rather than the level start point, so they land beside where they
            // came in and direction stays intuitive across level transitions
            string spawnId = SceneDirector.Instance != null ? SceneDirector.Instance.ConsumePendingSpawnId() : null;
            Checkpoint spawn = !string.IsNullOrEmpty(spawnId) ? FindSpawnPoint(spawnId) : null;
            if (spawn == null) spawn = start;

            if (spawn == null)
            {
                PlayerHandler existing = FindAnyObjectByType<PlayerHandler>(FindObjectsInactive.Include);
                if (existing == null) return; // menu or a non-gameplay scene
                checkpoint = existing.transform.position;
                RecordSave();   // still a new room, still where the run now is — see RecordSave
                return;
            }

            checkpoint = spawn.SpawnPos;
            PlayerHandler placed = PlaceInitialPlayer(checkpoint, true);

            // Arrived through a door (spawnId is non-null for every door arrival, "" when it names
            // the start point): the arrival checkpoint may walk the player in before handing over
            if (spawnId != null && placed != null && spawn.ArrivalWalkDistance > 0f)
                BeginArrivalWalk(placed, spawn);
        }

        // ---- Arrival walk ----

        // The player walking in from a door on their own (Checkpoint's arrival walk). It waits for the
        // scene transition to finish completely, then walks. Gameplay
        // input stays locked under this component's own name from arrival until the walk ends, so
        // the transition handing control back does not let the player move in the meantime.
        private PlayerHandler walker;
        private float walkSign;
        private float walkStartX;
        private float walkDistance;
        private float walkSpeed;
        private float walkLastX;
        private float walkStalled;
        private InputHandler walkInput;

        // A walk that has stopped advancing for this long is pressed against something: end it
        private const float WalkStallSeconds = 0.4f;

        private void BeginArrivalWalk(PlayerHandler player, Checkpoint spawn)
        {
            EndArrivalWalk();
            walker = player;
            walkSign = spawn.ArrivalWalkSign;
            walkDistance = spawn.ArrivalWalkDistance;
            walkSpeed = spawn.ArrivalWalkSpeed;
            walkStartX = walkLastX = player.transform.position.x;
            walkStalled = 0f;

            walkInput = GetComponent<InputHandler>();
            if (walkInput == null) walkInput = FindAnyObjectByType<InputHandler>();
            if (walkInput != null) walkInput.SetGameplayInputLocked(this, true);
        }

        private void StepArrivalWalk()
        {
            if (walker == null || LifeBus.IsDead)
            {
                EndArrivalWalk();
                return;
            }

            // Stand still until the transition (fade-in included) is fully over
            if (SceneDirector.Instance != null && SceneDirector.Instance.IsTransitioning)
            {
                walker.Move(Vector2.zero);
                walkStartX = walkLastX = walker.transform.position.x;   // measure from where the walk really starts
                return;
            }

            float x = walker.transform.position.x;
            if ((x - walkStartX) * walkSign >= walkDistance)
            {
                EndArrivalWalk();
                return;
            }

            // Scaled time on purpose: hitstop or a pause is not the walk being blocked
            walkStalled = Mathf.Abs(x - walkLastX) < 0.001f ? walkStalled + Time.deltaTime : 0f;
            walkLastX = x;
            if (walkStalled >= WalkStallSeconds)
            {
                EndArrivalWalk();
                return;
            }

            // The same entry the input layer drives: facing and the walk animation follow for free
            walker.Move(new Vector2(walkSign * walkSpeed, 0f));
        }

        private void EndArrivalWalk()
        {
            if (walker != null) walker.Move(Vector2.zero);
            walker = null;
            if (walkInput != null) walkInput.SetGameplayInputLocked(this, false);
            walkInput = null;
        }

        /// <summary>Prevents the deferred scene initializer from creating the player while a room
        /// intro is presenting its empty establishing shot.</summary>
        public void HoldSceneInitialization()
        {
            sceneInitHeld = true;
        }

        /// <summary>Clears a cinematic hold without forcing initialization during scene teardown.
        /// The next sceneLoaded callback will schedule its own normal initialization.</summary>
        public void ReleaseSceneInitialization()
        {
            sceneInitHeld = false;
        }

        /// <summary>Instantiates (or reuses) the player at this scene's authored start checkpoint.
        /// A fresh instance is created at its final position and deliberately does not raise the
        /// death-respawn event, so a new-game entrance does not play the respawn sound.</summary>
        public PlayerHandler SpawnPlayerAtSceneStart()
        {
            Checkpoint start = FindStartPoint();
            sceneInitHeld = false;
            sceneInitPending = false;

            if (start == null)
            {
                Debug.LogError("RespawnDirector: RoomIntro needs a start Checkpoint, but none is configured", this);
                return null;
            }

            checkpoint = start.SpawnPos;
            return PlaceInitialPlayer(checkpoint, false);   // RoomIntro stages the camera itself
        }

        /// <summary>Returns the scene's Player — found, reactivated or freshly instantiated at the
        /// requested position. The strongly typed prefab slot cannot accept a sensor child by mistake.</summary>
        private PlayerHandler AcquirePlayer(Vector2 position, out bool created)
        {
            created = false;
            PlayerHandler existing = FindAnyObjectByType<PlayerHandler>(FindObjectsInactive.Include);
            if (existing != null)
            {
                if (!existing.gameObject.activeSelf) existing.gameObject.SetActive(true);
                return existing;
            }
            if (playerPrefab == null)
            {
                Debug.LogError("RespawnDirector: no Player in the scene and no PlayerHandler prefab to instantiate", this);
                return null;
            }

            created = true;
            return Instantiate(playerPrefab, position, Quaternion.identity);
        }

        /// <summary>
        /// Establishes the initial player for a scene. Existing scene instances still receive the
        /// respawn placement event so their transient state is reset; a new instance already ran its
        /// clean Awake at the final position and must not masquerade as a death respawn.
        /// </summary>
        private PlayerHandler PlaceInitialPlayer(Vector2 position, bool snapCamera)
        {
            PlayerHandler player = AcquirePlayer(position, out bool created);
            if (player == null) return null;

            if (!created) LifeBus.RaiseRespawned(player.gameObject, position);
            if (snapCamera) FxBus.RaiseSnap();
            RecordSave();
            return player;
        }

        /// <summary>
        /// Autosave. Called at the two moments `checkpoint` changes for a reason worth keeping — a room
        /// entered, or a checkpoint touched — so the save always names a place the player can be put
        /// back onto. Deliberately *not* called from the death respawn or from Unstuck: neither moves
        /// the checkpoint, so there would be nothing new to write (the save keeps the checkpoint, not
        /// the player's live position, so a push-out has nothing to write either).
        ///
        /// This lives here rather than in a save-specific director because this class already owns
        /// "where the player comes back", and reading `checkpoint` from anywhere else would mean
        /// racing this component's own deferred scene init (both SceneDirector and this one defer by a
        /// frame, and Update order between two components on one GameObject is not defined).
        /// SaveStore ignores the call entirely outside a run, so opening a level straight from the
        /// editor writes nothing.
        /// </summary>
        private void RecordSave() => SaveStore.RecordProgress(SceneManager.GetActiveScene().name, checkpoint);

        void Update()
        {
            // Runs before the respawn pump: a scene switch invalidates `checkpoint`, and a death
            // pending from the previous scene must never be resurrected against the new one
            if (sceneInitPending && !sceneInitHeld)
            {
                sceneInitPending = false;
                pending = null;
                respawnRemaining = 0f;
                EndArrivalWalk();   // a walk from the previous room must not carry over
                InitForScene();
            }

            if (walker != null) StepArrivalWalk();

            if (pending == null) return;
            if (GameTimeController.Instance == null || !GameTimeController.Instance.IsUserPaused)
                respawnRemaining = Mathf.Max(0f, respawnRemaining - Time.unscaledDeltaTime);
            if (respawnRemaining > 0f) return;

            LifeBus.RaiseRespawned(pending, checkpoint);
            pending = null;

            // The player was just teleported; without a snap the camera drags its follow inertia all
            // the way from the death point
            FxBus.RaiseSnap();
        }

        /// <summary>
        /// Unstuck (the pause menu's button, and the Controls tab's row), for when a bug wedges the
        /// slime somewhere it cannot leave. Two remedies, the gentler first:
        ///
        ///  - Wedged into terrain: pushed to the nearest free spot (UnstuckSolver, within a few
        ///    units, preferring up). Only the player moves — no respawn, so the level is not
        ///    restored and nothing since the checkpoint is lost.
        ///  - Not wedged (trapped in a pit, say), or no free spot nearby: back to the last
        ///    checkpoint through the death path's own teleport, so everything listening for a
        ///    respawn (camera snap, state reset, level restore) sees the event it always does. Any
        ///    death still waiting out its delay is cleared — it would otherwise fire a second later
        ///    against a player who has already been moved, teleporting them again.
        ///
        /// Either way LifeBus.Unstuck reports where from and where to, for the perf log.
        /// </summary>
        public void Unstuck()
        {
            PlayerHandler player = PlayerBus.Player;
            if (player == null) return;     // no player in this scene (the menu) — nobody to rescue
            EndArrivalWalk();               // a rescue hands control back, it does not keep walking

            Vector2 from = player.transform.position;
            if (!LifeBus.IsDead && TryPushOut(player, out Vector2 freeSpot))
            {
                player.NudgeTo(freeSpot);
                LifeBus.RaiseUnstuck(from, freeSpot, true);
                return;
            }

            pending = null;
            respawnRemaining = 0f;
            LifeBus.RaiseRespawned(player.gameObject, checkpoint);
            FxBus.RaiseSnap();
            LifeBus.RaiseUnstuck(from, checkpoint, false);
        }

        // Terrain the body can be wedged in (ContactSensor's mask: Terrain | Breakable), and what a
        // push-out must also keep clear of (Hazard) — freeing the player onto spikes rescues nobody
        private const int WedgeMask = (1 << 6) | (1 << 11);
        private const int BlockedMask = WedgeMask | (1 << 13);

        // The player's own capsule, tested at the current spot (wedged?) and at candidate spots
        // (free?). False when the body is not wedged, or no free spot is near enough.
        private static bool TryPushOut(PlayerHandler player, out Vector2 freeSpot)
        {
            freeSpot = default;
            CapsuleCollider2D capsule = player.GetComponent<CapsuleCollider2D>();
            if (capsule == null) return false;

            Vector2 scale = player.transform.lossyScale;
            Vector2 size = new Vector2(capsule.size.x * Mathf.Abs(scale.x), capsule.size.y * Mathf.Abs(scale.y));
            Vector2 offset = Vector2.Scale(capsule.offset, scale);
            float angle = player.transform.eulerAngles.z;

            bool Overlaps(Vector2 at, int mask)
            {
                Collider2D hit = Physics2D.OverlapCapsule(at + offset, size, capsule.direction, angle, mask);
                return hit != null && !hit.isTrigger;
            }

            Vector2 origin = player.transform.position;
            if (!Overlaps(origin, WedgeMask)) return false;

            Vector2? spot = UnstuckSolver.FindFreeSpot(origin, at => !Overlaps(at, BlockedMask));
            if (!spot.HasValue) return false;
            freeSpot = spot.Value;
            return true;
        }

        private void OnCheckpointSet(Vector2 pos)
        {
            checkpoint = pos;
            RecordSave();
        }

        private void OnDied(DeathContext ctx)
        {
            pending = ctx.Victim;

            // Without a DeathDirector in the scene, or when this cause has no strategy, fall back:
            // a missing strategy should only drop the presentation, never leave the player dead forever
            DeathDirector deaths = Deaths;
            DeathStrategy strategy = deaths != null ? deaths.Resolve(ctx.Cause) : null;

            respawnRemaining = Mathf.Max(0f, strategy != null ? strategy.RespawnDelay : fallbackDelay);
        }

        // The no-sort-parameter overload is the current recommended API: the FindObjectsSortMode
        // version is deprecated in Unity 6000.4 — instance ID ordering was never stable, and order
        // does not matter here
        private Checkpoint FindStartPoint()
        {
            Checkpoint[] all = Object.FindObjectsByType<Checkpoint>();
            foreach (Checkpoint c in all)
            {
                if (c.IsStartPoint) return c;
            }
            return null;
        }

        // Arrival checkpoints are keyed by Checkpoint.spawnId — a serialized field the door's
        // targetSpawnId must match, replacing the old Spawn_<sceneName> object-name convention
        private Checkpoint FindSpawnPoint(string spawnId)
        {
            Checkpoint[] all = Object.FindObjectsByType<Checkpoint>();
            foreach (Checkpoint c in all)
            {
                if (c.SpawnId == spawnId) return c;
            }
            return null;
        }
    }
}
