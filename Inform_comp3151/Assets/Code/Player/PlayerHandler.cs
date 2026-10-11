using Inkform.Bus;
using Inkform.Life;
using Inkform.Tool;
using UnityEngine;

namespace Inkform.Player
{
    /// <summary>
    /// Player coordinator: receives input, drives each subsystem in a fixed order, responds to death
    /// and respawn. Concrete responsibilities have been split to four components on the same object —
    /// ContactSensor (four-way contact), PlayerMotor (kinematics), AnimStateResolver (animation
    /// derivation), PlayerInventory (carrying). The rope gun (RopeGun) is an optional fifth component:
    /// the game plays fine without it; with it, wiring happens via TryGetComponent.
    ///
    /// Why keep this class instead of having InputHandler talk to PlayerMotor directly: the
    /// subsystems' Update order must be "sense → move → animate", and Unity does not guarantee
    /// Update order among components on one object — a single driver must order it explicitly, and
    /// that is this class. InputHandler finds it through PlayerBus.Player.
    /// </summary>
    // Rigidbody2D needs no declaration here: PlayerMotor already RequiresComponent on it
    [RequireComponent(typeof(ContactSensor))]
    [RequireComponent(typeof(PlayerMotor))]
    [RequireComponent(typeof(AnimStateResolver))]
    public class PlayerHandler : MonoBehaviour
    {
        private ContactSensor contact;
        private PlayerMotor motor;
        private readonly ContactEdges contactEdges = new ContactEdges();
        private static readonly System.Action<ContactSide, float> RaiseContact = PlayerBus.RaiseContact;
        private AnimStateResolver anim;
        private PlayerInventory inventory; // may be null: levels without item gameplay need not attach it
        private RopeGun ropeGun;        // may be null: levels without the rope gun play fine

        private Vector2 lastMoveInput;  // latest frame's move input (WASD / left stick): fallback direction for spitting without a rope gun


        void Awake()
        {
            // Register before broadcasting: subscribers' first Refresh reads PlayerBus.Player
            PlayerBus.RegisterPlayer(this);

            contact = GetComponent<ContactSensor>();
            motor = GetComponent<PlayerMotor>();
            anim = GetComponent<AnimStateResolver>();
            TryGetComponent(out inventory);
            TryGetComponent(out ropeGun);

            // Broadcast once at startup so the bus snapshot is correct from the first frame
            PlayerBus.RaiseState(PlayerState.Idle);
            PlayerBus.RaiseFace(FaceDirection.R);
        }

        void OnEnable()
        {
            HazardBus.Exploded += OnExploded;
            LifeBus.Died += OnDied;
            LifeBus.Respawned += OnRespawned;
        }

        void OnDisable()
        {
            HazardBus.Exploded -= OnExploded;
            LifeBus.Died -= OnDied;
            LifeBus.Respawned -= OnRespawned;
        }

        void OnDestroy()
        {
            PlayerBus.UnregisterPlayer(this);
        }

        void Update()
        {
            if (LifeBus.IsDead) return;     // fully stopped while dead: contact, gravity, jump, animation all halted

            // Order must not move: animation reads contact and velocity from this same frame,
            // otherwise it lags a frame and flashes the wrong animation on landing/jumping moments
            contact.Tick();
            // "Just touched" moments for haptics, judged against last frame's velocity
            contactEdges.Detect(contact.OnGround, contact.OnCeiling, contact.OnLeftWall, contact.OnRightWall, RaiseContact);
            motor.Tick();
            contactEdges.RememberVelocity(new Vector2(motor.VelocityX, motor.VelocityY));
            if (motor.ConsumeJumpStarted()) anim.OnJumpStarted();
            anim.Tick();
            // Dash smashing lives in DashBreaker's own Update (RopeGun-style independent loop):
            // it must survive anything this chain throws before it
        }

        // ---- Input entries. Names match the actions in the InputSystem_Actions asset one-to-one;
        // rename actions and these together ----

        /// <summary>Move action (WASD / left stick).</summary>
        public void Move(Vector2 input)
        {
            if (LifeBus.IsDead) return;     // dead: neither facing nor velocity changes

            lastMoveInput = input;          // fallback source for spit direction (when no rope gun)

            // Facing: input always wins (applies even during move lockout); without input keep the current facing
            if (input.x > 0.01f) anim.SetFace(FaceDirection.R);
            else if (input.x < -0.01f) anim.SetFace(FaceDirection.L);

            anim.SetMoveInput(input);       // drives animation only; follows input even during lockout
            motor.Move(input);              // whether to lock is motor's own call
        }

        /// <summary>Jump action pressed (Space / A).</summary>
        public void JumpPressed()
        {
            if (LifeBus.IsDead) return;

            // Rope gun hanging: jump = release the rope + normal jump
            if (ropeGun != null) ropeGun.DetachOnJump();

            motor.RequestJump();
        }

        /// <summary>Jump action released: cuts the upward velocity, holding longer jumps higher.</summary>
        public void JumpReleased()
        {
            if (LifeBus.IsDead) return;

            motor.CutJump();
        }

        /// <summary>Dash action (LeftShift / LB). Direction = the aim direction (the rope gun's
        /// reticle; on a pad that is the right stick's last pushed direction), SNAPPED to 8
        /// directions — never a free angle — falling back to the 8-way move input, then to the
        /// facing. The dash itself is fixed-direction and gravity-free for its whole duration,
        /// decaying to a stop at the end (see PlayerMotor).</summary>
        public void Dash()
        {
            if (LifeBus.IsDead) return;

            bool succeeded = inventory != null && inventory.TryConsumeDashFuel();
            if (succeeded)
            {
                Vector2 dir;
                if (ropeGun != null)
                    dir = Dir8.Snap(ropeGun.EffectiveFireDir);
                else if (lastMoveInput.sqrMagnitude > 0.0001f)
                    dir = Dir8.Snap(lastMoveInput);
                else
                    dir = PlayerBus.Face == FaceDirection.R ? Vector2.right : Vector2.left;

                // The dash takes over motion: release any active rope (mid-pull or miss dangle)
                // first, or the pull's per-physics-step velocity writes would fight the dash
                if (ropeGun != null) ropeGun.Cancel();

                motor.Dash(dir);
                anim.SetFace(dir.x >= 0f ? FaceDirection.R : FaceDirection.L);
            }

            PlayerBus.RaiseDashAttempted(transform.position, succeeded);
        }

        // ---- Rope gun input entries ----

        /// <summary>Aim action (mouse delta / right stick): drives the rope gun's reticle.</summary>
        public void Aim(Vector2 delta, bool pixelDelta) => ropeGun?.Aim(delta, pixelDelta);

        /// <summary>RopeFire action (left mouse / right trigger): fire the rope; pressing again cancels.</summary>
        public void RopeFire()
        {
            if (LifeBus.IsDead) return;
            ropeGun?.TryFire();
        }

        /// <summary>SpitBomb action (Q / right shoulder): spits the bomb, direction = the rope gun's
        /// effective fire direction (always exactly toward the reticle); without a rope gun falls back
        /// to the 8-way move input, then to the facing.</summary>
        public void SpitBomb()
        {
            if (LifeBus.IsDead) return;
            if (inventory == null || inventory.IsEmpty) return;

            Vector2 dir;
            if (ropeGun != null)
                dir = ropeGun.EffectiveFireDir;
            else if (lastMoveInput.sqrMagnitude > 0.0001f)
                dir = Dir8.Snap(lastMoveInput);
            else
                dir = PlayerBus.Face == FaceDirection.R ? Vector2.right : Vector2.left;

            if (inventory.TryReleaseFirst(dir))
            {
                anim.SetFace(dir.x >= 0f ? FaceDirection.R : FaceDirection.L);
                // no spit animation (the Release animation was removed)
            }
        }

        // ---- Bus callbacks ----

        // Called by HazardBus on explosion: flung along the 8-way "blast center → self" direction,
        // with a brief move-input lockout
        private void OnExploded(GameObject victim, Vector2 center, float force)
        {
            if (victim != gameObject) return;

            motor.Knockback(Dir8.Snap((Vector2)transform.position - center) * force);
        }

        // Called by LifeBus on the player's death: only stops gameplay; body vanishing and the shard
        // burst belong to the death strategy
        private void OnDied(DeathContext ctx)
        {
            if (ctx.Victim != gameObject) return;

            motor.StopForDeath();
        }

        // Called by LifeBus on respawn: teleports to the checkpoint and zeros all transient state
        private void OnRespawned(GameObject victim, Vector2 pos)
        {
            if (victim != gameObject) return;

            motor.RespawnAt(pos);
            anim.ResetForRespawn();
            lastMoveInput = Vector2.zero;
            contact.ResetForRespawn();

            // Probe the ground once before aligning the baseline: otherwise respawning on the ground
            // counts as "just landed" and plays a Land animation and landing sound for nothing
            contact.Tick();
            anim.SyncContactBaseline();
            contactEdges.SyncBaseline(contact.OnGround, contact.OnCeiling, contact.OnLeftWall, contact.OnRightWall);
        }
    }
}
