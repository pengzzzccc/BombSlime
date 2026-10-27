using Inkform.Bus;
using UnityEngine;

namespace Inkform.Player
{
    /// <summary>
    /// Dash direction hint: an arrow orbiting the player, pointing where the next dash would go.
    /// Reads PlayerHandler.DashDirection — the same 8-way resolve Dash() uses — so the arrow settles
    /// on one of the 8 directions and always matches the real dash, whichever Dash Direction setting
    /// is active. A change of direction swings the arrow round the orbit (turnTime) instead of
    /// teleporting it. Shown only while the dash would succeed (alive, holding dash fuel).
    ///
    /// The arrow is placed in world space every LateUpdate rather than as a fixed local offset:
    /// the player root is scaled (and the facing may flip it), which would stretch or mirror a
    /// local orbit.
    /// </summary>
    [RequireComponent(typeof(PlayerHandler))]
    public class DashAimIndicator : MonoBehaviour
    {
        [SerializeField] private Sprite arrowSprite;                // art points up (+Y)
        [SerializeField] private float radius = 0.9f;               // orbit distance from the player centre, world units
        [SerializeField] private float arrowScale = 1f;
        [SerializeField, Range(0f, 1f)] private float alpha = 0.85f;
        [SerializeField] private int sortingOrder = 18;             // player layer, under the rope reticle (20)
        [Tooltip("Seconds the arrow takes to swing around to a new direction (smoothed); 0 = jump instantly")]
        [SerializeField, Min(0f)] private float turnTime = 0.06f;

        private PlayerHandler handler;
        private PlayerInventory inventory;  // may be null: without an inventory the dash never fires, so the arrow never shows
        private Transform arrow;
        private SpriteRenderer arrowRenderer;

        // The drawn angle (degrees) swings toward the 8-way target along the short way round;
        // only the target is snapped, the arrow travels the orbit in between
        private float angle;
        private float angleVelocity;

        void Awake()
        {
            handler = GetComponent<PlayerHandler>();
            TryGetComponent(out inventory);

            var go = new GameObject("DashAimArrow");
            go.transform.SetParent(transform, false);
            arrow = go.transform;
            arrowRenderer = go.AddComponent<SpriteRenderer>();
            arrowRenderer.sprite = arrowSprite;
            arrowRenderer.color = new Color(1f, 1f, 1f, alpha);
            // Ride the body's sorting layer, same stance as RopeGun.CreateFx
            if (TryGetComponent(out SpriteRenderer body)) arrowRenderer.sortingLayerID = body.sortingLayerID;
            arrowRenderer.sortingOrder = sortingOrder;
            arrowRenderer.enabled = false;
        }

        // LateUpdate: after PlayerHandler took this frame's move input and the body moved
        void LateUpdate()
        {
            bool show = arrowSprite != null && !LifeBus.IsDead && inventory != null && inventory.HasDashFuel;
            bool wasShown = arrowRenderer.enabled;
            arrowRenderer.enabled = show;
            if (!show) return;

            Vector2 target = handler.DashDirection;
            float targetAngle = Mathf.Atan2(target.y, target.x) * Mathf.Rad2Deg;

            // Appearing (first frame, after respawn): start on the target, no swing in from a stale angle
            if (!wasShown || turnTime <= 0f)
            {
                angle = targetAngle;
                angleVelocity = 0f;
            }
            else
            {
                angle = Mathf.SmoothDampAngle(angle, targetAngle, ref angleVelocity, turnTime);
            }

            float rad = angle * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            arrow.position = (Vector2)transform.position + dir * radius;
            arrow.rotation = Quaternion.Euler(0f, 0f, angle - 90f);

            // Undo the root's scale (and any facing flip) so the arrow keeps its authored size
            Vector3 parentScale = transform.lossyScale;
            arrow.localScale = new Vector3(
                arrowScale / Mathf.Max(0.0001f, Mathf.Abs(parentScale.x)),
                arrowScale / Mathf.Max(0.0001f, Mathf.Abs(parentScale.y)), 1f);
        }
    }
}
