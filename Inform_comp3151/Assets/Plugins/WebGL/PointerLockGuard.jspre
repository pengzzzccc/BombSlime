// Inkform: keep a refused pointer lock from crashing the page.
//
// Chrome refuses requestPointerLock() for about a second after the user left the lock with Esc,
// and rejects the returned Promise with a SecurityError. Unity requests the lock on every click
// while Cursor.lockState is Locked, and its loader treats the unhandled rejection as a fatal
// error ("An error occurred running the Unity content"). A refused lock is harmless — the next
// click asks again — so it is downgraded to a console warning here.
(function () {
  if (typeof HTMLCanvasElement === "undefined") return;
  var proto = HTMLCanvasElement.prototype;
  var original = proto.requestPointerLock;
  if (!original || original.__inkformGuarded) return;

  function warn(e) {
    console.warn("[Inkform] pointer lock refused (will retry on next click):", e && e.message ? e.message : e);
  }

  var guarded = function () {
    try {
      var result = original.apply(this, arguments);
      if (result && typeof result.catch === "function") result.catch(warn);
      return result;
    } catch (e) {
      warn(e);
      return undefined;
    }
  };
  guarded.__inkformGuarded = true;
  proto.requestPointerLock = guarded;
})();
