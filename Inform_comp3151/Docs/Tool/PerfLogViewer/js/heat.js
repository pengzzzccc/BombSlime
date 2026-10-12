/* Heat fields over world space: accumulate weights into a grid, Gaussian-blur them, color them
   through a single-hue ramp into an offscreen canvas the map scales over the level. Also the
   derived fields — lethality (deaths per life passing through), coverage (ground nobody came near)
   — and marching-squares contours. */
(function () {
  "use strict";
  const IK = (window.IK = window.IK || {});

  class Grid {
    constructor(bounds, cell, pad) {
      pad = pad == null ? 4 : pad;
      this.cell = cell;
      this.x0 = Math.floor(bounds[0] - pad);
      this.y0 = Math.floor(bounds[1] - pad);
      this.w = Math.max(1, Math.ceil((bounds[2] + pad - this.x0) / cell));
      this.h = Math.max(1, Math.ceil((bounds[3] + pad - this.y0) / cell));
      this.v = new Float32Array(this.w * this.h);
    }
    index(x, y) {
      const gx = Math.floor((x - this.x0) / this.cell), gy = Math.floor((y - this.y0) / this.cell);
      if (gx < 0 || gy < 0 || gx >= this.w || gy >= this.h) return -1;
      return gx + gy * this.w;
    }
    add(x, y, w) { const i = this.index(x, y); if (i >= 0) this.v[i] += w == null ? 1 : w; }
    max() { let m = 0; for (let i = 0; i < this.v.length; i++) if (this.v[i] > m) m = this.v[i]; return m; }
    // A robust top: the 99.5th percentile of non-zero cells, so one pile-up doesn't flatten the rest
    top(q) {
      const nz = [];
      for (let i = 0; i < this.v.length; i++) if (this.v[i] > 0) nz.push(this.v[i]);
      if (!nz.length) return 0;
      nz.sort((a, b) => a - b);
      return nz[Math.min(nz.length - 1, Math.floor(nz.length * (q || 0.995)))];
    }
    rect() { return [this.x0, this.y0, this.x0 + this.w * this.cell, this.y0 + this.h * this.cell]; }
  }

  // Separable Gaussian in place; sigma in world units
  function blur(grid, sigma) {
    const s = sigma / grid.cell;
    if (s < 0.3) return grid;
    const r = Math.ceil(s * 3);
    const k = new Float32Array(r * 2 + 1);
    let sum = 0;
    for (let i = -r; i <= r; i++) { k[i + r] = Math.exp(-(i * i) / (2 * s * s)); sum += k[i + r]; }
    for (let i = 0; i < k.length; i++) k[i] /= sum;

    const { w, h, v } = grid;
    const tmp = new Float32Array(v.length);
    for (let y = 0; y < h; y++) {
      const row = y * w;
      for (let x = 0; x < w; x++) {
        let acc = 0;
        const lo = Math.max(-r, -x), hi = Math.min(r, w - 1 - x);
        for (let i = lo; i <= hi; i++) acc += v[row + x + i] * k[i + r];
        tmp[row + x] = acc;
      }
    }
    for (let x = 0; x < w; x++) {
      for (let y = 0; y < h; y++) {
        let acc = 0;
        const lo = Math.max(-r, -y), hi = Math.min(r, h - 1 - y);
        for (let i = lo; i <= hi; i++) acc += tmp[(y + i) * w + x] * k[i + r];
        v[y * w + x] = acc;
      }
    }
    return grid;
  }

  // Grid → canvas through a ramp LUT. Rows flip: grid y grows up (world), canvas y grows down.
  function render(grid, ramp, top, floor) {
    const canvas = document.createElement("canvas");
    canvas.width = grid.w; canvas.height = grid.h;
    const ctx = canvas.getContext("2d");
    const img = ctx.createImageData(grid.w, grid.h);
    const lut = ramp.lut;
    const m = top || grid.top() || 1;
    const f = floor || 0.02;
    for (let y = 0; y < grid.h; y++) {
      const src = y * grid.w, dst = (grid.h - 1 - y) * grid.w;
      for (let x = 0; x < grid.w; x++) {
        let t = grid.v[src + x] / m;
        if (t <= f) continue;
        if (t > 1) t = 1;
        const li = Math.round(t * 255) * 4, o = (dst + x) * 4;
        img.data[o] = lut[li]; img.data[o + 1] = lut[li + 1]; img.data[o + 2] = lut[li + 2]; img.data[o + 3] = lut[li + 3];
      }
    }
    ctx.putImageData(img, 0, 0);
    return { canvas, rect: grid.rect(), top: m };
  }

  // Walk a polyline in small steps, calling fn(x, y) — paths are sparse samples, cells between
  // them were still crossed
  function walk(pts, step, fn) {
    for (let i = 0; i + 3 < pts.length; i += 3) {
      const x0 = pts[i], y0 = pts[i + 1], x1 = pts[i + 3], y1 = pts[i + 4];
      const n = Math.max(1, Math.ceil(Math.hypot(x1 - x0, y1 - y0) / step));
      for (let k = 0; k < n; k++) fn(x0 + ((x1 - x0) * k) / n, y0 + ((y1 - y0) * k) / n);
    }
    if (pts.length >= 3) fn(pts[pts.length - 3], pts[pts.length - 2]);
  }

  function deathField(data, bounds) {
    const g = new Grid(bounds, 0.5);
    for (const d of data.deaths) g.add(d.x, d.y, 1);
    return blur(g, 1.3);
  }

  function presenceField(data, bounds) {
    const g = new Grid(bounds, 1);
    for (const e of data.sessions) {
      const smp = e.session.samples;
      for (const i of e.idx) {
        if (e.playingId >= 0 && smp.gstate[i] !== e.playingId) continue;
        g.add(smp.x[i], smp.y[i], e.session.interval);
      }
    }
    return blur(g, 1.6);
  }

  // Deaths per life that passed through: each path (a stretch of one life) marks a cell once
  function lethalityField(data, bounds) {
    const lives = new Grid(bounds, 1);
    const seen = new Int32Array(lives.v.length).fill(-1);
    data.paths.forEach((p, pi) => {
      walk(p.pts, 0.5, (x, y) => {
        const i = lives.index(x, y);
        if (i >= 0 && seen[i] !== pi) { seen[i] = pi; lives.v[i] += 1; }
      });
    });
    const deaths = new Grid(bounds, 1);
    for (const d of data.deaths) deaths.add(d.x, d.y, 1);
    blur(lives, 2);
    blur(deaths, 2);
    const out = new Grid(bounds, 1);
    const minLives = Math.max(0.6, Math.min(2, data.sessions.length * 0.5));
    for (let i = 0; i < out.v.length; i++) {
      if (lives.v[i] < minLives || deaths.v[i] < 0.02) continue;
      out.v[i] = Math.min(1, deaths.v[i] / lives.v[i]);
    }
    // soften the masked edges so the field reads as a glow, not a stencil
    return blur(out, 1.2);
  }

  // Frame time over budget, where it happened
  function perfField(data, bounds) {
    const g = new Grid(bounds, 1);
    for (const e of data.sessions) {
      const smp = e.session.samples;
      for (const i of e.idx) {
        const over = Math.max(0, (isFinite(smp.maxFrame[i]) ? smp.maxFrame[i] : smp.frame[i]) - 16.7);
        if (over > 0) g.add(smp.x[i], smp.y[i], over + smp.hitches[i] * 20);
      }
    }
    return blur(g, 1.8);
  }

  const MECH = {
    wall: s => s.startsWith("WallSlide"),
    ceiling: s => s.startsWith("Ceiling"),
    air: s => s === "JumpUp" || s === "Rise" || s === "Fall",
    rope: null,
  };
  function mechField(data, bounds, mech) {
    const g = new Grid(bounds, 1);
    for (const e of data.sessions) {
      const smp = e.session.samples;
      const test = MECH[mech];
      for (const i of e.idx) {
        const hit = mech === "rope"
          ? (smp.ropeNames[smp.rope[i]] || "Idle") !== "Idle" && smp.ropeNames[smp.rope[i]] !== ""
          : test(smp.stateNames[smp.state[i]] || "");
        if (hit) g.add(smp.x[i], smp.y[i], e.session.interval);
      }
    }
    return blur(g, 1.4);
  }

  // Ground surfaces (a terrain cell with air above) that no path came within `radius` of
  function coverage(data, layout, radius) {
    radius = radius || 4;
    const terrain = (layout.tilemaps || []).filter(t => t.kind === "terrain");
    if (!terrain.length) return null;
    const visit = new Grid(layout.bounds, 1, 6);
    data.paths.forEach(p => walk(p.pts, 0.75, (x, y) => visit.add(x, y, 1)));
    // dilate the visited set by radius
    const reach = new Uint8Array(visit.v.length);
    const r = Math.ceil(radius);
    for (let y = 0; y < visit.h; y++) for (let x = 0; x < visit.w; x++) {
      if (!visit.v[x + y * visit.w]) continue;
      for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
        if (dx * dx + dy * dy > radius * radius) continue;
        const xx = x + dx, yy = y + dy;
        if (xx >= 0 && yy >= 0 && xx < visit.w && yy < visit.h) reach[xx + yy * visit.w] = 1;
      }
    }
    const runs = [];
    let surface = 0, reached = 0;
    for (const t of terrain) {
      const bits = IK.mapBits(t);
      for (let y = 0; y < t.h; y++) {
        let runStart = -1;
        for (let x = 0; x <= t.w; x++) {
          let unvisited = false;
          if (x < t.w) {
            const i = x + y * t.w;
            const filled = bits[i >> 3] & (1 << (i & 7));
            const above = y + 1 < t.h ? bits[(i + t.w) >> 3] & (1 << ((i + t.w) & 7)) : 0;
            if (filled && !above) {
              surface++;
              const wx = t.origin[0] + (x + 0.5) * t.cell[0], wy = t.origin[1] + (y + 1.5) * t.cell[1];
              const gi = visit.index(wx, wy);
              if (gi >= 0 && reach[gi]) reached++;
              else unvisited = true;
            }
          }
          if (unvisited && runStart < 0) runStart = x;
          if (!unvisited && runStart >= 0) {
            runs.push([t.origin[0] + runStart * t.cell[0], t.origin[1] + (y + 1) * t.cell[1], (x - runStart) * t.cell[0]]);
            runStart = -1;
          }
        }
      }
    }
    return { runs, surface, reached, ratio: surface ? reached / surface : 0 };
  }

  // Marching squares → line segments in world space at the given fractions of the field's top
  function contours(grid, levels, top) {
    const segs = [];
    const { w, h, v, cell, x0, y0 } = grid;
    const m = top || grid.top() || 1;
    for (const level of levels) {
      const L = level * m;
      for (let y = 0; y < h - 1; y++) for (let x = 0; x < w - 1; x++) {
        const a = v[x + y * w], b = v[x + 1 + y * w], c = v[x + 1 + (y + 1) * w], d = v[x + (y + 1) * w];
        const code = (a > L ? 1 : 0) | (b > L ? 2 : 0) | (c > L ? 4 : 0) | (d > L ? 8 : 0);
        if (code === 0 || code === 15) continue;
        const lerp = (p, q) => (L - p) / (q - p || 1e-6);
        const px = (x + 0.5) * cell + x0, py = (y + 0.5) * cell + y0;
        const e = [
          [px + lerp(a, b) * cell, py],                 // bottom
          [px + cell, py + lerp(b, c) * cell],          // right
          [px + lerp(d, c) * cell, py + cell],          // top
          [px, py + lerp(a, d) * cell],                 // left
        ];
        const table = {
          1: [[3, 0]], 2: [[0, 1]], 3: [[3, 1]], 4: [[1, 2]], 5: [[3, 2], [0, 1]], 6: [[0, 2]], 7: [[3, 2]],
          8: [[2, 3]], 9: [[0, 2]], 10: [[0, 3], [1, 2]], 11: [[1, 2]], 12: [[1, 3]], 13: [[0, 1]], 14: [[0, 3]],
        };
        for (const [p, q] of table[code]) segs.push(e[p][0], e[p][1], e[q][0], e[q][1], level);
      }
    }
    return segs;
  }

  // Tile bitsets are base64 in the layout; decoded once and kept on the tilemap object
  IK.mapBits = function (t) {
    if (t._bits) return t._bits;
    const bin = atob(t.bits);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return (t._bits = out);
  };

  IK.heat = { Grid, blur, render, walk, deathField, presenceField, lethalityField, perfField, mechField, coverage, contours };
})();
