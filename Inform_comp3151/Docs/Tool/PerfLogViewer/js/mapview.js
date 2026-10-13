/* The level map: a pan/zoom canvas in world units (the perf log's x/y space). Level geometry is
   drawn from the exported tile bitsets as cached Path2D (crisp at every zoom) or from the rendered
   level art; heat fields, paths, segment arcs, checkpoints, hazards and death markers stack on top.
   Also hosts the ghost replay and the annotated PNG export. */
(function () {
  "use strict";
  const IK = (window.IK = window.IK || {});

  const DEFAULT_LAYERS = {
    deathHeat: true, lethality: false, presence: false, perf: false, mech: false, coverage: false, contours: false,
    paths: true, segments: true, checkpoints: true, hazards: true, doors: true, props: false, deaths: true,
  };
  const HEAT_LAYERS = ["presence", "lethality", "perf", "mech", "deathHeat"];
  const HEAT_RAMP = { deathHeat: "death", lethality: "lethal", presence: "presence", perf: "perf", mech: "mech" };
  // context layers sit quieter than the subject layers so stacking two never washes the level out
  const HEAT_ALPHA = { deathHeat: 0.92, lethality: 0.88, presence: 0.55, perf: 0.8, mech: 0.7 };
  const CLUSTER_ZOOM = 7;        // px per unit below which deaths cluster
  const CLUSTER_CELL = 38;       // px
  // label budgets by zoom: zoomed out, only the few hardest segments / deadliest hazards get text
  const labelBudget = z => (z < 2.5 ? 4 : z < 5 ? 8 : z < 9 ? 16 : Infinity);

  const ease = t => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const fmtTime = s => {
    if (s == null || !isFinite(s)) return "–";
    s = Math.max(0, s);
    const m = Math.floor(s / 60), r = Math.floor(s % 60);
    return `${m}:${String(r).padStart(2, "0")}`;
  };

  class MapView {
    constructor(opts) {
      this.canvas = opts.canvas;
      this.shell = opts.shell;
      this.ctx = this.canvas.getContext("2d");
      this.tip = opts.tooltip;
      this.legendEl = opts.legend;
      this.minimap = opts.minimap;
      this.scaleLabel = opts.scaleLabel;
      this.scaleRule = opts.scaleRule;
      this.hintEl = opts.hint;
      this.onReplayTick = opts.onReplayTick || (() => {});
      this.onLegendChange = opts.onLegendChange || (() => {});

      this.cam = { x: 0, y: 0, z: 4 };
      this.layers = { ...DEFAULT_LAYERS };
      this.opts = { baseMode: "art", dim: 0.45, mech: "wall", pathColor: "session" };
      try {
        const saved = JSON.parse(localStorage.getItem("ik.map") || "null");
        if (saved) { Object.assign(this.layers, saved.layers || {}); Object.assign(this.opts, saved.opts || {}); }
      } catch (e) { /* defaults */ }

      this.data = null;
      this.layout = null;
      this.geom = new Map();          // tilemap → {fill, edge}
      this.heat = {};                 // layer → {canvas, rect, top}
      this.coverageCache = null;
      this.contourCache = null;
      this.art = null;                // {img, rect, ready}
      this.artCache = new Map();
      this.hits = [];
      this.hover = null;
      this.highlight = null;
      this.replay = { on: false, playing: false, t: 0, speed: 4, last: 0, bursts: [], prevT: 0 };
      this.dirty = true;
      this.anim = null;
      this.vel = null;

      this.theme = IK.theme.current;
      this.resize();
      new ResizeObserver(() => this.resize()).observe(this.shell);
      this.bindInput();
      this.loop = this.loop.bind(this);
      requestAnimationFrame(this.loop);
    }

    persist() {
      try { localStorage.setItem("ik.map", JSON.stringify({ layers: this.layers, opts: this.opts })); } catch (e) { /* not persisted */ }
    }

    // ---------- data ----------

    setData(data, layout) {
      const sceneChanged = !this.data || !data || this.data.scene !== data.scene;
      const layoutChanged = this.layout !== layout;
      this.data = data;
      this.layout = layout;
      this.heat = {};
      this.coverageCache = null;
      this.contourCache = null;
      if (layoutChanged) this.geom = new Map();
      if (layoutChanged || sceneChanged) this.loadArt();
      if (sceneChanged || layoutChanged) { this.minimapBase = null; this.fit(true); }
      if (this.replay.t > (data ? data.maxTime : 0)) this.replay.t = 0;
      this.buildLegend();
      this.request();
    }

    setTheme() {
      this.theme = IK.theme.current;
      this.heat = {};
      this.contourCache = null;
      this.minimapBase = null;
      this.buildLegend();
      this.request();
    }

    setLayer(name, on) {
      this.layers[name] = on;
      this.persist();
      this.fadeIn(name);
      this.buildLegend();
      this.request();
    }

    setOption(name, value) {
      this.opts[name] = value;
      if (name === "mech") delete this.heat.mech;
      this.persist();
      this.buildLegend();
      this.request();
    }

    fadeIn(name) {
      this.fades = this.fades || {};
      this.fades[name] = performance.now();
    }
    fadeAlpha(name) {
      const f = this.fades && this.fades[name];
      if (!f) return 1;
      const a = (performance.now() - f) / 160;
      if (a >= 1) { delete this.fades[name]; return 1; }
      this.request();
      return a;
    }

    loadArt() {
      this.art = null;
      const L = this.layout;
      if (!L || !L.art) { this.updateHint(); this.onArtChange && this.onArtChange(); return; }
      const key = L.art.file;
      if (this.artCache.has(key)) { this.art = this.artCache.get(key); this.updateHint(); this.onArtChange && this.onArtChange(); return; }
      const img = new Image();
      const entry = { img, rect: L.art.rect, ready: false, failed: false };
      img.onload = () => { entry.ready = true; this.minimapBase = null; this.updateHint(); this.request(); this.onArtChange && this.onArtChange(); };
      img.onerror = () => { entry.failed = true; this.updateHint(); this.onArtChange && this.onArtChange(); };
      img.src = "data/" + encodeURIComponent(key);
      this.artCache.set(key, entry);
      this.art = entry;
      this.updateHint();
    }

    hasArt() { return !!(this.art && this.art.ready); }

    updateHint() {
      if (!this.hintEl) return;
      const noLayout = this.data && !this.layout;
      if (noLayout) {
        this.hintEl.textContent = IK.model.layouts ? IK.t("layout.sceneMissing") : IK.t("layout.missing").replace(/<[^>]+>/g, "");
        this.hintEl.classList.remove("hidden");
      } else this.hintEl.classList.add("hidden");
    }

    // ---------- camera ----------

    resize() {
      // layout size, not getBoundingClientRect: a CSS transform on an ancestor must not shrink the backing store
      this.dpr = Math.min(window.devicePixelRatio || 1, 2.5);
      this.W = Math.max(1, this.shell.offsetWidth);
      this.H = Math.max(1, this.shell.offsetHeight);
      this.canvas.width = Math.round(this.W * this.dpr);
      this.canvas.height = Math.round(this.H * this.dpr);
      if (!this.userMoved && this.data) this.fit(true);
      this.request();
    }

    worldBounds() {
      if (this.layout && this.layout.bounds) return this.layout.bounds;
      if (this.data && this.data.bounds) return this.data.bounds;
      return [0, 0, 100, 60];
    }

    fitZoom() {
      const b = this.worldBounds();
      return Math.min((this.W - 80) / Math.max(1, b[2] - b[0]), (this.H - 120) / Math.max(1, b[3] - b[1]));
    }

    fit(instant) {
      this.userMoved = false;
      const b = this.worldBounds();
      this.moveTo((b[0] + b[2]) / 2, (b[1] + b[3]) / 2, this.fitZoom(), instant, true);
    }

    moveTo(x, y, z, instant, keepFit) {
      if (!keepFit) this.userMoved = true;
      z = clamp(z, this.fitZoom() * 0.5, 64);
      if (instant) { this.cam = { x, y, z }; this.anim = null; this.request(); return; }
      this.anim = { from: { ...this.cam }, to: { x, y, z }, start: performance.now(), dur: 520 };
      this.request();
    }

    focusHottest() {
      const d = this.data;
      if (!d || !d.deaths.length) return;
      const g = new IK.heat.Grid(this.worldBounds(), 2, 2);
      for (const p of d.deaths) g.add(p.x, p.y, 1);
      IK.heat.blur(g, 3);
      let bi = 0;
      for (let i = 1; i < g.v.length; i++) if (g.v[i] > g.v[bi]) bi = i;
      const x = g.x0 + ((bi % g.w) + 0.5) * g.cell, y = g.y0 + (Math.floor(bi / g.w) + 0.5) * g.cell;
      this.moveTo(x, y, Math.max(16, this.cam.z));
    }

    focusSegment(seg) {
      const pts = [];
      if (seg.fromPos) pts.push(seg.fromPos);
      if (seg.toPos) pts.push(seg.toPos);
      for (const g of seg.instances) {
        for (const d of this.data.deathsAll) if (d.session === g.session && d.seg === g.seg && d.seg != null) pts.push([d.x, d.y]);
      }
      if (!pts.length) return;
      const xs = pts.map(p => p[0]), ys = pts.map(p => p[1]);
      const x0 = Math.min(...xs), x1 = Math.max(...xs), y0 = Math.min(...ys), y1 = Math.max(...ys);
      const z = Math.min((this.W - 200) / Math.max(8, x1 - x0), (this.H - 200) / Math.max(8, y1 - y0));
      this.moveTo((x0 + x1) / 2, (y0 + y1) / 2, clamp(z, 6, 40));
      this.flash = { seg, until: performance.now() + 1800 };
    }

    toScreen(x, y) { return [(x - this.cam.x) * this.cam.z + this.W / 2, this.H / 2 - (y - this.cam.y) * this.cam.z]; }
    toWorld(sx, sy) { return [(sx - this.W / 2) / this.cam.z + this.cam.x, (this.H / 2 - sy) / this.cam.z + this.cam.y]; }

    // ---------- input ----------

    bindInput() {
      const c = this.canvas;
      const pointers = new Map();
      let drag = null, pinch = null;

      c.addEventListener("pointerdown", e => {
        c.setPointerCapture(e.pointerId);
        pointers.set(e.pointerId, { x: e.offsetX, y: e.offsetY });
        this.anim = null; this.vel = null;
        this.userMoved = true;
        if (pointers.size === 1) drag = { x: e.offsetX, y: e.offsetY, t: performance.now(), vx: 0, vy: 0, moved: 0 };
        if (pointers.size === 2) {
          const [a, b] = [...pointers.values()];
          pinch = { d: Math.hypot(a.x - b.x, a.y - b.y), z: this.cam.z };
          drag = null;
        }
      });
      c.addEventListener("pointermove", e => {
        const p = pointers.get(e.pointerId);
        if (p) { p.x = e.offsetX; p.y = e.offsetY; }
        if (pinch && pointers.size === 2) {
          const [a, b] = [...pointers.values()];
          const mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
          this.zoomAt(mx, my, (pinch.z * Math.hypot(a.x - b.x, a.y - b.y)) / pinch.d / this.cam.z);
          return;
        }
        if (drag) {
          const dx = e.offsetX - drag.x, dy = e.offsetY - drag.y;
          drag.moved += Math.abs(dx) + Math.abs(dy);
          const now = performance.now(), dt = Math.max(1, now - drag.t);
          drag.vx = dx / dt; drag.vy = dy / dt; drag.t = now;
          this.cam.x -= dx / this.cam.z;
          this.cam.y += dy / this.cam.z;
          drag.x = e.offsetX; drag.y = e.offsetY;
          if (drag.moved > 3) c.classList.add("dragging");
          this.hideTip();
          this.request();
          return;
        }
        this.pointer = { x: e.offsetX, y: e.offsetY };
        this.hoverTest();
      });
      const end = e => {
        pointers.delete(e.pointerId);
        if (pinch && pointers.size < 2) pinch = null;
        if (drag) {
          if (performance.now() - drag.t < 60 && Math.hypot(drag.vx, drag.vy) > 0.15) this.vel = { x: drag.vx * 16, y: drag.vy * 16 };
          if (drag.moved <= 3 && this.hover && this.onClick) this.onClick(this.hover);
        }
        drag = null;
        c.classList.remove("dragging");
      };
      c.addEventListener("pointerup", end);
      c.addEventListener("pointercancel", end);
      c.addEventListener("pointerleave", () => { this.pointer = null; this.hover = null; this.hideTip(); this.request(); });
      c.addEventListener("wheel", e => {
        e.preventDefault();
        const k = Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.0016));
        this.anim = null;
        this.userMoved = true;
        this.zoomAt(e.offsetX, e.offsetY, k);
      }, { passive: false });
      c.addEventListener("dblclick", () => this.focusHottest());
      c.addEventListener("keydown", e => {
        const step = 80 / this.cam.z;
        if (e.key === "+" || e.key === "=") this.zoomAt(this.W / 2, this.H / 2, 1.3);
        else if (e.key === "-") this.zoomAt(this.W / 2, this.H / 2, 1 / 1.3);
        else if (e.key === "0") this.fit();
        else if (e.key === "ArrowLeft") this.cam.x -= step;
        else if (e.key === "ArrowRight") this.cam.x += step;
        else if (e.key === "ArrowUp") this.cam.y += step;
        else if (e.key === "ArrowDown") this.cam.y -= step;
        else if (e.key === " " && this.replay.on) { this.toggleReplayPlay(); }
        else return;
        e.preventDefault();
        this.request();
      });

      // minimap: click or drag to move
      if (this.minimap) {
        let mm = false;
        const go = e => {
          const r = this.minimap.getBoundingClientRect();
          const b = this.worldBounds();
          const fx = (e.clientX - r.left) / r.width, fy = (e.clientY - r.top) / r.height;
          this.cam.x = b[0] + fx * (b[2] - b[0]);
          this.cam.y = b[3] - fy * (b[3] - b[1]);
          this.anim = null;
          this.request();
        };
        this.minimap.addEventListener("pointerdown", e => { mm = true; this.minimap.setPointerCapture(e.pointerId); go(e); });
        this.minimap.addEventListener("pointermove", e => mm && go(e));
        this.minimap.addEventListener("pointerup", () => (mm = false));
      }
    }

    zoomAt(sx, sy, k) {
      const [wx, wy] = this.toWorld(sx, sy);
      const z = clamp(this.cam.z * k, this.fitZoom() * 0.5, 64);
      this.cam.z = z;
      this.cam.x = wx - (sx - this.W / 2) / z;
      this.cam.y = wy + (sy - this.H / 2) / z;
      this.hideTip();
      this.request();
    }

    request() { this.dirty = true; }

    loop(now) {
      if (this.anim) {
        const a = this.anim, t = clamp((now - a.start) / a.dur, 0, 1), e = ease(t);
        // zoom interpolates in log space so a long zoom feels even
        this.cam.z = Math.exp(Math.log(a.from.z) + (Math.log(a.to.z) - Math.log(a.from.z)) * e);
        this.cam.x = a.from.x + (a.to.x - a.from.x) * e;
        this.cam.y = a.from.y + (a.to.y - a.from.y) * e;
        if (t >= 1) this.anim = null;
        this.dirty = true;
      }
      if (this.vel) {
        this.cam.x -= this.vel.x / this.cam.z;
        this.cam.y += this.vel.y / this.cam.z;
        this.vel.x *= 0.9; this.vel.y *= 0.9;
        if (Math.hypot(this.vel.x, this.vel.y) < 0.2) this.vel = null;
        this.dirty = true;
      }
      if (this.replay.on) {
        if (this.replay.playing) {
          const dt = this.replay.last ? Math.min(0.1, (now - this.replay.last) / 1000) : 0;
          this.replay.prevT = this.replay.t;
          this.replay.t += dt * this.replay.speed;
          if (this.data && this.replay.t >= this.data.maxTime) { this.replay.t = this.data.maxTime; this.replay.playing = false; }
          this.spawnBursts(this.replay.prevT, this.replay.t);
          this.onReplayTick(this.replay);
        }
        this.replay.last = now;
        if (this.replay.playing || this.replay.bursts.length) this.dirty = true;
      }
      if (this.flash && now < this.flash.until) this.dirty = true;
      if (this.dirty && this.W > 1) {
        this.dirty = false;
        this.draw(this.ctx, this.W, this.H, this.dpr);
        this.drawMinimap();
        this.updateScalebar();
      }
      requestAnimationFrame(this.loop);
    }

    // ---------- drawing ----------

    draw(ctx, W, H, dpr, forExport) {
      const T = this.theme;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.globalCompositeOperation = "source-over";
      ctx.globalAlpha = 1;
      ctx.fillStyle = T.map.bg;
      ctx.fillRect(0, 0, W, H);
      this.hits = [];
      const d = this.data;

      const mode = this.opts.baseMode;
      const artOk = this.hasArt();
      const showArt = artOk && mode !== "schematic";
      const showSchematic = !!this.layout && (mode === "schematic" || !artOk);
      if (showArt) this.drawArt(ctx, dpr);
      if (showSchematic) this.drawSchematic(ctx, dpr, false);
      else if (showArt && mode === "blend") this.drawSchematic(ctx, dpr, true);
      if (!this.layout) this.drawGrid(ctx, W, H);
      if (!d) return;

      const replay = this.replay.on;
      if (this.layers.coverage && this.layout) this.drawCoverage(ctx, dpr);
      for (const name of HEAT_LAYERS) if (this.layers[name] && !replay) this.drawHeat(ctx, name);
      if (this.layers.contours && !replay) this.drawContours(ctx, dpr);
      if (this.layers.paths) this.drawPaths(ctx, dpr, replay ? 0.18 : 1);
      if (this.layers.doors && this.layout) this.drawDoors(ctx);
      if (this.layers.props && this.layout) this.drawProps(ctx);
      if (this.layers.segments && !replay) this.drawSegments(ctx);
      if (this.layers.checkpoints) this.drawCheckpoints(ctx);
      if (this.layers.hazards) this.drawHazards(ctx);
      if (this.layers.deaths) this.drawDeaths(ctx, replay ? this.replay.t : Infinity);
      if (replay) this.drawGhosts(ctx);
      if (!forExport) this.drawHover(ctx);
    }

    worldTransform(ctx, dpr) {
      const z = this.cam.z;
      ctx.setTransform(dpr * z, 0, 0, -dpr * z, dpr * (this.W / 2 - this.cam.x * z), dpr * (this.H / 2 + this.cam.y * z));
    }

    drawArt(ctx, dpr) {
      const a = this.art, r = a.rect;
      const [sx, sy] = this.toScreen(r[0], r[1] + r[3]);
      ctx.save();
      ctx.imageSmoothingEnabled = this.cam.z < (this.layout.art.ppu || 12) * 1.5;
      ctx.imageSmoothingQuality = "high";
      if ("filter" in ctx) ctx.filter = "saturate(0.6)";
      ctx.drawImage(a.img, sx, sy, r[2] * this.cam.z, r[3] * this.cam.z);
      ctx.restore();
      // dim toward the page color so overlays stay the brightest thing on screen
      ctx.save();
      ctx.globalAlpha = clamp(1 - this.opts.dim, 0, 0.9);
      ctx.fillStyle = this.theme.page;
      ctx.fillRect(sx, sy, r[2] * this.cam.z, r[3] * this.cam.z);
      ctx.restore();
    }

    geometry(t) {
      let g = this.geom.get(t);
      if (g) return g;
      const bits = IK.mapBits(t);
      const fill = new Path2D(), edge = new Path2D();
      const on = (x, y) => {
        if (x < 0 || y < 0 || x >= t.w || y >= t.h) return false;
        const i = x + y * t.w;
        return (bits[i >> 3] & (1 << (i & 7))) !== 0;
      };
      const [ox, oy] = t.origin, [cw, ch] = t.cell;
      for (let y = 0; y < t.h; y++) {
        let run = -1;
        for (let x = 0; x <= t.w; x++) {
          const f = x < t.w && on(x, y);
          if (f && run < 0) run = x;
          if (!f && run >= 0) { fill.rect(ox + run * cw, oy + y * ch, (x - run) * cw, ch); run = -1; }
        }
      }
      // boundary edges, merged into runs: horizontal (between rows) and vertical (between columns)
      for (let y = 0; y <= t.h; y++) {
        let run = -1;
        for (let x = 0; x <= t.w; x++) {
          const e = x < t.w && on(x, y) !== on(x, y - 1);
          if (e && run < 0) run = x;
          if (!e && run >= 0) { edge.moveTo(ox + run * cw, oy + y * ch); edge.lineTo(ox + x * cw, oy + y * ch); run = -1; }
        }
      }
      for (let x = 0; x <= t.w; x++) {
        let run = -1;
        for (let y = 0; y <= t.h; y++) {
          const e = y < t.h && on(x, y) !== on(x - 1, y);
          if (e && run < 0) run = y;
          if (!e && run >= 0) { edge.moveTo(ox + x * cw, oy + run * ch); edge.lineTo(ox + x * cw, oy + y * ch); run = -1; }
        }
      }
      g = { fill, edge };
      this.geom.set(t, g);
      return g;
    }

    drawSchematic(ctx, dpr, edgesOnly) {
      const M = this.theme.map;
      const tms = (this.layout.tilemaps || []).slice().reverse();
      this.worldTransform(ctx, dpr);
      const px = 1 / this.cam.z;
      for (const t of tms) {
        const g = this.geometry(t);
        if (t.kind === "background") {
          if (edgesOnly) continue;
          ctx.fillStyle = M.bgtile; ctx.fill(g.fill);
        } else if (t.kind === "decor") {
          if (edgesOnly) continue;
          ctx.fillStyle = M.decor; ctx.fill(g.fill);
        } else if (t.kind === "hazard") {
          ctx.globalAlpha = edgesOnly ? 0.75 : 1;
          ctx.fillStyle = M.hazard; ctx.fill(g.fill);
          ctx.strokeStyle = M.hazardEdge; ctx.lineWidth = 1.2 * px; ctx.stroke(g.edge);
          ctx.globalAlpha = 1;
        } else {
          if (!edgesOnly) { ctx.fillStyle = t.kind === "platform" ? M.platform : M.terrain; ctx.fill(g.fill); }
          ctx.strokeStyle = edgesOnly ? IK.theme.rgba(this.theme.dark ? "#ffffff" : "#000000", 0.35) : M.terrainEdge;
          ctx.lineWidth = (edgesOnly ? 1 : 1.25) * px;
          ctx.stroke(g.edge);
        }
      }
      // fine unit grid once zoomed in far enough to read cells
      if (!edgesOnly && this.cam.z >= 22) this.drawUnitGrid(ctx, px);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    drawUnitGrid(ctx, px) {
      const [x0, y1] = this.toWorld(0, 0), [x1, y0] = this.toWorld(this.W, this.H);
      ctx.beginPath();
      for (let x = Math.floor(x0); x <= x1; x++) { ctx.moveTo(x, y0); ctx.lineTo(x, y1); }
      for (let y = Math.floor(y0); y <= y1; y++) { ctx.moveTo(x0, y); ctx.lineTo(x1, y); }
      ctx.strokeStyle = IK.theme.rgba(this.theme.dark ? "#ffffff" : "#000000", 0.05);
      ctx.lineWidth = px;
      ctx.stroke();
    }

    drawGrid(ctx, W, H) {
      const z = this.cam.z;
      const step = z > 12 ? 1 : z > 3 ? 10 : 50;
      const [x0, y1] = this.toWorld(0, 0), [x1, y0] = this.toWorld(W, H);
      ctx.beginPath();
      for (let x = Math.floor(x0 / step) * step; x <= x1; x += step) { const [sx] = this.toScreen(x, 0); ctx.moveTo(Math.round(sx) + 0.5, 0); ctx.lineTo(Math.round(sx) + 0.5, H); }
      for (let y = Math.floor(y0 / step) * step; y <= y1; y += step) { const [, sy] = this.toScreen(0, y); ctx.moveTo(0, Math.round(sy) + 0.5); ctx.lineTo(W, Math.round(sy) + 0.5); }
      ctx.strokeStyle = this.theme.grid;
      ctx.lineWidth = 1;
      ctx.stroke();
    }

    heatFor(name) {
      if (this.heat[name]) return this.heat[name];
      const d = this.data, b = this.worldBounds();
      let grid = null;
      if (name === "deathHeat") grid = IK.heat.deathField(d, b);
      else if (name === "presence") grid = IK.heat.presenceField(d, b);
      else if (name === "lethality") grid = IK.heat.lethalityField(d, b);
      else if (name === "perf") grid = IK.heat.perfField(d, b);
      else if (name === "mech") grid = IK.heat.mechField(d, b, this.opts.mech);
      // lethality is a ratio: scale to what the data reaches (never below 10%) and say so on the legend
      const top = name === "lethality" ? Math.max(0.1, grid.top(0.99)) : grid.top(0.995);
      const r = IK.heat.render(grid, this.theme.ramps[HEAT_RAMP[name]], top, name === "lethality" ? 0.04 : 0.025);
      r.grid = grid;
      this.heat[name] = r;
      return r;
    }

    drawHeat(ctx, name) {
      const h = this.heatFor(name);
      if (!h.top) return;
      const r = h.rect;
      const [sx, sy] = this.toScreen(r[0], r[3]);
      ctx.save();
      ctx.globalCompositeOperation = this.theme.map.blend;
      ctx.globalAlpha = HEAT_ALPHA[name] * this.fadeAlpha(name);
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = "high";
      ctx.drawImage(h.canvas, sx, sy, (r[2] - r[0]) * this.cam.z, (r[3] - r[1]) * this.cam.z);
      ctx.restore();
    }

    drawContours(ctx, dpr) {
      if (!this.contourCache) {
        const h = this.heatFor("deathHeat");
        this.contourCache = h.top ? IK.heat.contours(h.grid, [0.2, 0.45, 0.75], h.top) : [];
      }
      const segs = this.contourCache;
      if (!segs.length) return;
      this.worldTransform(ctx, dpr);
      const peak = this.theme.ramps.death.stops[2], mid = this.theme.ramps.death.stops[1];
      for (const level of [0.2, 0.45, 0.75]) {
        ctx.beginPath();
        for (let i = 0; i < segs.length; i += 5) {
          if (segs[i + 4] !== level) continue;
          ctx.moveTo(segs[i], segs[i + 1]); ctx.lineTo(segs[i + 2], segs[i + 3]);
        }
        ctx.strokeStyle = level > 0.5 ? peak : mid;
        ctx.globalAlpha = 0.45 + level * 0.6;
        ctx.lineWidth = (0.8 + level * 1.2) / this.cam.z;
        ctx.stroke();
      }
      ctx.globalAlpha = 1;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    drawCoverage(ctx, dpr) {
      if (!this.coverageCache) this.coverageCache = IK.heat.coverage(this.data, this.layout) || { runs: [] };
      const runs = this.coverageCache.runs;
      if (!runs.length) return;
      this.worldTransform(ctx, dpr);
      ctx.fillStyle = IK.theme.rgba(this.theme.coverage, 0.85);
      ctx.globalAlpha = this.fadeAlpha("coverage");
      const hgt = Math.max(0.3, 3 / this.cam.z);
      for (const [x, y, w] of runs) ctx.fillRect(x, y, w, hgt);
      ctx.globalAlpha = 1;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    pathColor(path) {
      return IK.theme.sessionColor(path.session.slot);
    }

    drawPaths(ctx, dpr, alphaScale) {
      const d = this.data, z = this.cam.z;
      const [vx0, vy1] = this.toWorld(-20, -20), [vx1, vy0] = this.toWorld(this.W + 20, this.H + 20);
      const minStep = 1.5 / z;                  // decimate points closer than 1.5 px
      const byTime = this.opts.pathColor === "time";
      const lut = this.theme.ramps.presence.lut;
      const hl = this.highlight;
      ctx.save();
      this.worldTransform(ctx, dpr);
      ctx.lineJoin = "round"; ctx.lineCap = "round";
      ctx.lineWidth = 2 / z;
      const fade = this.fadeAlpha("paths");
      for (const p of d.paths) {
        const pts = p.pts;
        if (!p.bbox) {
          let a = Infinity, b = Infinity, c = -Infinity, e = -Infinity;
          for (let i = 0; i < pts.length; i += 3) { a = Math.min(a, pts[i]); b = Math.min(b, pts[i + 1]); c = Math.max(c, pts[i]); e = Math.max(e, pts[i + 1]); }
          p.bbox = [a, b, c, e];
        }
        const bb = p.bbox;
        if (bb[2] < vx0 || bb[0] > vx1 || bb[3] < vy0 || bb[1] > vy1) continue;
        const dim = hl && hl !== p.session.id ? 0.15 : 1;
        const base = this.pathColor(p);
        const tEnd = Math.max(1, d.maxTime);
        if (!byTime) {
          ctx.strokeStyle = base;
          ctx.globalAlpha = 0.7 * alphaScale * dim * fade;
          ctx.beginPath();
          let lx = pts[0], ly = pts[1];
          ctx.moveTo(lx, ly);
          for (let i = 3; i < pts.length; i += 3) {
            const x = pts[i], y = pts[i + 1];
            if (i + 3 < pts.length && Math.abs(x - lx) < minStep && Math.abs(y - ly) < minStep) continue;
            ctx.lineTo(x, y); lx = x; ly = y;
          }
          ctx.stroke();
        } else {
          // time mode: short runs colored along the presence ramp by session time
          const CH = 6;
          for (let s = 0; s + 3 < pts.length; s += CH * 3) {
            const li = Math.round(clamp(pts[s + 2] / tEnd, 0, 1) * 200 + 55) * 4;
            ctx.strokeStyle = `rgb(${lut[li]},${lut[li + 1]},${lut[li + 2]})`;
            ctx.globalAlpha = 0.85 * alphaScale * dim * fade;
            ctx.beginPath();
            ctx.moveTo(pts[s], pts[s + 1]);
            for (let i = s + 3; i < Math.min(pts.length, s + CH * 3 + 3); i += 3) ctx.lineTo(pts[i], pts[i + 1]);
            ctx.stroke();
          }
        }
      }
      // respawn jumps: thin dashed links from the death to where the player came back
      if (alphaScale >= 1 && z > 2) {
        ctx.setLineDash([4 / z, 4 / z]);
        ctx.lineWidth = 1 / z;
        ctx.strokeStyle = this.theme.ink3;
        ctx.globalAlpha = 0.5 * fade;
        ctx.beginPath();
        for (const j of d.jumps) {
          if (hl && hl !== j.session.id) continue;
          ctx.moveTo(j.x0, j.y0); ctx.lineTo(j.x1, j.y1);
        }
        ctx.stroke();
        ctx.setLineDash([]);
      }
      ctx.restore();

      // direction chevrons, sparse, only once zoomed in enough to read them
      if (z >= 5 && alphaScale >= 1) {
        ctx.save();
        ctx.lineWidth = 1.5; ctx.lineCap = "round"; ctx.lineJoin = "round";
        for (const p of d.paths) {
          const bb = p.bbox;
          if (!bb || bb[2] < vx0 || bb[0] > vx1 || bb[3] < vy0 || bb[1] > vy1) continue;
          if (hl && hl !== p.session.id) continue;
          let acc = 0;
          const pts = p.pts;
          ctx.strokeStyle = byTime ? this.theme.ink2 : this.pathColor(p);
          ctx.globalAlpha = 0.85;
          for (let i = 3; i < pts.length; i += 3) {
            const [ax, ay] = this.toScreen(pts[i - 3], pts[i - 2]), [bx, by] = this.toScreen(pts[i], pts[i + 1]);
            const len = Math.hypot(bx - ax, by - ay);
            acc += len;
            if (acc < 90 || len < 6) continue;
            acc = 0;
            const mx = (ax + bx) / 2, my = (ay + by) / 2, ang = Math.atan2(by - ay, bx - ax);
            ctx.beginPath();
            ctx.moveTo(mx - Math.cos(ang - 0.6) * 5, my - Math.sin(ang - 0.6) * 5);
            ctx.lineTo(mx, my);
            ctx.lineTo(mx - Math.cos(ang + 0.6) * 5, my - Math.sin(ang + 0.6) * 5);
            ctx.stroke();
          }
        }
        ctx.restore();
      }
    }

    drawDoors(ctx) {
      const T = this.theme;
      ctx.save();
      for (const door of this.layout.doors || []) {
        const b = door.box;
        const [x0, y0] = this.toScreen(b[0], b[3]), [x1, y1] = this.toScreen(b[2], b[1]);
        if (x1 < -50 || x0 > this.W + 50 || y1 < -50 || y0 > this.H + 50) continue;
        ctx.strokeStyle = T.ink2; ctx.lineWidth = 1.5; ctx.globalAlpha = 0.9;
        roundRect(ctx, x0, y0, Math.max(6, x1 - x0), Math.max(8, y1 - y0), 3);
        ctx.stroke();
        ctx.fillStyle = IK.theme.rgba(T.ink1, 0.06); ctx.fill();
        const label = "→ " + (door.to || "?");
        this.pill(ctx, (x0 + x1) / 2, y0 - 12, label, T.ink3, false, 11);
        this.hits.push({ type: "door", x: (x0 + x1) / 2, y: (y0 + y1) / 2, r: Math.max(14, (x1 - x0) / 2 + 4), data: door });
      }
      ctx.restore();
    }

    drawProps(ctx) {
      const T = this.theme;
      if (this.cam.z < 2.5) return;
      ctx.save();
      for (const p of this.layout.props || []) {
        const [sx, sy] = this.toScreen(p.pos[0], p.pos[1]);
        if (sx < -20 || sx > this.W + 20 || sy < -20 || sy > this.H + 20) continue;
        ctx.globalAlpha = 0.8;
        if (p.kind === "platform") {
          const b = p.box, [a0, a1] = this.toScreen(b[0], b[3]), [c0, c1] = this.toScreen(b[2], b[1]);
          ctx.strokeStyle = T.series[0]; ctx.lineWidth = 1.5;
          ctx.strokeRect(a0, a1, c0 - a0, c1 - a1);
          if (p.path) this.patrol(ctx, p.path, p.loop, T.series[0]);
        } else if (p.kind === "pickup") {
          ctx.fillStyle = T.series[3];
          IK.theme.shapePath(ctx, "star", sx, sy, 6); ctx.fill();
        } else if (p.kind === "breakable") {
          ctx.strokeStyle = T.ink2; ctx.lineWidth = 1.2;
          ctx.strokeRect(sx - 5, sy - 5, 10, 10);
          ctx.beginPath(); ctx.moveTo(sx - 5, sy + 5); ctx.lineTo(sx + 5, sy - 5); ctx.stroke();
        } else {
          ctx.fillStyle = T.ink3;
          ctx.beginPath(); ctx.arc(sx, sy, 2.5, 0, Math.PI * 2); ctx.fill();
        }
        this.hits.push({ type: "prop", x: sx, y: sy, r: 10, data: p });
      }
      ctx.restore();
    }

    patrol(ctx, path, loop, color) {
      ctx.save();
      ctx.strokeStyle = color; ctx.globalAlpha = 0.45; ctx.lineWidth = 1;
      ctx.setLineDash([3, 3]);
      ctx.beginPath();
      path.forEach((p, i) => { const [x, y] = this.toScreen(p[0], p[1]); i ? ctx.lineTo(x, y) : ctx.moveTo(x, y); });
      if (loop) ctx.closePath();
      ctx.stroke();
      ctx.restore();
    }

    segArc(seg) {
      const [ax, ay] = this.toScreen(seg.fromPos[0], seg.fromPos[1]);
      const [bx, by] = this.toScreen(seg.toPos[0], seg.toPos[1]);
      const dx = bx - ax, dy = by - ay, dist = Math.hypot(dx, dy);
      // bow upward (screen) so arcs clear the ground they connect along
      let nx = -dy / (dist || 1), ny = dx / (dist || 1);
      if (ny > 0) { nx = -nx; ny = -ny; }
      const bow = Math.min(dist * 0.28, 90);
      const cx = (ax + bx) / 2 + nx * bow, cy = (ay + by) / 2 + ny * bow;
      return { ax, ay, bx, by, cx, cy, dist, mx: 0.25 * ax + 0.5 * cx + 0.25 * bx, my: 0.25 * ay + 0.5 * cy + 0.25 * by };
    }

    drawSegments(ctx) {
      const d = this.data, T = this.theme;
      const segs = d.segments.filter(s => s.fromPos && s.toPos && s.from !== s.to);
      if (!segs.length) return;
      const fade = this.fadeAlpha("segments");
      const placed = [];
      const flash = this.flash && performance.now() < this.flash.until ? this.flash.seg : null;
      ctx.save();
      // arcs first, easiest underneath
      const ordered = segs.slice().sort((a, b) => a.mean - b.mean);
      for (const s of ordered) {
        const a = this.segArc(s);
        if (a.dist < 6) continue;
        const rating = IK.theme.rating(s.mean);
        const color = T.status[rating.status];
        const hot = flash === s || (this.hover && this.hover.data === s);
        // an arc much longer than the view is context, not the subject: keep it quiet
        const long = a.dist > Math.max(this.W, this.H) * 1.2;
        ctx.globalAlpha = (hot ? 1 : long ? 0.3 : 0.85) * fade;
        ctx.strokeStyle = color;
        ctx.lineWidth = hot ? 4.5 : 2.5 + Math.min(2, s.mean * 0.6);
        ctx.lineCap = "round";
        ctx.beginPath(); ctx.moveTo(a.ax, a.ay); ctx.quadraticCurveTo(a.cx, a.cy, a.bx, a.by); ctx.stroke();
        // arrowhead along the end tangent
        const ang = Math.atan2(a.by - a.cy, a.bx - a.cx);
        ctx.fillStyle = color;
        ctx.beginPath();
        ctx.moveTo(a.bx - Math.cos(ang) * 2, a.by - Math.sin(ang) * 2);
        ctx.lineTo(a.bx - Math.cos(ang - 0.45) * 11, a.by - Math.sin(ang - 0.45) * 11);
        ctx.lineTo(a.bx - Math.cos(ang + 0.45) * 11, a.by - Math.sin(ang + 0.45) * 11);
        ctx.closePath(); ctx.fill();
      }
      // labels: hardest first, skip any that would collide with one already placed
      const byHard = segs.slice().sort((a, b) => b.mean - a.mean || b.total - a.total);
      let budget = labelBudget(this.cam.z);
      for (const s of byHard) {
        const a = this.segArc(s);
        if (a.dist < 6) continue;
        const rating = IK.theme.rating(s.mean);
        const color = T.status[rating.status];
        const txt = this.segPillText(s);
        ctx.font = `600 11px ${T.font}`;
        const w = ctx.measureText(txt).width + 16, h = 20;
        const box = [a.mx - w / 2, a.my - h / 2, w, h];
        const hot = flash === s || (this.hover && this.hover.data === s);
        const collide = placed.some(p => box[0] < p[0] + p[2] + 4 && box[0] + w + 4 > p[0] && box[1] < p[1] + p[3] + 3 && box[1] + h + 3 > p[1]);
        this.hits.push({ type: "segment", x: a.mx, y: a.my, r: Math.max(16, w / 2), data: s });
        if (!hot && (collide || budget <= 0 || (budget < 20 && s.total === 0))) continue;
        budget--;
        placed.push(box);
        ctx.globalAlpha = fade;
        this.pill(ctx, a.mx, a.my, txt, color, true, 11);
      }
      ctx.restore();
    }

    segPillText(s) {
      const parts = [`☠ ${s.total}`];
      if (s.medianTime != null) parts.push(fmtTime(s.medianTime));
      if (s.firstTry != null) parts.push(Math.round(s.firstTry * 100) + "%");
      return parts.join(" · ");
    }

    pill(ctx, x, y, text, accent, strong, size) {
      const T = this.theme;
      ctx.save();
      ctx.font = `${strong ? 600 : 500} ${size || 11}px ${T.font}`;
      const w = ctx.measureText(text).width + 14, h = (size || 11) + 9;
      ctx.fillStyle = IK.theme.rgba(T.surface1, 0.92);
      roundRect(ctx, x - w / 2, y - h / 2, w, h, h / 2);
      ctx.fill();
      ctx.lineWidth = strong ? 1.5 : 1;
      ctx.strokeStyle = strong ? accent : T.border;
      ctx.stroke();
      ctx.fillStyle = strong ? T.ink1 : T.ink2;
      ctx.textAlign = "center"; ctx.textBaseline = "middle";
      ctx.fillText(text, x, y + 0.5);
      ctx.restore();
    }

    drawCheckpoints(ctx) {
      const T = this.theme, d = this.data;
      const small = this.cam.z < 3;
      ctx.save();
      ctx.font = `700 10px ${T.font}`;
      ctx.textAlign = "center"; ctx.textBaseline = "middle";
      for (const cp of d.checkpoints) {
        const [sx, sy] = this.toScreen(cp.x, cp.y);
        if (sx < -20 || sx > this.W + 20 || sy < -30 || sy > this.H + 20) continue;
        const reached = cp.reachedBy > 0;
        const r = small ? 6 : 9;
        const py = sy - (small ? 8 : 14);
        ctx.globalAlpha = reached ? 1 : 0.5;
        // stem
        ctx.strokeStyle = cp.start ? T.accent : T.ink2; ctx.lineWidth = 1.5;
        ctx.beginPath(); ctx.moveTo(sx, sy); ctx.lineTo(sx, py + r - 1); ctx.stroke();
        ctx.fillStyle = cp.start ? T.accent : T.surface1;
        ctx.beginPath(); ctx.arc(sx, py, r, 0, Math.PI * 2); ctx.fill();
        ctx.lineWidth = 2; ctx.strokeStyle = cp.start ? T.surface1 : (reached ? T.ink1 : T.ink3);
        ctx.stroke();
        ctx.beginPath(); ctx.arc(sx, sy, 2, 0, Math.PI * 2); ctx.fillStyle = cp.start ? T.accent : T.ink2; ctx.fill();
        if (!small) { ctx.fillStyle = cp.start ? "#ffffff" : T.ink1; ctx.fillText(String(cp.order), sx, py + 0.5); }
        this.hits.push({ type: "checkpoint", x: sx, y: py, r: r + 6, data: cp });
      }
      ctx.restore();
    }

    hazardGlyph(ctx, h, sx, sy, r, color) {
      ctx.save();
      ctx.strokeStyle = color; ctx.fillStyle = color; ctx.lineWidth = 1.5;
      const prefab = (h.prefab || "").toLowerCase();
      if (prefab.startsWith("gear") || h.spin) {
        ctx.beginPath(); ctx.arc(sx, sy, r, 0, Math.PI * 2); ctx.stroke();
        for (let i = 0; i < 8; i++) {
          const a = (i * Math.PI) / 4;
          ctx.beginPath(); ctx.moveTo(sx + Math.cos(a) * r, sy + Math.sin(a) * r); ctx.lineTo(sx + Math.cos(a) * (r + 2.5), sy + Math.sin(a) * (r + 2.5)); ctx.stroke();
        }
      } else if (prefab.startsWith("laser")) {
        ctx.fillRect(sx - 1.5, sy - r * 1.3, 3, r * 2.6);
      } else if (prefab.includes("cursher") || prefab.includes("crusher")) {
        ctx.strokeRect(sx - r, sy - r, r * 2, r * 2);
        ctx.beginPath(); ctx.moveTo(sx - r, sy + r * 0.3); ctx.lineTo(sx + r, sy + r * 0.3); ctx.stroke();
      } else {
        IK.theme.shapePath(ctx, "triangle", sx, sy, r); ctx.stroke();
      }
      ctx.restore();
    }

    drawHazards(ctx) {
      const T = this.theme, d = this.data;
      ctx.save();
      // every placed hazard, faint, with its patrol route
      if (this.layout && this.cam.z >= 2) {
        for (const h of this.layout.hazards || []) {
          const [sx, sy] = this.toScreen(h.pos[0], h.pos[1]);
          if (sx < -30 || sx > this.W + 30 || sy < -30 || sy > this.H + 30) continue;
          if (h.path && this.cam.z >= 4) this.patrol(ctx, h.path, h.loop, T.ink3);
          ctx.globalAlpha = 0.55;
          this.hazardGlyph(ctx, h, sx, sy, this.cam.z >= 8 ? 5 : 3.5, T.ink3);
          this.hits.push({ type: "hazard", x: sx, y: sy, r: 9, data: { name: h.name, prefab: h.prefab, cause: h.cause, kills: 0, layout: h } });
        }
      }
      // kill badges, biggest on top
      const placed = d.hazards.filter(h => h.layout);
      const kills = placed.slice().reverse();
      const maxK = Math.max(1, ...placed.map(h => h.kills));
      const budget = Math.min(6, labelBudget(this.cam.z));
      ctx.font = `600 11px ${T.font}`;
      kills.forEach((h, revIdx) => {
        const rank = kills.length - 1 - revIdx;
        const pos = h.layout.pos;
        const [sx, sy] = this.toScreen(pos[0], pos[1]);
        if (sx < -60 || sx > this.W + 60 || sy < -60 || sy > this.H + 60) return;
        const r = 7 + 15 * Math.sqrt(h.kills / maxK);
        const color = T.causeColor(h.cause);
        ctx.globalAlpha = 0.22;
        ctx.fillStyle = color;
        ctx.beginPath(); ctx.arc(sx, sy, r, 0, Math.PI * 2); ctx.fill();
        ctx.globalAlpha = 0.95;
        ctx.lineWidth = 2; ctx.strokeStyle = color; ctx.stroke();
        this.hazardGlyph(ctx, h.layout || { prefab: h.prefab || h.name }, sx, sy, 4.5, color);
        if (rank < budget || this.cam.z >= 10) {
          const label = rank < budget ? `${h.name} ×${h.kills}` : `×${h.kills}`;
          this.pill(ctx, sx, sy - r - 11, label, color, rank < 3, 11);
        }
        this.hits.push({ type: "hazard", x: sx, y: sy, r: Math.max(12, r), data: h });
      });
      ctx.restore();
    }

    drawDeaths(ctx, untilT) {
      const T = this.theme, d = this.data, z = this.cam.z;
      const list = d.deaths.filter(p => p.ts <= untilT);
      const hl = this.highlight;
      const fade = this.fadeAlpha("deaths");
      ctx.save();
      if (z < CLUSTER_ZOOM && !this.replay.on) {
        const cells = new Map();
        for (const p of list) {
          const [sx, sy] = this.toScreen(p.x, p.y);
          if (sx < -30 || sx > this.W + 30 || sy < -30 || sy > this.H + 30) continue;
          const key = Math.floor(sx / CLUSTER_CELL) + "," + Math.floor(sy / CLUSTER_CELL);
          let c = cells.get(key);
          if (!c) cells.set(key, (c = { sx: 0, sy: 0, items: [] }));
          c.sx += sx; c.sy += sy; c.items.push(p);
        }
        const mid = T.ramps.death.stops[1];
        ctx.font = `700 11px ${T.font}`;
        ctx.textAlign = "center"; ctx.textBaseline = "middle";
        for (const c of cells.values()) {
          const n = c.items.length, x = c.sx / n, y = c.sy / n;
          if (n === 1) { this.marker(ctx, c.items[0], x, y, hl, fade); continue; }
          const r = Math.min(20, 7 + 2.6 * Math.sqrt(n));
          ctx.globalAlpha = 0.9 * fade;
          ctx.fillStyle = mid;
          ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.fill();
          ctx.lineWidth = 2; ctx.strokeStyle = T.map.bg; ctx.stroke();
          ctx.fillStyle = "#ffffff";
          ctx.fillText(String(n), x, y + 0.5);
          this.hits.push({ type: "cluster", x, y, r: Math.max(r, 14), data: c.items });
        }
      } else {
        for (const p of list) {
          const [sx, sy] = this.toScreen(p.x, p.y);
          if (sx < -20 || sx > this.W + 20 || sy < -20 || sy > this.H + 20) continue;
          this.marker(ctx, p, sx, sy, hl, fade);
        }
      }
      ctx.restore();
    }

    marker(ctx, p, sx, sy, hl, fade) {
      const T = this.theme;
      const dim = hl && hl !== p.session.id ? 0.2 : 1;
      const color = T.causeColor(p.cause);
      const shape = p.inferred ? "ring" : T.causeShape(p.cause);
      const r = this.hover && this.hover.data === p ? 8 : 5.5;
      ctx.globalAlpha = (p.inferred ? 0.8 : 1) * dim * fade;
      if (shape === "ring") {
        ctx.beginPath(); ctx.arc(sx, sy, r * 0.85, 0, Math.PI * 2);
        ctx.lineWidth = 4.5; ctx.strokeStyle = T.map.bg; ctx.stroke();
        ctx.lineWidth = 2; ctx.strokeStyle = color; ctx.stroke();
      } else {
        IK.theme.shapePath(ctx, shape, sx, sy, r);
        ctx.lineWidth = 2; ctx.strokeStyle = T.map.bg; ctx.stroke();   // 2 px surface ring
        ctx.fillStyle = color; ctx.fill();
      }
      this.hits.push({ type: "death", x: sx, y: sy, r: 12, data: p });
    }

    // ---------- ghost replay ----------

    setReplay(on) {
      this.replay.on = on;
      this.replay.playing = false;
      this.replay.bursts = [];
      this.replay.last = 0;
      this.request();
    }
    toggleReplayPlay() {
      const r = this.replay;
      if (!this.data) return;
      if (!r.playing && r.t >= this.data.maxTime) r.t = 0;
      r.playing = !r.playing;
      r.last = 0;
      this.onReplayTick(r);
      this.request();
    }
    seekReplay(t) {
      this.replay.prevT = t;
      this.replay.t = clamp(t, 0, this.data ? this.data.maxTime : 0);
      this.replay.bursts = [];
      this.onReplayTick(this.replay);
      this.request();
    }

    spawnBursts(t0, t1) {
      if (!this.data || t1 - t0 > 3) return;
      const now = performance.now();
      for (const e of this.data.sessions) {
        for (const d of e.deaths) {
          if (d.ts > t0 && d.ts <= t1) {
            const shards = [];
            for (let i = 0; i < 12; i++) {
              const a = Math.random() * Math.PI * 2, v = 40 + Math.random() * 80;
              shards.push({ vx: Math.cos(a) * v, vy: Math.sin(a) * v - 30, rot: Math.random() * 6, s: 2 + Math.random() * 3 });
            }
            this.replay.bursts.push({ x: d.x, y: d.y, born: now, color: IK.theme.sessionColor(e.session.slot), shards });
          }
        }
      }
    }

    ghostAt(e, t) {
      const smp = e.session.samples, idx = e.idx;
      const tt = t + e.t0;
      let lo = 0, hi = idx.length - 1;
      if (tt < smp.t[idx[0]] || tt > smp.t[idx[hi]] + e.session.interval) return null;
      while (lo < hi) { const m = (lo + hi + 1) >> 1; if (smp.t[idx[m]] <= tt) lo = m; else hi = m - 1; }
      const i = idx[lo], j = idx[Math.min(lo + 1, idx.length - 1)];
      const span = smp.t[j] - smp.t[i];
      const gap = Math.max(2.5 * e.session.interval, 1.2);
      if (span <= 0 || span > gap) {
        if (tt - smp.t[i] > gap) return null;
        return { x: smp.x[i], y: smp.y[i], k: lo };
      }
      const f = clamp((tt - smp.t[i]) / span, 0, 1);
      if (Math.hypot(smp.x[j] - smp.x[i], smp.y[j] - smp.y[i]) > 12) return { x: f < 0.5 ? smp.x[i] : smp.x[j], y: f < 0.5 ? smp.y[i] : smp.y[j], k: lo };
      return { x: smp.x[i] + (smp.x[j] - smp.x[i]) * f, y: smp.y[i] + (smp.y[j] - smp.y[i]) * f, k: lo };
    }

    drawGhosts(ctx) {
      const t = this.replay.t, now = performance.now();
      ctx.save();
      for (const e of this.data.sessions) {
        const g = this.ghostAt(e, t);
        if (!g) continue;
        const color = IK.theme.sessionColor(e.session.slot);
        const smp = e.session.samples;
        // trail: the last few seconds of samples, fading out behind the head
        const trail = [];
        for (let k = g.k; k >= 0; k--) {
          const i = e.idx[k];
          const age = t + e.t0 - smp.t[i];
          if (age > 5) break;
          trail.push([smp.x[i], smp.y[i], age]);
        }
        let prev = this.toScreen(g.x, g.y);
        ctx.lineCap = "round";
        for (const [x, y, age] of trail) {
          const p = this.toScreen(x, y);
          if (Math.hypot(p[0] - prev[0], p[1] - prev[1]) > 12 * this.cam.z) break;
          ctx.globalAlpha = Math.max(0, 0.8 * (1 - age / 5));
          ctx.strokeStyle = color; ctx.lineWidth = 3.5 * (1 - age / 6);
          ctx.beginPath(); ctx.moveTo(prev[0], prev[1]); ctx.lineTo(p[0], p[1]); ctx.stroke();
          prev = p;
        }
        const [sx, sy] = this.toScreen(g.x, g.y);
        const glow = ctx.createRadialGradient(sx, sy, 0, sx, sy, 16);
        glow.addColorStop(0, IK.theme.rgba(color, 0.75));
        glow.addColorStop(1, IK.theme.rgba(color, 0));
        ctx.globalAlpha = 1;
        ctx.fillStyle = glow;
        ctx.beginPath(); ctx.arc(sx, sy, 16, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = color;
        ctx.beginPath(); ctx.arc(sx, sy, 5, 0, Math.PI * 2); ctx.fill();
        ctx.lineWidth = 2; ctx.strokeStyle = "#ffffff"; ctx.stroke();
      }
      // death bursts: an expanding ring plus shards under gravity (the in-game shatter, in miniature)
      this.replay.bursts = this.replay.bursts.filter(b => now - b.born < 900);
      for (const b of this.replay.bursts) {
        const age = (now - b.born) / 1000;
        const [sx, sy] = this.toScreen(b.x, b.y);
        const crit = this.theme.status.critical;
        ctx.globalAlpha = Math.max(0, 1 - age / 0.6);
        ctx.strokeStyle = crit; ctx.lineWidth = 3;
        ctx.beginPath(); ctx.arc(sx, sy, 6 + age * 60, 0, Math.PI * 2); ctx.stroke();
        ctx.globalAlpha = Math.max(0, 1 - age / 0.9);
        ctx.fillStyle = b.color;
        for (const s of b.shards) {
          const x = sx + s.vx * age, y = sy + s.vy * age + 160 * age * age;
          ctx.save(); ctx.translate(x, y); ctx.rotate(s.rot + age * 8);
          ctx.fillRect(-s.s / 2, -s.s / 2, s.s, s.s);
          ctx.restore();
        }
      }
      ctx.restore();
    }

    // ---------- hover / tooltip ----------

    hoverTest() {
      if (!this.pointer) return;
      const { x, y } = this.pointer;
      let best = null, bestD = Infinity;
      // later hits are drawn on top; prefer them on ties
      for (let i = this.hits.length - 1; i >= 0; i--) {
        const h = this.hits[i];
        const d = Math.hypot(h.x - x, h.y - y);
        if (d <= h.r && d < bestD - 0.5) { best = h; bestD = d; }
      }
      const changed = (best && best.data) !== (this.hover && this.hover.data);
      this.hover = best;
      this.canvas.style.cursor = best ? "pointer" : "";
      if (best) this.showTip(best, x, y);
      else this.hideTip();
      if (changed) this.request();
    }

    drawHover(ctx) {
      const h = this.hover;
      if (!h || h.type === "segment" || h.type === "death") return;
      ctx.save();
      ctx.strokeStyle = this.theme.ink1; ctx.lineWidth = 1.5; ctx.globalAlpha = 0.6;
      ctx.beginPath(); ctx.arc(h.x, h.y, h.r + 2, 0, Math.PI * 2); ctx.stroke();
      ctx.restore();
    }

    showTip(hit, x, y) {
      const html = this.tipHtml(hit);
      if (!html) { this.hideTip(); return; }
      const tip = this.tip;
      tip.innerHTML = html;
      tip.classList.add("show");
      const w = tip.offsetWidth, h = tip.offsetHeight;
      let tx = x + 16, ty = y + 16;
      if (tx + w > this.W - 8) tx = x - w - 16;
      if (ty + h > this.H - 8) ty = y - h - 16;
      tip.style.left = Math.max(8, tx) + "px";
      tip.style.top = Math.max(8, ty) + "px";
    }
    hideTip() { this.tip.classList.remove("show"); }

    tipHtml(hit) {
      const T = this.theme, t = IK.t, d = this.data;
      const row = (k, v) => `<div class="tt-row"><span>${k}</span><b>${v}</b></div>`;
      const esc = s => String(s == null ? "" : s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
      if (hit.type === "death") {
        const p = hit.data;
        const shape = p.inferred ? "ring" : T.causeShape(p.cause);
        let h = `<div class="tt-title">${IK.theme.shapeSvg(shape, T.causeColor(p.cause), p.inferred)}${esc(t("cause." + p.cause))}${p.source ? " · " + esc(p.source) : ""}</div>`;
        h += row(t("tt.session"), `<span class="sw" style="background:${IK.theme.sessionColor(p.session.slot)}"></span>${esc(p.session.name)}`);
        h += row(t("tt.time"), fmtTime(p.ts));
        if (p.state) h += row(t("tt.state"), esc(p.state));
        if (p.life != null) h += row(t("tt.life"), p.life.toFixed(1) + " " + t("unit.s"));
        if (!p.inferred && (p.vx || p.vy)) h += row(t("tt.speed"), Math.hypot(p.vx, p.vy).toFixed(1));
        if (p.seg != null) {
          const inst = d.segInstances.find(g => g.session === p.session && g.seg === p.seg);
          if (inst) h += row(t("tt.segment"), `${esc(IK.model.segLabel(d, inst))} · ${t("tt.nthDeath", { n: p.segDeaths })}`);
        }
        if (p.inferred) h += `<div class="tt-note">${t("tt.inferred")}</div>`;
        return h;
      }
      if (hit.type === "cluster") {
        const items = hit.data;
        const causes = {};
        items.forEach(p => (causes[p.inferred ? "Unknown" : p.cause] = (causes[p.inferred ? "Unknown" : p.cause] || 0) + 1));
        let h = `<div class="tt-title">☠ ${t("tt.cluster", { n: items.length })}</div>`;
        Object.entries(causes).sort((a, b) => b[1] - a[1]).forEach(([c, n]) => {
          h += row(`${IK.theme.shapeSvg(c === "Unknown" ? "ring" : T.causeShape(c), T.causeColor(c), c === "Unknown").replace("<svg", '<svg style="width:12px;height:12px;vertical-align:-2px;margin-right:6px"')}${esc(t("cause." + c))}`, n);
        });
        return h + `<div class="tt-note">${t("tt.zoomIn")}</div>`;
      }
      if (hit.type === "segment") {
        const s = hit.data;
        let h = `<div class="tt-title">${esc(IK.model.segLabel(d, s))}</div>`;
        h += `<div style="margin:-2px 0 6px">${IK.theme.ratingHtml(s.mean)}</div>`;
        h += row(t("tt.attempts"), s.passes);
        h += row(t("tt.deaths"), s.total);
        h += row(t("tt.meanDeaths"), s.mean.toFixed(2));
        if (s.firstTry != null) h += row(t("tt.firstTry"), Math.round(s.firstTry * 100) + "%");
        if (s.medianTime != null) h += row(t("tt.medianTime"), fmtTime(s.medianTime));
        if (s.quits) h += row(t("tt.quits"), s.quits);
        if (s.inferred) h += `<div class="tt-note">${t("seg.inferredNote")}</div>`;
        return h;
      }
      if (hit.type === "checkpoint") {
        const cp = hit.data;
        let h = `<div class="tt-title"><span class="cp-pill">CP${String(cp.order).padStart(2, "0")}</span>${esc(cp.name)}</div>`;
        h += row(t("tt.stamps"), cp.stamps.length);
        h += row(t("tt.firstReach"), cp.firstReach != null ? fmtTime(cp.firstReach) : t("tt.never"));
        return h;
      }
      if (hit.type === "hazard") {
        const hz = hit.data;
        let h = `<div class="tt-title">${esc(hz.name)}</div>`;
        if (hz.prefab) h += row(t("tt.prefab"), esc(hz.prefab));
        if (hz.cause) h += row(t("tt.cause"), esc(t("cause." + hz.cause)));
        h += row(t("tt.kills"), hz.kills);
        return h;
      }
      if (hit.type === "door") return `<div class="tt-title">${esc(hit.data.name)}</div>` + row(t("tt.door"), esc(hit.data.to));
      if (hit.type === "prop") return `<div class="tt-title">${esc(hit.data.name)}</div>` + row("", esc(hit.data.kind));
      return "";
    }

    // ---------- minimap / scale ----------

    drawMinimap() {
      const mm = this.minimap;
      if (!mm || !mm.offsetParent) return;
      const b = this.worldBounds();
      const bw = b[2] - b[0], bh = b[3] - b[1];
      const W = 200, H = Math.max(40, Math.min(130, Math.round((W * bh) / bw)));
      const dpr = this.dpr;
      if (mm.width !== W * dpr || mm.height !== H * dpr) { mm.width = W * dpr; mm.height = H * dpr; mm.style.width = W + "px"; mm.style.height = H + "px"; this.minimapBase = null; }
      const ctx = mm.getContext("2d");
      const s = Math.min(W / bw, H / bh);
      if (!this.minimapBase) {
        const base = document.createElement("canvas");
        base.width = W * dpr; base.height = H * dpr;
        const c = base.getContext("2d");
        c.fillStyle = this.theme.map.bg; c.fillRect(0, 0, base.width, base.height);
        c.setTransform(dpr * s, 0, 0, -dpr * s, -b[0] * s * dpr, b[3] * s * dpr);
        if (this.hasArt() && this.opts.baseMode !== "schematic") {
          const r = this.art.rect;
          c.save(); c.setTransform(dpr * s, 0, 0, dpr * s, (r[0] - b[0]) * s * dpr, (b[3] - (r[1] + r[3])) * s * dpr);
          c.globalAlpha = 0.85; c.drawImage(this.art.img, 0, 0, r[2], r[3]); c.restore();
        } else if (this.layout) {
          for (const t of this.layout.tilemaps || []) {
            if (t.kind !== "terrain" && t.kind !== "hazard") continue;
            c.fillStyle = t.kind === "hazard" ? this.theme.map.hazardEdge : this.theme.map.terrainEdge;
            c.fill(this.geometry(t).fill);
          }
        }
        this.minimapBase = base;
      }
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.clearRect(0, 0, mm.width, mm.height);
      ctx.drawImage(this.minimapBase, 0, 0);
      // death dots on the minimap so the hot spots are findable from anywhere
      if (this.data) {
        ctx.fillStyle = this.theme.ramps.death.stops[1];
        for (const p of this.data.deaths) ctx.fillRect(((p.x - b[0]) * s - 1) * dpr, ((b[3] - p.y) * s - 1) * dpr, 2 * dpr, 2 * dpr);
      }
      const [x0, y1] = this.toWorld(0, 0), [x1, y0] = this.toWorld(this.W, this.H);
      ctx.strokeStyle = this.theme.ink1; ctx.lineWidth = 1.5 * dpr;
      ctx.strokeRect((x0 - b[0]) * s * dpr, (b[3] - y1) * s * dpr, (x1 - x0) * s * dpr, (y1 - y0) * s * dpr);
    }

    updateScalebar() {
      if (!this.scaleLabel) return;
      const target = 110 / this.cam.z;
      const pow = Math.pow(10, Math.floor(Math.log10(target)));
      const nice = [1, 2, 5, 10].map(m => m * pow).reduce((a, b) => (Math.abs(b - target) < Math.abs(a - target) ? b : a));
      this.scaleRule.style.width = Math.round(nice * this.cam.z) + "px";
      this.scaleLabel.textContent = IK.t("map.cells", { n: nice >= 1 ? nice : nice.toFixed(1) });
    }

    // ---------- legend ----------

    buildLegend() {
      const el = this.legendEl;
      if (!el) return;
      const T = this.theme, t = IK.t, d = this.data;
      let h = "";
      if (d && this.layers.deaths && d.deathsAll.length) {
        const counts = {};
        d.deaths.forEach(p => { const c = p.inferred ? "Unknown" : p.cause; counts[c] = (counts[c] || 0) + 1; });
        h += `<h6>${t("legend.deaths")}</h6>`;
        IK.theme.CAUSE_ORDER.forEach(c => {
          if (!counts[c]) return;
          const inferred = c === "Unknown";
          h += `<div class="legend-row">${IK.theme.shapeSvg(inferred ? "ring" : T.causeShape(c), T.causeColor(c), inferred)}<span>${t(inferred && d.anyInferred ? "legend.inferred" : "cause." + c)}</span><span class="muted num" style="margin-left:auto">${counts[c]}</span></div>`;
        });
      }
      for (const name of HEAT_LAYERS) {
        if (!this.layers[name]) continue;
        const ramp = T.ramps[HEAT_RAMP[name]];
        const label = name === "mech" ? `${t("layer.mech")} · ${t("mech." + this.opts.mech)}` : t("layer." + name);
        const high = name === "lethality" && d ? `≥ ${Math.round(this.heatFor("lethality").top * 100)}%` : t("legend.high");
        h += `<h6>${label}</h6><div class="legend-ramp" style="background:${ramp.css}"></div><div class="legend-scale"><span>${name === "lethality" ? "0%" : t("legend.low")}</span><span>${high}</span></div>`;
      }
      if (this.layers.coverage && this.layout) {
        h += `<div class="legend-row" style="margin-top:6px"><svg viewBox="0 0 14 14"><rect x="1" y="6" width="12" height="3" rx="1" fill="${T.coverage}"/></svg><span>${t("legend.unvisited")}</span></div>`;
      }
      if (this.layers.segments && d && d.segments.some(s => s.fromPos && s.toPos)) {
        h += `<h6>${t("legend.segments")}</h6>`;
        ["easy", "moderate", "hard", "brutal"].forEach((k, i) => {
          h += `<div class="legend-row">${IK.theme.ratingHtml([0, 1, 2, 4][i])}</div>`;
        });
      }
      if (this.layers.paths && d && d.sessions.length && this.opts.pathColor === "session") {
        h += `<h6>${t("legend.sessions")}</h6>`;
        d.sessions.slice(0, 9).forEach(e => {
          h += `<div class="legend-row"><svg viewBox="0 0 14 14"><path d="M1 9c4-6 8 2 12-4" stroke="${IK.theme.sessionColor(e.session.slot)}" stroke-width="2.4" fill="none" stroke-linecap="round"/></svg><span>${e.session.name}</span></div>`;
        });
      }
      el.innerHTML = h;
      el.classList.toggle("hidden", !h);
      this.onLegendChange();
    }

    // ---------- export ----------

    exportImage(title, subtitle) {
      const scale = 2, W = this.W, H = this.H, head = 64, foot = 0;
      const c = document.createElement("canvas");
      c.width = W * scale; c.height = (H + head + foot) * scale;
      const ctx = c.getContext("2d");
      const T = this.theme;
      ctx.fillStyle = T.surface1; ctx.fillRect(0, 0, c.width, c.height);
      // map
      const map = document.createElement("canvas");
      map.width = W * scale; map.height = H * scale;
      const hits = this.hits, hover = this.hover;
      this.hover = null;
      this.draw(map.getContext("2d"), W, H, scale, true);
      this.hits = hits; this.hover = hover;
      ctx.drawImage(map, 0, head * scale);
      ctx.setTransform(scale, 0, 0, scale, 0, 0);
      ctx.fillStyle = T.ink1; ctx.font = `650 18px ${T.font}`; ctx.textBaseline = "alphabetic";
      ctx.fillText(title, 18, 30);
      ctx.fillStyle = T.ink3; ctx.font = `400 12px ${T.font}`;
      ctx.fillText(subtitle, 18, 50);
      // legend card, rasterized from the live legend's content
      this.drawLegendOnto(ctx, W - 16, head + 12);
      // scale bar
      const rule = parseFloat(this.scaleRule.style.width) || 80;
      ctx.fillStyle = IK.theme.rgba(T.surface1, 0.9);
      roundRect(ctx, W - rule - 36, head + H - 44, rule + 24, 32, 8); ctx.fill();
      ctx.strokeStyle = T.ink2; ctx.lineWidth = 2;
      ctx.beginPath(); ctx.moveTo(W - rule - 24, head + H - 22); ctx.lineTo(W - rule - 24, head + H - 16); ctx.lineTo(W - 24, head + H - 16); ctx.lineTo(W - 24, head + H - 22); ctx.stroke();
      ctx.fillStyle = T.ink2; ctx.font = `500 11px ${T.font}`;
      ctx.fillText(this.scaleLabel.textContent, W - rule - 24, head + H - 26);
      return c;
    }

    drawLegendOnto(ctx, right, top) {
      const T = this.theme, t = IK.t, d = this.data;
      const rows = [];
      if (d && this.layers.deaths) {
        const counts = {};
        d.deaths.forEach(p => { const c = p.inferred ? "Unknown" : p.cause; counts[c] = (counts[c] || 0) + 1; });
        IK.theme.CAUSE_ORDER.forEach(c => counts[c] && rows.push({ kind: "shape", c, label: `${t(c === "Unknown" && d.anyInferred ? "legend.inferred" : "cause." + c)}  ${counts[c]}` }));
      }
      for (const name of HEAT_LAYERS) if (this.layers[name]) rows.push({ kind: "ramp", ramp: T.ramps[HEAT_RAMP[name]], label: t("layer." + name) });
      if (this.layers.segments) ["easy", "moderate", "hard", "brutal"].forEach(k => {
        const r = IK.theme.rating({ easy: 0, moderate: 1, hard: 2, brutal: 4 }[k]);
        rows.push({ kind: "line", color: T.status[r.status], label: t("rating." + k) });
      });
      if (!rows.length) return;
      const w = 180, h = 16 + rows.length * 20;
      ctx.save();
      ctx.fillStyle = IK.theme.rgba(T.surface1, 0.92);
      roundRect(ctx, right - w, top, w, h, 10); ctx.fill();
      ctx.strokeStyle = T.border; ctx.lineWidth = 1; ctx.stroke();
      ctx.font = `500 12px ${T.font}`; ctx.textBaseline = "middle";
      rows.forEach((r, i) => {
        const y = top + 18 + i * 20, x = right - w + 14;
        if (r.kind === "shape") {
          const shape = r.c === "Unknown" ? "ring" : T.causeShape(r.c);
          IK.theme.shapePath(ctx, shape === "ring" ? "circle" : shape, x + 6, y, 5);
          if (shape === "ring") { ctx.strokeStyle = T.causeColor(r.c); ctx.lineWidth = 2; ctx.stroke(); }
          else { ctx.fillStyle = T.causeColor(r.c); ctx.fill(); }
        } else if (r.kind === "ramp") {
          const g = ctx.createLinearGradient(x, 0, x + 22, 0);
          g.addColorStop(0, r.ramp.stops[0]); g.addColorStop(0.5, r.ramp.stops[1]); g.addColorStop(1, r.ramp.stops[2]);
          ctx.fillStyle = g; roundRect(ctx, x, y - 4, 22, 8, 3); ctx.fill();
        } else {
          ctx.strokeStyle = r.color; ctx.lineWidth = 3; ctx.lineCap = "round";
          ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x + 18, y); ctx.stroke();
        }
        ctx.fillStyle = T.ink2;
        ctx.fillText(r.label, x + 30, y);
      });
      ctx.restore();
    }

    activeLayerNames() {
      return Object.keys(this.layers).filter(k => this.layers[k]).map(k => IK.t("layer." + k)).join(" · ");
    }
  }

  function roundRect(ctx, x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }

  IK.MapView = MapView;
  IK.fmtTime = fmtTime;
  IK.HEAT_LAYERS = HEAT_LAYERS;
  IK.HEAT_RAMP = HEAT_RAMP;
})();
