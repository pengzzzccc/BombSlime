/* Session store + per-scene aggregation. Everything the views draw comes from sceneData(scene):
   deaths, paths, canonical checkpoints in route order, checkpoint-to-checkpoint segments (read from
   format-2 event lines, or inferred from paths crossing checkpoint boxes for old logs), and hazard
   kill counts. Results are cached per (scene, selection, filters) and dropped on any change. */
(function () {
  "use strict";
  const IK = (window.IK = window.IK || {});

  const sessions = [];
  const listeners = [];
  let layouts = null;
  let nextSlot = 0;
  const filters = { causes: new Set(IK.theme.CAUSE_ORDER), build: "all" };
  let cache = new Map();

  const CP_MATCH = 0.75;          // spawn positions closer than this are the same checkpoint
  const PATH_JUMP = 12;           // a jump longer than this between samples is a teleport, not motion
  const TRAP_LIFE = 3;            // seconds: a life this short right after respawn is a trap
  const TRAP_DIST = 6;

  function emit() { cache = new Map(); listeners.forEach(fn => fn()); }

  // ---------- sessions ----------

  function addSession(s) {
    if (sessions.some(o => o.file === s.file && o.samples.n === s.samples.n)) return false;
    s.id = "s" + Math.random().toString(36).slice(2, 9);
    s.slot = nextSlot++;
    s.enabled = true;
    s.build = buildKey(s.meta);
    // names key the chart legends, so two logs from the same minute must not collide
    const base = s.name;
    for (let k = 2; sessions.some(o => o.name === s.name); k++) s.name = `${base} ·${k}`;
    sessions.push(s);
    sessions.sort((a, b) => (a.meta.startedAt || a.file).localeCompare(b.meta.startedAt || b.file));
    emit();
    return true;
  }

  function removeSession(id) {
    const i = sessions.findIndex(s => s.id === id);
    if (i >= 0) sessions.splice(i, 1);
    emit();
  }

  function setEnabled(id, on) {
    const s = sessions.find(o => o.id === id);
    if (s) { s.enabled = on; emit(); }
  }
  function setAllEnabled(on) { sessions.forEach(s => (s.enabled = on)); emit(); }

  function buildKey(meta) {
    const branch = meta.branch && meta.branch !== "unknown" ? meta.branch : "";
    const commit = meta.commit && meta.commit !== "unknown" ? meta.commit.slice(0, 7) : "";
    return branch || commit ? `${branch || "?"}@${commit || "?"}` : "unknown";
  }

  function selected() {
    return sessions.filter(s => s.enabled && (filters.build === "all" || s.build === filters.build));
  }

  function setLayouts(obj) {
    layouts = obj && obj.scenes ? obj : null;
    emit();
  }

  function setCauseEnabled(cause, on) {
    if (on) filters.causes.add(cause); else filters.causes.delete(cause);
    emit();
  }
  function setBuild(b) { filters.build = b; emit(); }

  // ---------- scene list / global stats ----------

  function sceneList() {
    const stats = new Map();
    for (const s of selected()) {
      const smp = s.samples;
      for (let i = 0; i < smp.n; i++) {
        const name = smp.sceneNames[smp.scene[i]];
        if (!IK.parse.isGameplayScene(name)) continue;
        let st = stats.get(name);
        if (!st) stats.set(name, (st = { name, time: 0, deaths: 0, sessions: new Set() }));
        st.time += s.interval;
        st.sessions.add(s.id);
      }
      for (const d of s.events.deaths) {
        const st = stats.get(d.scene);
        if (st) st.deaths++;
      }
    }
    const layoutOrder = layouts ? Object.keys(layouts.scenes) : [];
    return [...stats.values()].sort((a, b) => {
      const ia = layoutOrder.indexOf(a.name), ib = layoutOrder.indexOf(b.name);
      if (ia >= 0 || ib >= 0) return (ia < 0 ? 1e9 : ia) - (ib < 0 ? 1e9 : ib);
      return a.name.localeCompare(b.name, undefined, { numeric: true });
    });
  }

  // Per-session totals over gameplay scenes only (menus and the end card are not play)
  function sessionStats(s) {
    if (s._stats) return s._stats;
    const smp = s.samples;
    let time = 0, fpsSum = 0, fpsN = 0, hitches = 0;
    for (let i = 0; i < smp.n; i++) {
      if (!IK.parse.isGameplayScene(smp.sceneNames[smp.scene[i]])) continue;
      time += s.interval;
      if (isFinite(smp.fps[i])) { fpsSum += smp.fps[i]; fpsN++; }
      hitches += smp.hitches[i];
    }
    const deaths = s.events.deaths.filter(d => IK.parse.isGameplayScene(d.scene)).length;
    return (s._stats = { time, deaths, fps: fpsN ? fpsSum / fpsN : NaN, hitches, rate: time > 0 ? deaths / (time / 60) : 0 });
  }

  // ---------- per-scene aggregation ----------

  function sceneData(scene) {
    const sel = selected();
    const key = scene + "|" + sel.map(s => s.id).join(",") + "|" + [...filters.causes].join(",");
    if (cache.has(key)) return cache.get(key);
    const data = buildScene(scene, sel);
    cache.set(key, data);
    return data;
  }

  function buildScene(scene, sel) {
    const layout = layouts && layouts.scenes[scene] ? layouts.scenes[scene] : null;
    const cps = new Map();         // key → canonical checkpoint
    const layoutCps = layout ? layout.checkpoints.map((c, i) => ({ ...c, _i: i })) : [];

    function cpFor(name, x, y) {
      if (x == null || y == null) return null;
      let best = null, bestD = CP_MATCH;
      for (const c of layoutCps) {
        const d = Math.hypot(c.spawn[0] - x, c.spawn[1] - y);
        if (d < bestD) { bestD = d; best = c; }
      }
      const key = best ? "L" + best._i : `${name}@${x.toFixed(1)},${y.toFixed(1)}`;
      let cp = cps.get(key);
      if (!cp) {
        cp = {
          key, name: best ? best.name : name, x: best ? best.spawn[0] : x, y: best ? best.spawn[1] : y,
          start: best ? !!best.start : false, layout: best, stamps: [], order: 0,
        };
        cps.set(key, cp);
      }
      return cp;
    }
    // Every layout checkpoint exists even if nobody reached it — "never reached" is a finding
    layoutCps.forEach(c => cpFor(c.name, c.spawn[0], c.spawn[1]));

    const out = {
      scene, layout, sessions: [], deaths: [], deathsAll: [], respawns: [], paths: [], jumps: [],
      checkpoints: [], segments: [], segInstances: [], hazards: [], bounds: null,
      anyInferred: false, anyEvents: false, interval: 1, maxTime: 0,
    };

    let bx0 = Infinity, by0 = Infinity, bx1 = -Infinity, by1 = -Infinity;
    const grow = (x, y) => { if (x < bx0) bx0 = x; if (y < by0) by0 = y; if (x > bx1) bx1 = x; if (y > by1) by1 = y; };

    for (const s of sel) {
      const smp = s.samples;
      const sceneId = smp.sceneNames.indexOf(scene);
      if (sceneId < 0) continue;

      const idx = [];
      for (let i = 0; i < smp.n; i++) if (smp.scene[i] === sceneId && !(smp.x[i] === 0 && smp.y[i] === 0)) idx.push(i);
      if (!idx.length) continue;

      const t0 = smp.t[idx[0]];
      const playingId = smp.gstateNames.indexOf("Playing");
      const entry = { session: s, idx: Int32Array.from(idx), t0, tEnd: smp.t[idx[idx.length - 1]] - t0, playingId };
      out.sessions.push(entry);
      out.interval = Math.min(out.interval, s.interval);
      out.maxTime = Math.max(out.maxTime, entry.tEnd);
      if (s.hasEvents) out.anyEvents = true;
      for (const i of idx) grow(smp.x[i], smp.y[i]);

      // deaths, respawns
      const deaths = s.events.deaths.filter(d => d.scene === scene && d.x != null);
      const sorted = deaths.map(d => ({ ...d, session: s, ts: d.t - t0 })).sort((a, b) => a.t - b.t);
      entry.deaths = sorted;
      for (const d of sorted) {
        out.deathsAll.push(d);
        if (filters.causes.has(d.cause)) out.deaths.push(d);
      }
      const respawns = s.events.respawns.filter(r => r.scene === scene).map(r => ({ ...r, session: s, ts: r.t - t0 }));
      entry.respawns = respawns;
      out.respawns.push(...respawns);

      // checkpoint stamps
      if (s.hasEvents) {
        for (const c of s.events.checkpoints) {
          if (c.scene !== scene) continue;
          const cp = cpFor(c.name, c.x, c.y);
          if (cp) cp.stamps.push({ session: s, ts: c.t - t0 });
        }
      }

      // segments
      let instances;
      if (s.hasEvents) {
        instances = s.events.segments.filter(g => g.scene === scene).map(g => {
          const from = isEntryName(g.from) ? g.from : (cpFor(g.from, g.fromX, g.fromY) || {}).key;
          const to = g.endReason === "checkpoint" ? (cpFor(g.to, g.toX, g.toY) || {}).key : null;
          return { ...g, session: s, from, to };
        });
      } else {
        instances = inferSegments(s, entry, sorted, layoutCps, cpFor);
        if (instances.length) out.anyInferred = true;
      }
      // Entry segments start wherever the player stood: the first sample at or after the start
      for (const g of instances) {
        if (!isEntryName(g.from)) continue;
        const i = firstSampleAfter(smp, entry.idx, g.start);
        if (i >= 0) { g.fromX = smp.x[i]; g.fromY = smp.y[i]; }
      }
      entry.segments = instances;
      out.segInstances.push(...instances);

      // paths, split at deaths, teleports and time gaps
      buildPaths(s, entry, sorted, respawns, out);
    }

    if (isFinite(bx0)) out.bounds = [bx0, by0, bx1, by1];
    if (layout && layout.bounds) out.bounds = layout.bounds;

    // route order: median first-stamp time; never-stamped checkpoints follow by x
    const list = [...cps.values()];
    for (const cp of list) {
      const firsts = new Map();
      for (const st of cp.stamps) if (!firsts.has(st.session.id) || st.ts < firsts.get(st.session.id)) firsts.set(st.session.id, st.ts);
      cp.firstReach = median([...firsts.values()]);
      cp.reachedBy = firsts.size;
    }
    list.sort((a, b) => {
      const ra = a.firstReach, rb = b.firstReach;
      if (a.start !== b.start) return a.start ? -1 : 1;
      if (ra != null && rb != null) return ra - rb;
      if (ra != null) return -1;
      if (rb != null) return 1;
      return a.x - b.x;
    });
    list.forEach((cp, i) => (cp.order = i + 1));
    out.checkpoints = list;
    out.cpByKey = cps;

    out.segments = aggregateSegments(out.segInstances, cps);
    out.hazards = hazardKills(out.deathsAll, layout);
    out.traps = respawnTraps(out.deathsAll, out.respawns, cps);
    return out;
  }

  function isEntryName(name) { return name === "entry" || name === "rec_start"; }

  function firstSampleAfter(smp, idx, t) {
    for (const i of idx) if (smp.t[i] >= t - 1e-6) return i;
    return -1;
  }

  // Old logs: walk the scene's samples, stamping a checkpoint when the player stands inside its
  // trigger box (grown a little, since a 1 s sample can land just beside it)
  function inferSegments(s, entry, deaths, layoutCps, cpFor) {
    if (!layoutCps.length) return [];
    const smp = s.samples, idx = entry.idx, out = [];
    const boxes = layoutCps.map(c => {
      const b = c.box || [c.pos[0] - 0.5, c.pos[1] - 1, c.pos[0] + 0.5, c.pos[1] + 1];
      return [b[0] - 0.75, b[1] - 0.75, b[2] + 0.75, b[3] + 0.75];
    });
    let cur = null, curCp = null, di = 0;
    const open = (t, from) => (cur = { session: s, from, to: null, start: t, deaths: 0, endReason: null, inferred: true });
    const close = (t, reason, toCp) => {
      if (!cur) return;
      cur.duration = t - cur.start; cur.playDuration = null; cur.endReason = reason;
      cur.to = toCp ? toCp.key : null;
      if (cur.deaths > 0 || cur.duration >= 1) out.push(cur);
      cur = null;
    };
    let prevI = -1;
    for (const i of idx) {
      const t = smp.t[i];
      if (prevI >= 0 && t - smp.t[prevI] > s.interval * 3) { close(smp.t[prevI], "scene"); curCp = null; }
      if (!cur) open(t, curCp ? curCp.key : "entry");
      while (di < deaths.length && deaths[di].t <= t) { if (cur) cur.deaths++; di++; }
      const x = smp.x[i], y = smp.y[i];
      for (let k = 0; k < boxes.length; k++) {
        const b = boxes[k];
        if (x < b[0] || x > b[2] || y < b[1] || y > b[3]) continue;
        const cp = cpFor(layoutCps[k].name, layoutCps[k].spawn[0], layoutCps[k].spawn[1]);
        if (cp === curCp) break;
        cp.stamps.push({ session: s, ts: t - entry.t0, inferred: true });
        close(t, "checkpoint", cp);
        curCp = cp;
        open(t, cp.key);
        break;
      }
      prevI = i;
    }
    if (prevI >= 0) {
      const last = smp.t[prevI];
      const leftScene = prevI + 1 < smp.n;
      close(last, leftScene ? "scene" : "stop");
    }
    return out;
  }

  function buildPaths(s, entry, deaths, respawns, out) {
    const smp = s.samples, idx = entry.idx;
    const maxGap = Math.max(2.5 * s.interval, 1.2);
    let pts = [];
    let di = 0;
    const flush = () => {
      if (pts.length >= 6) out.paths.push({ session: s, pts: Float32Array.from(pts) });
      pts = [];
    };
    let prev = -1;
    for (const i of idx) {
      const t = smp.t[i], x = smp.x[i], y = smp.y[i];
      if (prev >= 0) {
        const gap = t - smp.t[prev] > maxGap;
        const jump = Math.hypot(x - smp.x[prev], y - smp.y[prev]) > PATH_JUMP;
        let died = false;
        while (di < deaths.length && deaths[di].t <= t) {
          const d = deaths[di++];
          if (d.t >= smp.t[prev]) {
            pts.push(d.x, d.y, d.t - entry.t0);
            died = true;
            const r = respawns.find(r => r.t >= d.t && r.t <= d.t + 10);
            if (r && r.x != null) out.jumps.push({ session: s, x0: d.x, y0: d.y, x1: r.x, y1: r.y });
          }
        }
        if (gap || jump || died) flush();
      }
      pts.push(x, y, t - entry.t0);
      prev = i;
    }
    flush();
  }

  function aggregateSegments(instances, cps) {
    const groups = new Map();
    for (const g of instances) {
      if (g.from == null) continue;
      const key = `${g.from}→${g.to || "#" + g.endReason}`;
      let a = groups.get(key);
      if (!a) groups.set(key, (a = { key, from: g.from, to: g.to, endReason: g.to ? "checkpoint" : g.endReason, instances: [] }));
      a.instances.push(g);
    }
    const out = [];
    for (const a of groups.values()) {
      const inst = a.instances;
      const deaths = inst.map(g => g.deaths || 0);
      const times = inst.map(g => (g.playDuration != null ? g.playDuration : g.duration)).filter(v => v != null);
      const bySession = new Map();
      for (const g of inst) {
        const f = bySession.get(g.session.id);
        if (!f || g.start < f.start) bySession.set(g.session.id, g);
      }
      const firsts = [...bySession.values()];
      a.passes = inst.length;
      a.total = deaths.reduce((p, c) => p + c, 0);
      a.mean = a.total / Math.max(1, inst.length);
      a.median = median(deaths);
      a.medianTime = median(times);
      a.firstTry = a.to ? firsts.filter(g => (g.deaths || 0) === 0).length / Math.max(1, firsts.length) : null;
      a.quits = inst.filter(g => g.endReason === "stop").length;
      a.sessions = bySession.size;
      a.inferred = inst.some(g => g.inferred);
      const fromCp = cps.get(a.from), toCp = a.to ? cps.get(a.to) : null;
      a.fromCp = fromCp || null;
      a.toCp = toCp || null;
      const fx = inst.map(g => g.fromX).filter(v => v != null), fy = inst.map(g => g.fromY).filter(v => v != null);
      a.fromPos = fromCp ? [fromCp.x, fromCp.y] : fx.length ? [median(fx), median(fy)] : null;
      a.toPos = toCp ? [toCp.x, toCp.y] : null;
      a.order = (fromCp ? fromCp.order : 0) * 1000 + (toCp ? toCp.order : 999);
      out.push(a);
    }
    out.sort((a, b) => a.order - b.order);
    return out;
  }

  // Kills per hazard: by source object name, matched to the layout's hazard of that name nearest
  // the kill point (prefab instances can share a name), or to a hazard tilemap
  function hazardKills(deaths, layout) {
    const groups = new Map();
    for (const d of deaths) {
      if (!d.source) continue;
      let g = groups.get(d.source);
      if (!g) groups.set(d.source, (g = { name: d.source, kills: 0, deaths: [], causes: {} }));
      g.kills++;
      g.deaths.push(d);
      g.causes[d.cause] = (g.causes[d.cause] || 0) + 1;
    }
    const out = [];
    for (const g of groups.values()) {
      const hx = mean(g.deaths.map(d => (d.hx != null ? d.hx : d.x)));
      const hy = mean(g.deaths.map(d => (d.hy != null ? d.hy : d.y)));
      g.hit = [hx, hy];
      g.cause = Object.entries(g.causes).sort((a, b) => b[1] - a[1])[0][0];
      if (layout) {
        let best = null, bestD = Infinity;
        for (const h of layout.hazards || []) {
          if (h.name !== g.name) continue;
          const d = Math.hypot(h.pos[0] - hx, h.pos[1] - hy);
          if (d < bestD) { bestD = d; best = h; }
        }
        g.layout = best;
        g.tilemap = !best && (layout.tilemaps || []).some(t => t.name === g.name && t.kind === "hazard");
        g.prefab = best ? best.prefab : "";
      }
      out.push(g);
    }
    return out.sort((a, b) => b.kills - a.kills);
  }

  function respawnTraps(deaths, respawns, cps) {
    const groups = new Map();
    for (const d of deaths) {
      if (d.life == null || d.life > TRAP_LIFE) continue;
      const r = [...respawns].filter(r => r.session === d.session && r.t <= d.t).sort((a, b) => b.t - a.t)[0];
      if (!r || Math.hypot(r.x - d.x, r.y - d.y) > TRAP_DIST) continue;
      let cp = null, bestD = 1.5;
      for (const c of cps.values()) {
        const dist = Math.hypot(c.x - r.x, c.y - r.y);
        if (dist < bestD) { bestD = dist; cp = c; }
      }
      const key = cp ? cp.key : `${r.x.toFixed(0)},${r.y.toFixed(0)}`;
      let g = groups.get(key);
      if (!g) groups.set(key, (g = { key, cp, x: r.x, y: r.y, deaths: [] }));
      g.deaths.push(d);
    }
    return [...groups.values()].map(g => ({ ...g, count: g.deaths.length, avgLife: mean(g.deaths.map(d => d.life)) }))
      .sort((a, b) => b.count - a.count);
  }

  // ---------- difficulty curve (port of Docs/Tool/difficulty_curve.py) ----------

  function curveData(s) {
    if (s._curve) return s._curve;
    const smp = s.samples;
    const game = [];
    for (let i = 0; i < smp.n; i++) if (IK.parse.isGameplayScene(smp.sceneNames[smp.scene[i]])) game.push(i);
    if (game.length < 2) return (s._curve = null);
    const t0 = smp.t[game[0]], t1 = smp.t[game[game.length - 1]];
    const span = t1 - t0;
    if (span <= 0) return (s._curve = null);
    const events = s.events.deaths.filter(d => IK.parse.isGameplayScene(d.scene) && d.t >= t0 && d.t <= t1 + 1)
      .map(d => d.t).sort((a, b) => a - b);
    return (s._curve = { t0, span, events, pct: events.map(t => ((t - t0) / span) * 100) });
  }

  // ---------- helpers ----------

  function median(arr) {
    const a = arr.filter(v => v != null && isFinite(v)).sort((x, y) => x - y);
    if (!a.length) return null;
    const m = a.length >> 1;
    return a.length % 2 ? a[m] : (a[m - 1] + a[m]) / 2;
  }
  function mean(arr) {
    const a = arr.filter(v => v != null && isFinite(v));
    return a.length ? a.reduce((p, c) => p + c, 0) / a.length : null;
  }

  function cpLabel(data, key) {
    if (key === "entry") return IK.t("seg.entry");
    if (key === "rec_start") return IK.t("seg.recStart");
    if (key == null) return "";
    const cp = data.cpByKey.get(key);
    return cp ? "CP" + String(cp.order).padStart(2, "0") : key;
  }
  function segLabel(data, seg) {
    const to = seg.to ? cpLabel(data, seg.to) : seg.endReason === "stop" ? IK.t("seg.stop") : IK.t("seg.exit");
    return `${cpLabel(data, seg.from)} → ${to}`;
  }

  IK.model = {
    get sessions() { return sessions; },
    get layouts() { return layouts; },
    filters,
    addSession, removeSession, setEnabled, setAllEnabled, setLayouts, setCauseEnabled, setBuild,
    selected, sceneList, sessionStats, sceneData, curveData,
    cpLabel, segLabel, median, mean,
    onChange(fn) { listeners.push(fn); },
    TRAP_LIFE,
  };
})();
