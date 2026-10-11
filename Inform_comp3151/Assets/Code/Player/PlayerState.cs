
namespace Inkform.Player
{
    // Eat / Release / Swing were removed with the old Bomb monolith: no animation branch plays them
    // (AnimStateResolver never produces them), so the states no longer exist in the enum.
    public enum PlayerState {Idle, Move, JumpUp, Rise, Fall, Land, CeilingStick, CeilingMove, WallSlideL, WallSlideR, CeilingIdle}
    public enum FaceDirection {L, R }

    /// <summary>Which surface the player just touched (PlayerBus.Contacted).</summary>
    public enum ContactSide { Down, Up, Left, Right }
}
