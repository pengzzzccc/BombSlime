using System;
using UnityEngine;
using Inkform.Player;

namespace Inkform.Bus
{
    /// <summary>
    /// Player state/action bus: published by PlayerHandler and its sibling AnimStateResolver (State/Face) — except InteractPressed, which
    /// InputHandler raises because "confirm" targets whatever the player stands near (world parts),
    /// not the player itself. Subscribers need no serialized player reference. State changes are
    /// deduped and snapshotted; DashAttempted is transient.
    /// </summary>
    public static class PlayerBus
    {
        public static event Action<PlayerState> StateChanged;
        public static event Action<FaceDirection> FaceChanged;
        public static event Action<Vector2, bool> DashAttempted;

        /// <summary>The player just touched a surface — ground, ceiling or a wall — moving into
        /// it at the given speed (world units / s). Transient, like DashAttempted.</summary>
        public static event Action<ContactSide, float> Contacted;

        /// <summary>Raised by RegisterPlayer: a live player came into existence (scene instance
        /// Awake, or a runtime spawn appearing long after sceneLoaded). Systems that bind once per
        /// player — the animation driver, for one — refresh here, because a newcomer matching the
        /// deduped snapshot (Idle onto Idle) fires no state event to refresh them.</summary>
        public static event Action<PlayerHandler> PlayerRegistered;

        /// <summary>One confirm press (E / gamepad north) — consumed by world parts that are currently
        /// in range (e.g. AbilityPickupPart), not by PlayerHandler.</summary>
        public static event Action InteractPressed;

        // Current snapshot: subscribers can read it in OnEnable to complete initial sync
        public static PlayerState State { get; private set; }
        public static FaceDirection Face { get; private set; }

        // The current scene's live player, registered by PlayerHandler on Awake and cleared on
        // destroy. Persistent managers must read the player from here instead of a serialized
        // reference: the GameManager survives scene switches (PersistentGameRoot's DontDestroyOnLoad),
        // so a reference to the spawning scene's player goes stale the moment the scene changes.
        public static PlayerHandler Player { get; private set; }

        public static void RegisterPlayer(PlayerHandler player)
        {
            Player = player;
            PlayerRegistered?.Invoke(player);
        }

        // The new scene's player registers (Awake) before the old one is destroyed, so the guard
        // keeps the live player in place during a scene switch
        public static void UnregisterPlayer(PlayerHandler player)
        {
            if (Player == player) Player = null;
        }

        public static void RaiseState(PlayerState state)
        {
            if (state == State) return;      // dedup: broadcast only on change
            State = state;
            StateChanged?.Invoke(state);
        }

        public static void RaiseFace(FaceDirection face)
        {
            if (face == Face) return;
            Face = face;
            FaceChanged?.Invoke(face);
        }

        /// <summary>One live-player dash input. succeeded means a DashFuel was consumed and motion began.</summary>
        public static void RaiseDashAttempted(Vector2 position, bool succeeded) =>
            DashAttempted?.Invoke(position, succeeded);

        public static void RaiseInteractPressed() => InteractPressed?.Invoke();

        public static void RaiseContact(ContactSide side, float impactSpeed) => Contacted?.Invoke(side, impactSpeed);

        // Static fields do not clear on scene reload; with Domain Reload off, dead subscribers from
        // the previous run linger
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            StateChanged = null;
            FaceChanged = null;
            DashAttempted = null;
            Contacted = null;
            InteractPressed = null;
            PlayerRegistered = null;
            // Match PlayerHandler's field defaults (Idle / R) to avoid an extra broadcast at startup
            State = PlayerState.Idle;
            Face = FaceDirection.R;
            Player = null;
        }
    }
}
