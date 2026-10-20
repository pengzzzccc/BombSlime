/* PerfLog parser. A log is: '#' header comments (key=value), one column header starting with
   time_s, sample rows, '# scene=' markers, '#E-schema' + '#E,' event lines (format 2) and a closing
   '# summary'. Columns and event fields are located by name, never by position, so older and newer
   logs both read. Logs without event lines get deaths inferred from the cumulative deaths column. */
(function () {
  "use strict";
  const IK = (window.IK = window.IK || {});

  const NUMERIC_EVENT_FIELDS = new Set([
    "time_s", "play_s", "x", "y", "hit_x", "hit_y", "vx", "vy", "life_s", "seg", "seg_deaths",
    "from_x", "from_y", "to_x", "to_y", "start_s", "duration_s", "play_dur_s", "deaths",
  ]);
  const MENU_SCENES = new Set(["MainMenu", "End", ""]);

  // "# unity=6000.4 platform=WindowsPlayer" → {unity, platform}; values may contain spaces
  function parseKeyValues(text) {
    const out = {};
    const re = /(?:^|\s)([A-Za-z_][\w]*)=/g;
    const hits = [];
    let m;
    while ((m = re.exec(text))) hits.push({ key: m[1], start: m.index + m[0].length, at: m.index });
    hits.forEach((h, i) => {
      const end = i + 1 < hits.length ? hits[i + 1].at : text.length;
      out[h.key] = text.slice(h.start, end).trim();
    });
    return out;
  }

  // Split a CSV line; quotes are honored but rare (the recorder never writes them)
  function splitCsv(line) {
    if (line.indexOf('"') < 0) return line.split(",");
    const out = [];
    let cur = "", q = false;
    for (let i = 0; i < line.length; i++) {
      const c = line[i];
      if (q) {
        if (c === '"') { if (line[i + 1] === '"') { cur += '"'; i++; } else q = false; }
        else cur += c;
      } else if (c === '"') q = true;
      else if (c === ",") { out.push(cur); cur = ""; }
      else cur += c;
    }
    out.push(cur);
    return out;
  }

  class Interner {
    constructor() { this.list = []; this.map = new Map(); }
    id(s) {
      let i = this.map.get(s);
      if (i === undefined) { i = this.list.length; this.list.push(s); this.map.set(s, i); }
      return i;
    }
  }

  /**
   * Parses one log. Runs in slices (await between chunks) so a 10 MB file never freezes the page;
   * onProgress(fraction) is called between slices.
   */
  async function parseLog(text, fileName, onProgress) {
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    const lines = text.split(/\r?\n/);

    const meta = {};
    const markers = [];
    const schemas = {};
    const rawEvents = [];
    let summary = null;
    let header = null, col = null;

    const scenes = new Interner(), states = new Interner(), ropes = new Interner(), gstates = new Interner();
    const cols = {
      t: [], scene: [], x: [], y: [], fps: [], fpsMin: [], frame: [], maxFrame: [], gc: [], hitches: [],
      deaths: [], state: [], rope: [], gstate: [], draw: [], tris: [], voices: [],
    };

    const SLICE = 8000;
    for (let i = 0; i < lines.length; i++) {
      if (i % SLICE === 0 && i > 0) {
        if (onProgress) onProgress(i / lines.length);
        await new Promise(r => setTimeout(r, 0));
      }
      const line = lines[i];
      if (!line) continue;

      if (line.charCodeAt(0) === 35 /* # */) {
        if (line.startsWith("#E,")) { rawEvents.push(line); continue; }
        if (line.startsWith("#E-schema,")) {
          const parts = line.split(",");
          schemas[parts[1]] = parts.slice(2);
          continue;
        }
        const body = line.replace(/^#\s*/, "");
        if (body.startsWith("scene=")) {
          const m = /^scene=(.*?)\s+t=([\d.]+)s?\s*$/.exec(body);
          if (m) markers.push({ scene: m[1], t: parseFloat(m[2]) });
          continue;
        }
        if (body.startsWith("summary")) { summary = parseKeyValues(body.slice(7)); continue; }
        if (body.indexOf("=") > 0) Object.assign(meta, parseKeyValues(body));
        continue;
      }

      const parts = splitCsv(line);
      if (!header) {
        if (parts[0].trim() === "time_s") {
          header = parts.map(p => p.trim());
          col = name => header.indexOf(name);
          col.t = col("time_s"); col.scene = col("scene"); col.x = col("x"); col.y = col("y");
          col.fps = col("fps_avg"); col.fpsMin = col("fps_min"); col.frame = col("frame_ms"); col.maxFrame = col("max_frame_ms");
          col.gc = col("gc_mb"); col.hitches = col("hitches"); col.deaths = col("deaths"); col.state = col("state");
          col.rope = col("rope"); col.gstate = col("game_state"); col.draw = col("draw_calls"); col.tris = col("tris");
          col.voices = col("voices");
        }
        continue;
      }
      if (parts.length < header.length) continue;
      const t = parseFloat(parts[col.t]);
      if (!isFinite(t)) continue;

      const num = (c, d) => { if (c < 0) return d; const v = parseFloat(parts[c]); return isFinite(v) ? v : d; };
      cols.t.push(t);
      cols.scene.push(scenes.id(col.scene >= 0 ? parts[col.scene].trim() : ""));
      cols.x.push(num(col.x, 0)); cols.y.push(num(col.y, 0));
      cols.fps.push(num(col.fps, NaN)); cols.fpsMin.push(num(col.fpsMin, NaN));
      cols.frame.push(num(col.frame, NaN)); cols.maxFrame.push(num(col.maxFrame, NaN));
      cols.gc.push(num(col.gc, NaN)); cols.hitches.push(num(col.hitches, 0));
      cols.deaths.push(num(col.deaths, 0));
      cols.state.push(states.id(col.state >= 0 ? parts[col.state] : ""));
      cols.rope.push(ropes.id(col.rope >= 0 ? parts[col.rope] : ""));
      cols.gstate.push(gstates.id(col.gstate >= 0 ? parts[col.gstate] : ""));
      cols.draw.push(num(col.draw, -1)); cols.tris.push(num(col.tris, -1));
      cols.voices.push(num(col.voices, 0));
    }
    if (!header || cols.t.length === 0) return null;

    const n = cols.t.length;
    const samples = {
      n,
      t: Float64Array.from(cols.t),
      scene: Uint16Array.from(cols.scene),
      x: Float32Array.from(cols.x), y: Float32Array.from(cols.y),
      fps: Float32Array.from(cols.fps), fpsMin: Float32Array.from(cols.fpsMin),
      frame: Float32Array.from(cols.frame), maxFrame: Float32Array.from(cols.maxFrame),
      gc: Float32Array.from(cols.gc), hitches: Float32Array.from(cols.hitches),
      deaths: Float32Array.from(cols.deaths),
      state: Uint8Array.from(cols.state), rope: Uint8Array.from(cols.rope), gstate: Uint8Array.from(cols.gstate),
      draw: Float32Array.from(cols.draw), tris: Float32Array.from(cols.tris),
      sceneNames: scenes.list, stateNames: states.list, ropeNames: ropes.list, gstateNames: gstates.list,
    };

    // Median sample spacing: the recorder's interval, used to tell gaps from consecutive samples
    const gaps = [];
    for (let i = 1; i < Math.min(n, 400); i++) gaps.push(samples.t[i] - samples.t[i - 1]);
    gaps.sort((a, b) => a - b);
    const interval = gaps.length ? Math.max(0.05, gaps[gaps.length >> 1]) : 1;

    const events = { deaths: [], respawns: [], checkpoints: [], segments: [], unstucks: [] };
    for (const line of rawEvents) {
      const parts = splitCsv(line);
      const kind = parts[1];
      const names = schemas[kind];
      if (!names) continue;
      const ev = { kind };
      for (let i = 0; i < names.length; i++) {
        const raw = parts[i + 2] ?? "";
        if (NUMERIC_EVENT_FIELDS.has(names[i])) ev[names[i]] = raw === "" ? null : parseFloat(raw);
        else ev[names[i]] = raw;
      }
      if (kind === "death") events.deaths.push(normalizeDeath(ev));
      else if (kind === "respawn") events.respawns.push({ t: ev.time_s, play: ev.play_s, scene: ev.scene, x: ev.x, y: ev.y, seg: ev.seg });
      else if (kind === "checkpoint") events.checkpoints.push({ t: ev.time_s, play: ev.play_s, scene: ev.scene, x: ev.x, y: ev.y, name: ev.name, seg: ev.seg });
      else if (kind === "segment") events.segments.push(normalizeSegment(ev));
      else if (kind === "unstuck") events.unstucks.push({ t: ev.time_s, play: ev.play_s, scene: ev.scene, x: ev.x, y: ev.y, toX: ev.to_x, toY: ev.to_y, method: ev.method, seg: ev.seg });
    }

    const hasEvents = rawEvents.length > 0 && Object.keys(schemas).length > 0;
    if (!hasEvents) events.deaths = inferDeaths(samples);

    return {
      file: fileName,
      name: shortName(fileName, meta),
      meta,
      format: hasEvents ? 2 : 1,
      hasEvents,
      samples,
      interval,
      markers,
      summary,
      events,
      duration: samples.t[n - 1] - samples.t[0],
    };
  }

  function normalizeDeath(ev) {
    return {
      t: ev.time_s, play: ev.play_s, scene: ev.scene,
      x: ev.x, y: ev.y, hx: ev.hit_x, hy: ev.hit_y,
      cause: ev.cause || "Unknown", source: ev.source || "", state: ev.state || "", rope: ev.rope || "",
      vx: ev.vx || 0, vy: ev.vy || 0, life: ev.life_s, seg: ev.seg, segDeaths: ev.seg_deaths,
      inferred: false,
    };
  }

  function normalizeSegment(ev) {
    return {
      t: ev.time_s, play: ev.play_s, scene: ev.scene, seg: ev.seg,
      from: ev.from, fromX: ev.from_x, fromY: ev.from_y,
      to: ev.to, toX: ev.to_x, toY: ev.to_y,
      start: ev.start_s, duration: ev.duration_s, playDuration: ev.play_dur_s,
      deaths: ev.deaths, endReason: ev.end_reason, inferred: false,
    };
  }

  // Old logs: a death is a rise of the cumulative column. The row that shows the rise was sampled
  // after the death (often already at the respawn), so the row before it is the better position.
  function inferDeaths(s) {
    const out = [];
    for (let i = 1; i < s.n; i++) {
      const d = s.deaths[i] - s.deaths[i - 1];
      if (d <= 0 || d > 20) continue;
      const scene = s.sceneNames[s.scene[i]];
      const j = s.scene[i - 1] === s.scene[i] ? i - 1 : i;
      if (s.x[j] === 0 && s.y[j] === 0) continue;
      for (let k = 0; k < d; k++) {
        out.push({
          t: s.t[i], play: null, scene, x: s.x[j], y: s.y[j], hx: null, hy: null,
          cause: "Unknown", source: "", state: s.stateNames[s.state[j]] || "", rope: "",
          vx: 0, vy: 0, life: null, seg: null, segDeaths: null, inferred: true,
        });
      }
    }
    return out;
  }

  function shortName(file, meta) {
    const m = /perf_(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})/.exec(file);
    if (m) return `${m[2]}-${m[3]} ${m[4]}:${m[5]}`;
    if (meta.startedAt) return meta.startedAt.replace("T", " ").slice(5, 16);
    return file.replace(/\.csv$/i, "");
  }

  function isGameplayScene(name) { return !MENU_SCENES.has(name); }

  IK.parse = { parseLog, parseKeyValues, splitCsv, isGameplayScene, MENU_SCENES };
})();
