using Inkform.Tool;
using UnityEngine;

namespace Inkform.Player
{
    /// <summary>
    /// The player's kinematics: velocity, jump, non-linear gravity, dash and knockback move lockout.
    /// The second layer split from PlayerHandler — touches only the rigidbody, neither animation nor items.
    ///
    /// Tuning lives on Player.prefab: several [SerializeField] defaults here (gravity, coyoteTime,
    /// fallGravityMultiplier, attackTime) differ from the prefab's values, so read the prefab, not
    /// these initializers, for the shipped feel.
    /// No own Update; PlayerHandler calls Tick() in order.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(ContactSensor))]
    public class PlayerMotor : MonoBehaviour
    {
        [Header("Move")]
        [SerializeField] private float movingSpeed = 10f;
        [SerializeField] private float jumpSpeed = 12f;
        [SerializeField][Range(0, 1)] private float fallCutMultiplier = 0.12f;
        [SerializeField] private float jumpBuffer = 0.15f;
        [SerializeField] private int jumpTimes = 1;

        [Header("Jump feel")]
        [SerializeField] private float coyoteTime = 0.1f;          // grace after walking off a ledge: the unused ground jump survives this long

        [Header("Gravity")]
        [SerializeField] private float gravity = 3f;
        [SerializeField] private float fallGravityMultiplier = 2.2f;
        [SerializeField][Range(0, 1)] private float onWallGravityMultiplier = 0.2f;
        [Tooltip("On the frame the player first touches a wall while airborne, a fall faster than maxFallSpeed * this is rewritten down to it — the wall catches the player instead of the slide starting at full speed")]
        [SerializeField][Range(0, 1)] private float wallCatchFallFraction = 1f / 3f;
        [Tooltip("Symmetric Y speed cap: free-fall terminal velocity AND the safety net for any one-off upward burst (rope release, knockback). Active rope pulls and dashes assert their own speed and are unaffected; a jump lands exactly on it.")]
        [SerializeField] private float maxFallSpeed = 12f;

        [Header("Wall jump")]
        [SerializeField] private float wallJumpTime = 0.15f;
        [SerializeField][Range(0, 1)] private float wallKickMultiplier = 0.3f;

        [Header("Attack dash")]
        [SerializeField][Range(1, 5)] private float attackMultiplier = 1f;
        [SerializeField] private float attackTime = 0.22f;      // dash / move-lockout duration
        [SerializeField][Range(0f, 1f)] private float attackFullFraction = 0.7f;   // speed profile: full speed up to this fraction of the duration, linear decay to zero across the rest
        [Tooltip("Y velocity asserted on the dash's expiry frame (positive = up). A small upward value gives the dash a soft launch out of the decay instead of dying into a straight drop")]
        [SerializeField] private float dashEndYVelocity = 1.5f;

        [Header("Knockback")]
        [SerializeField] private float knockbackTime = 0.35f;   // move-input lockout after being blasted

        private Rigidbody2D body;
        private ContactSensor contact;

        private Timer wallJumpBuffer;
        private Timer attackTimer;
        private Timer knockbackTimer;
        private Timer updateBuffer;     // brief window after leaving the ground, during which jump count is not refilled
        private Timer coyoteTimer;      // the ground jump's grace window after walking off a ledge

        private int jumpLeft;
        private float requestTime = -999f;
        private bool jumpCutQueued;

        // Dash direction captured at Dash() (unit vector, free angle). StepDash rewrites it into the
        // rigidbody every frame of the dash, so the trajectory stays fixed for the whole duration
        private Vector2 attackDir = Vector2.right;

        // Expiry edge for StepDash: set by Dash(), cleared on the first frame the timer is no
        // longer running — that frame asserts dashEndYVelocity (see StepDash)
        private bool dashActive;

        // Previous frame's OnWall, for the wall-catch edge in ApplyNonLinearGravity
        private bool wasOnWall;

        // Platform follow state: standing on a moving platform carries the player along. Zero
        // "platform awareness" on the player side — it only reads physical facts (how far the contacted
        // rigidbody moved this frame); static platforms move zero, naturally unaffected
        private Collider2D groundPlatform;     // the ground collider from the previous frame
        private Vector2 groundPrevPos;         // the platform's previous-frame position

        // The jump happened this frame — handed to the animation layer by PlayerHandler.
        // A take-once-then-clear flag rather than an event: a one-shot notice within the same object,
        // an event would not pay for itself
        private bool jumpStarted;

        void Awake()
        {
            body = GetComponent<Rigidbody2D>();
            contact = GetComponent<ContactSensor>();

            body.gravityScale = gravity;
            jumpLeft = jumpTimes;
        }

        /// <summary>Whether a jump just started this frame. Cleared on take — consumed exactly once.</summary>
        public bool ConsumeJumpStarted()
        {
            bool v = jumpStarted;
            jumpStarted = false;
            return v;
        }

        // Move is locked (wall-jump recovery / dash / knockback / rope-gun attached). The animation
        // layer is unaffected and still follows input
        private bool MoveLocked =>
            wallJumpBuffer.IsRunning || attackTimer.IsRunning || knockbackTimer.IsRunning || grappleLocked;

        private bool grappleLocked;     // input lockout while the rope gun hangs/pulls; the rope dominates motion

        /// <summary>Locks move input while the rope gun is attached; unlocks on release. Locks input,
        /// never touches the rigidbody.</summary>
        public void SetMoveLocked(bool locked) => grappleLocked = locked;

        public float VelocityX => body.linearVelocityX;
        public float VelocityY => body.linearVelocityY;

        /// <summary>Whether the attack-dash timer is running — the dash owns the velocity while true.</summary>
        public bool IsDashing => attackTimer.IsRunning;

        /// <summary>The dash's fixed direction (unit vector); meaningful only while IsDashing.</summary>
        public Vector2 DashDirection => attackDir;

        /// <summary>Advances gravity and jump each frame. Called by PlayerHandler after ContactSensor.Tick().</summary>
        public void Tick()
        {
            ApplyNonLinearGravity();
            StepDash();
            StepJump();
            StepPlatform();
        }

        // Fixed-trajectory dash: while the dash timer runs, velocity is rewritten every frame so
        // nothing accumulates on top of it — gravity is zeroed in ApplyNonLinearGravity, and any
        // external velocity write only survives until the next frame. Update-chain, not
        // FixedUpdate: hitstop freezes physics steps, and the dash must keep asserting through them.
        // Speed profile: full speed for the first attackFullFraction of the duration, then a linear
        // decay to zero across the tail — the dash settles to a stop instead of snapping out. On
        // the expiry frame Y is replaced with dashEndYVelocity: the decay's residue would otherwise
        // loft a diagonal dash, and a hard zero drops straight down — the parameter buys a small
        // upward launch instead. X needs no such handling, Move rewrites it the moment the lockout
        // lifts.
        private void StepDash()
        {
            if (!attackTimer.IsRunning)
            {
                if (dashActive)
                {
                    dashActive = false;
                    body.linearVelocityY = dashEndYVelocity;
                }
                return;
            }

            dashActive = true;
            float progress = 1f - attackTimer.Remaining / attackTime;
            float factor = 1f;
            if (attackFullFraction < 1f && progress > attackFullFraction)
                factor = 1f - (progress - attackFullFraction) / (1f - attackFullFraction);
            body.linearVelocity = attackDir * movingSpeed * attackMultiplier * factor;
        }

        // Platform follow: adds the contacted rigidbody's movement this frame to the player, carrying
        // the player on a moving platform. Switching platforms resets the baseline (never applies the
        // cross-platform jump delta); jumping / walking off the edge stops the follow automatically
        // when Ground disappears
        private void StepPlatform()
        {
            if (contact.Ground == null)
            {
                groundPlatform = null;
                return;
            }

            if (contact.Ground != groundPlatform)
            {
                groundPlatform = contact.Ground;
                groundPrevPos = PlatformPos();
                return;
            }

            Vector2 now = PlatformPos();
            Vector2 delta = now - groundPrevPos;
            groundPrevPos = now;
            if (delta.sqrMagnitude < 1e-8f) return;

            // The project sets m_AutoSyncTransforms = 0: transform and rigidbody positions do not sync,
            // write both
            transform.position += (Vector3)delta;
            body.position += delta;
        }

        // Platform position reads the rigidbody (true physical position, synced by PatrolMover's
        // double-write); without a rigidbody, falls back to the transform
        private Vector2 PlatformPos()
        {
            Rigidbody2D rb = groundPlatform.attachedRigidbody;
            return rb != null ? rb.position : (Vector2)groundPlatform.transform.position;
        }

        public void Move(Vector2 input)
        {
            // Also rejects move input while blasted/dashing, or the next frame would wipe the knockback velocity
            if (MoveLocked) return;

#if UNITY_EDITOR
            // F2 flight: both axes at full move speed, gravity zeroed in Tick. Rope/dash lockouts
            // still win (the check above)
            if (DebugCheats.Flight)
            {
                body.linearVelocity = input * movingSpeed;
                return;
            }
#endif

            body.linearVelocityX = input.x * movingSpeed;
        }

        public void RequestJump()
        {
            // Only stamps the request; whether it becomes a ground, air or wall jump is StepJump's
            // call, made against the sensors on the frame it fires (so a press buffered just
            // before touching a wall still wall-jumps)
            requestTime = Time.time;
        }

        /// <summary>Jump released: cuts a chunk of the upward velocity, holding longer jumps higher.</summary>
        public void CutJump() => jumpCutQueued = true;

        /// <summary>Attack dash: locks velocity to dir (a unit vector, free angle) for attackTime
        /// seconds — fixed direction, gravity-free, full speed then a linear decay to zero (see
        /// StepDash) — and locks move input for the duration.</summary>
        public void Dash(Vector2 dir)
        {
            attackDir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.right;
            dashActive = true;
            body.linearVelocity = attackDir * movingSpeed * attackMultiplier;
            attackTimer.Set(attackTime);
        }

        /// <summary>Blasted away: rewrites velocity directly and locks a short move input.</summary>
        public void Knockback(Vector2 velocity)
        {
            body.linearVelocity = velocity;
            knockbackTimer.Set(knockbackTime);
        }

        /// <summary>Death: stops velocity and physics.
        /// Disabling physics stops all collision callbacks, so the corpse is not repeatedly judged by
        /// spikes. Must NOT SetActive(false) — OnDisable would unsubscribe the buses and "respawn"
        /// would never arrive.</summary>
        public void StopForDeath()
        {
            body.linearVelocity = Vector2.zero;
            body.simulated = false;
        }

        /// <summary>Respawn: teleports to the checkpoint and zeros all transient state.</summary>
        public void RespawnAt(Vector2 pos)
        {
            // The project sets m_AutoSyncTransforms = 0: transform and rigidbody positions do not sync,
            // write both
            transform.position = pos;
            body.position = pos;
            body.simulated = true;
            body.linearVelocity = Vector2.zero;

            // Lockouts and jump count accumulated before death must not carry into the next life
            jumpLeft = jumpTimes;
            jumpCutQueued = false;
            jumpStarted = false;
            requestTime = -999f;
            wallJumpBuffer.Clear();
            attackTimer.Clear();
            dashActive = false;
            wasOnWall = false;
            knockbackTimer.Clear();
            updateBuffer.Clear();
            coyoteTimer.Clear();
            grappleLocked = false;
            groundPlatform = null;
            groundPrevPos = Vector2.zero;
        }

        // The branch order matters, relying on ContactSensor's inverted semantics: CeilingStickActive
        // is always true while away from the ceiling, so the second branch (stick time exhausted) can
        // only hit while actually stuck — wall sliding and fall acceleration only run otherwise.
        // Read the comment on ContactSensor.ceilingStickTimer before reordering
        private void ApplyNonLinearGravity()
        {
            // Wall contact edge: sampled before the early returns below so the baseline stays
            // current through flight / dash frames
            bool wallTouched = contact.OnWall && !wasOnWall;
            wasOnWall = contact.OnWall;

#if UNITY_EDITOR
            // F2 flight: gravity never touches the body — Move drives both axes directly. Toggling
            // off self-heals: the normal path below assigns gravityScale every frame anyway
            if (DebugCheats.Flight)
            {
                body.gravityScale = 0f;
                return;
            }
#endif
            // Attack dash: gravity-free for its whole duration. Must return here — the branches below
            // assign gravityScale every frame (ceiling stick's -5 would yank an upward dash onto the
            // ceiling); expiry self-heals like the Flight branch above
            if (attackTimer.IsRunning)
            {
                body.gravityScale = 0f;
                return;
            }

            bool wallSliding = contact.OnWall && !contact.OnGround && body.linearVelocityY < 0f;

            if (contact.OnCeiling && contact.CeilingStickActive)    {body.gravityScale = -5f;}                                  // stuck to the ceiling, gravity points up
            else if (!contact.CeilingStickActive)                   {body.gravityScale = gravity;}                              // stick time exhausted, fall off
            else if (wallSliding)                                   {body.gravityScale = gravity * onWallGravityMultiplier;}    // wall slide slowdown
            else if (body.linearVelocityY < 0f)                     {body.gravityScale = gravity * fallGravityMultiplier;}      // falling acceleration, snappier feel
            else                                                    {body.gravityScale = gravity;}

            // Symmetric speed cap on Y. Downward: a long fall caps here instead of accelerating
            // forever (tunnel-through risk on thin floors, unreadably harsh landings). Upward:
            // the safety net — any one-off burst granting more rise than a jump (a rope release
            // at pullSpeed 16) is trimmed back. A bomb blast's knockback is exempt: Knockback's
            // only caller is the explosion handler, so while its lockout runs the launch keeps
            // its full force, and gravity (19.62 m/s² at the prefab's gravity 2) bleeds 18 down
            // below the cap in ~0.31s — inside the 0.35s window, so the cap resumes as a no-op,
            // no snap. Active rope pulls and dashes are likewise unaffected: they assert their
            // own velocity in FixedUpdate / StepDash, after this Update-chain clamp. Jumps land
            // exactly ON the cap (equal, not greater) and are never clipped. Lives after the
            // gravity branches so it also bounds the wall-slide and ceiling-release paths.
            if (body.linearVelocityY < -maxFallSpeed)
                body.linearVelocityY = -maxFallSpeed;
            if (!knockbackTimer.IsRunning && body.linearVelocityY > maxFallSpeed)
                body.linearVelocityY = maxFallSpeed;

            // Wall catch: on the first frame of touching a wall in the air, a fall faster than
            // the catch speed is rewritten to it; the reduced wall-slide gravity takes over from
            // there. Edge-only — while staying on the wall the fall is free to build again
            float catchSpeed = maxFallSpeed * wallCatchFallFraction;
            if (wallTouched && !contact.OnGround && body.linearVelocityY < -catchSpeed)
                body.linearVelocityY = -catchSpeed;
        }

        private void StepJump()
        {
            if (jumpCutQueued && body.linearVelocityY > 0f)
            {
                body.linearVelocityY *= fallCutMultiplier;
                jumpCutQueued = false;
            }
            else if (body.linearVelocityY <= 0f)
            {
                jumpCutQueued = false;
            }

            // A jump requested during a dash stays buffered (requestTime) and fires right after the
            // dash ends — the dash owns the velocity until its timer expires
            bool buffered = (Time.time - requestTime) < jumpBuffer;

            // Wall jump: any airborne frame a side sensor touches a wall — sliding, rising or
            // hanging alike, no wall-slide state required. It is free: it never spends jumpLeft,
            // so wall jumps never eat the air-jump budget
            bool wallJump = contact.OnWall && !contact.OnGround;
            if (buffered && (wallJump || jumpLeft > 0) && !attackTimer.IsRunning)
            {
                if (wallJump)
                {
                    float kick = contact.OnLeftWall ? 1f : -1f;     // away from the touched wall
                    body.linearVelocity = new Vector2(kick * jumpSpeed * wallKickMultiplier, jumpSpeed);
                    wallJumpBuffer.Set(wallJumpTime);
                }
                else
                {
                    jumpLeft--;
                }

                body.linearVelocityY = jumpSpeed;
                requestTime = -999f;
                jumpStarted = true;     // ground/double/wall jumps all count; the animation layer plays JumpUp

                // On the jump frame the feet have not left the ground; without a guard the line below
                // would immediately refill the count
                if (contact.OnGround) updateBuffer.Set(0.1f);
            }

            if ((contact.OnGround || contact.OnCeiling) && !updateBuffer.IsRunning) jumpLeft = jumpTimes;

            // Coyote time: the grace window keeps the unused ground jump alive for a moment after
            // walking off a ledge; once it expires the jump is revoked. Assumes the single-jump
            // design — a consumed jump is never revoked, and wall jumps never touch the count.
            // updateBuffer above is a different job: it blocks the refill on the jump frame itself.
            // OnCeiling is excluded so ceiling-stick's jump refill is never revoked
            if (contact.OnGround) coyoteTimer.Set(coyoteTime);
            else if (!coyoteTimer.IsRunning && !contact.OnCeiling && jumpLeft == jumpTimes) jumpLeft = 0;
        }
    }
}
