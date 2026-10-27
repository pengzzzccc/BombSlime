// Inkform: lets UIManager notice that the browser released the pointer lock (Esc in the page is
// eaten by the browser and never reaches the game).
mergeInto(LibraryManager.library, {
  InkformIsPointerLocked: function () {
    return document.pointerLockElement ? 1 : 0;
  }
});
