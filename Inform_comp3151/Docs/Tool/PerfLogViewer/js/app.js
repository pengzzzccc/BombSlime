/* App shell: file intake (drag & drop / picker), sidebar, scene tabs, views, and the wiring between
   the model, the map and the charts. State that only matters to this viewer (tab, scene, language,
   theme, map layers) is remembered per browser; nothing is ever sent anywhere. */
(function () {
  "use strict";
  const IK = window.IK;
  const t = IK.t, esc = IK.charts.esc, M = IK.model;
  const $ = s => document.querySelector(s);

  const state = { view: "map", scene: null, curveWindow: 5, curveX: "norm" };
  try { Object.assign(state, JSON.parse(localStorage.getItem("ik.app") || "{}")); } catch (e) { /* defaults */ }
  const persist = () => { try { localStorage.setItem("ik.app", JSON.stringify({ view: state.view, scene: state.scene, curveWindow: state.curveWindow, curveX: state.curveX })); } catch (e) { /* not persisted */ } };

  let map = null;
  const renderedViews = new Set();

  // ---------- boot ----------

  function boot() {
    IK.theme.refresh();
    IK.i18n.apply();
    $("#btnLang").textContent = IK.i18n.lang === "zh" ? "EN" : "中";

    if (window.INKFORM_LAYOUTS && window.INKFORM_LAYOUTS.scenes) M.setLayouts(window.INKFORM_LAYOUTS);

    map = new IK.MapView({
      canvas: $("#mapCanvas"), shell: $("#mapShell"), tooltip: $("#mapTip"), legend: $("#mapLegend"),
      minimap: $("#minimap"), scaleLabel: $("#scaleLabel"), scaleRule: $("#scaleRule"), hint: $("#mapHint"),
      onReplayTick: updateReplayUi,
    });
    map.onArtChange = syncToolbar;
    map.onClick = hit => {
      if (hit.type === "segment") { map.focusSegment(hit.data); }
      if (hit.type === "cluster") {
        const xs = hit.data.map(p => p.x), ys = hit.data.map(p => p.y);
        map.moveTo((Math.min(...xs) + Math.max(...xs)) / 2, (Math.min(...ys) + Math.max(...ys)) / 2, Math.max(map.cam.z * 2.5, 9));
      }
    };

    bindChrome();
    buildLayerPanel();
    M.onChange(onModelChange);
    IK.theme.onChange(() => { map.setTheme(); rerenderViews(); });
    IK.i18n.onChange(() => {
      $("#btnLang").textContent = IK.i18n.lang === "zh" ? "EN" : "中";
      buildLayerPanel(); renderSidebar(); renderSceneTabs(); map.buildLegend(); rerenderViews(); updateLayoutStatus();
    });
    updateLayoutStatus();
    renderSidebar();
    renderSceneTabs();
    showView(state.view);
    updateEmpty();
  }

  function onModelChange() {
    const scenes = M.sceneList();
    if (!scenes.some(s => s.name === state.scene)) state.scene = scenes.length ? scenes[0].name : null;
    renderSidebar();
    renderSceneTabs();
    updateLayoutStatus();
    updateEmpty();
    pushMap();
    rerenderViews();
  }

  function pushMap() {
    if (!map) return;
    if (!state.scene) { map.setData(null, null); return; }
    const layout = M.layouts && M.layouts.scenes[state.scene] ? M.layouts.scenes[state.scene] : null;
    map.setData(M.sceneData(state.scene), layout);
    updateReplayUi(map.replay);
  }

  function updateEmpty() {
    $("#dropzone").classList.toggle("hidden", M.sessions.length > 0);
  }

  // ---------- file intake ----------

  function bindChrome() {
    const input = $("#fileInput");
    $("#btnAdd").addEventListener("click", () => input.click());
    $("#btnPick").addEventListener("click", () => input.click());
    input.addEventListener("change", () => { loadFiles([...input.files]); input.value = ""; });

    let depth = 0;
    window.addEventListener("dragenter", e => { if (hasFiles(e)) { depth++; $("#dragOverlay").classList.add("show"); e.preventDefault(); } });
    window.addEventListener("dragover", e => { if (hasFiles(e)) e.preventDefault(); });
    window.addEventListener("dragleave", () => { depth = Math.max(0, depth - 1); if (!depth) $("#dragOverlay").classList.remove("show"); });
    window.addEventListener("drop", e => {
      e.preventDefault();
      depth = 0;
      $("#dragOverlay").classList.remove("show");
      if (e.dataTransfer && e.dataTransfer.files.length) loadFiles([...e.dataTransfer.files]);
    });

    $("#btnLang").addEventListener("click", () => IK.i18n.setLang(IK.i18n.lang === "zh" ? "en" : "zh"));
    $("#btnTheme").addEventListener("click", () => IK.theme.setTheme(IK.theme.current.dark ? "light" : "dark"));
    $("#btnSidebar").addEventListener("click", () => $("#sidebar").classList.toggle("open"));
    $("#selAll").addEventListener("click", () => M.setAllEnabled(true));
    $("#selNone").addEventListener("click", () => M.setAllEnabled(false));
    $("#buildFilter").addEventListener("change", e => M.setBuild(e.target.value));
    $("#btnExport").addEventListener("click", exportMap);

    $("#viewTabs").addEventListener("click", e => { const b = e.target.closest("[data-view]"); if (b) showView(b.dataset.view); });

    // map toolbar
    $("#baseMode").addEventListener("click", e => {
      const b = e.target.closest("[data-mode]");
      if (!b || b.disabled) return;
      map.setOption("baseMode", b.dataset.mode);
      map.minimapBase = null;
      map.updateHint();
      syncToolbar();
    });
    $("#dimSlider").addEventListener("input", e => { map.setOption("dim", +e.target.value / 100); });
    $("#btnLayers").addEventListener("click", () => {
      const p = $("#layerPanel"), on = p.classList.toggle("hidden") === false;
      $("#btnLayers").setAttribute("aria-pressed", String(on));
    });
    $("#btnFit").addEventListener("click", () => map.fit());
    $("#btnHot").addEventListener("click", () => map.focusHottest());
    $("#btnReplay").addEventListener("click", () => {
      const on = !map.replay.on;
      map.setReplay(on);
      $("#btnReplay").setAttribute("aria-pressed", String(on));
      $("#replayBar").classList.toggle("hidden", !on);
      $("#replayPanel").classList.toggle("hidden", !on);
      $("#minimapWrap").classList.toggle("hidden", on);
      if (on && !map.replay.playing) map.toggleReplayPlay();
      updateReplayUi(map.replay);
    });
    $("#rpPlay").addEventListener("click", () => map.toggleReplayPlay());
    $("#rpSlider").addEventListener("input", e => {
      if (!map.data) return;
      map.replay.playing = false;
      map.seekReplay((+e.target.value / 1000) * map.data.maxTime);
    });
    $("#rpSpeed").addEventListener("change", e => (map.replay.speed = +e.target.value));
    syncToolbar();
  }

  function hasFiles(e) { return e.dataTransfer && [...(e.dataTransfer.types || [])].includes("Files"); }

  async function loadFiles(files) {
    const prog = $("#progress"), bar = $("#progressBar"), txt = $("#progressText");
    let loaded = 0;
    const logs = files.filter(f => !/\.json$/i.test(f.name));
    for (const f of files.filter(f => /\.json$/i.test(f.name))) {
      try {
        const obj = JSON.parse(await f.text());
        if (obj && obj.scenes) { M.setLayouts(obj); toast(t("toast.layout", { n: Object.keys(obj.scenes).length })); }
        else toast(t("toast.skipped", { name: f.name }));
      } catch (e) { toast(t("toast.skipped", { name: f.name })); }
    }
    if (!logs.length) return;
    prog.classList.remove("hidden");
    for (let i = 0; i < logs.length; i++) {
      const f = logs[i];
      txt.textContent = t("progress.parsing", { name: f.name, i: i + 1, n: logs.length });
      bar.style.width = (i / logs.length) * 100 + "%";
      try {
        const text = await f.text();
        const s = await IK.parse.parseLog(text, f.name, p => (bar.style.width = ((i + p) / logs.length) * 100 + "%"));
        if (!s) { toast(t("toast.skipped", { name: f.name })); continue; }
        if (M.addSession(s)) loaded++;
        else toast(t("toast.dup", { name: f.name }));
      } catch (e) {
        console.error(e);
        toast(t("toast.skipped", { name: f.name }));
      }
    }
    bar.style.width = "100%";
    setTimeout(() => prog.classList.add("hidden"), 300);
    if (loaded) toast(t("toast.loaded", { n: loaded }));
  }

  let toastTimer = 0;
  function toast(msg) {
    const el = $("#toast");
    el.textContent = msg;
    el.classList.remove("hidden");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.add("hidden"), 2600);
  }

  // ---------- sidebar ----------

  function renderSidebar() {
    const list = $("#sessionList");
    const T = IK.theme.current;
    list.innerHTML = M.sessions.map(s => {
      const st = M.sessionStats(s);
      const build = s.build !== "unknown" ? s.build : "";
      const passes = M.filters.build === "all" || s.build === M.filters.build;
      return `<li class="session ${s.enabled && passes ? "" : "off"}" data-id="${s.id}">
        <span class="dot" style="background:${IK.theme.sessionColor(s.slot)}"></span>
        <span class="title">${esc(s.name)}</span>
        <span class="deaths num">${t("side.deathsN", { n: st.deaths })}</span>
        <span class="sub"><span class="num">${IK.fmtTime(st.time)}</span>${build ? `<span>${esc(build)}</span>` : ""}<span class="chip ${s.hasEvents ? "events" : ""}">${s.hasEvents ? t("side.events") : t("side.inferred")}</span></span>
        <button class="remove" data-remove="${s.id}" title="${t("side.remove")}">✕</button>
      </li>`;
    }).join("");
    list.querySelectorAll(".session").forEach(li => {
      li.addEventListener("click", e => {
        if (e.target.closest("[data-remove]")) { M.removeSession(li.dataset.id); return; }
        const s = M.sessions.find(o => o.id === li.dataset.id);
        M.setEnabled(s.id, !s.enabled);
      });
      li.addEventListener("mouseenter", () => { map.highlight = li.dataset.id; map.request(); });
      li.addEventListener("mouseleave", () => { map.highlight = null; map.request(); });
    });

    // build filter
    const builds = [...new Set(M.sessions.map(s => s.build))];
    const sel = $("#buildFilter");
    sel.innerHTML = `<option value="all">${t("side.allBuilds")}</option>` + builds.map(b => `<option value="${esc(b)}">${esc(b)}</option>`).join("");
    sel.value = M.filters.build;
    sel.closest(".side-section").classList.toggle("hidden", builds.length < 2);

    // cause chips with counts for the current scene
    const data = state.scene ? M.sceneData(state.scene) : null;
    const counts = {};
    if (data) data.deathsAll.forEach(d => (counts[d.inferred ? "Unknown" : d.cause] = (counts[d.inferred ? "Unknown" : d.cause] || 0) + 1));
    const chips = IK.theme.CAUSE_ORDER.filter(c => counts[c] || c === "Spike");
    $("#causeChips").innerHTML = chips.map(c => `<button class="chip-toggle" data-cause="${c}" aria-pressed="${M.filters.causes.has(c)}">
        ${IK.theme.shapeSvg(c === "Unknown" ? "ring" : T.causeShape(c), T.causeColor(c), c === "Unknown")}${t("cause." + c)}<span class="count num">${counts[c] || 0}</span></button>`).join("");
    $("#causeChips").querySelectorAll("[data-cause]").forEach(b => b.addEventListener("click", () => M.setCauseEnabled(b.dataset.cause, !M.filters.causes.has(b.dataset.cause))));
  }

  function updateLayoutStatus() {
    const el = $("#layoutStatus");
    const L = M.layouts;
    if (!L) { el.innerHTML = t("layout.missing"); el.classList.add("warn"); return; }
    const n = Object.keys(L.scenes).length;
    const art = Object.values(L.scenes).filter(s => s.art).length;
    el.classList.toggle("warn", !art);
    el.innerHTML = t("layout.loaded", { n, at: esc((L.exportedAt || "").replace("T", " ")) }) + " " + (art ? t("layout.art", { n: art }) : t("layout.noArt"));
  }

  function renderSceneTabs() {
    const nav = $("#sceneTabs");
    const scenes = M.sceneList();
    nav.innerHTML = scenes.map(s => `<button class="scene-tab" role="tab" data-scene="${esc(s.name)}" aria-selected="${s.name === state.scene}">${esc(s.name)}<span class="badge num">☠ ${s.deaths}</span></button>`).join("");
    nav.querySelectorAll("[data-scene]").forEach(b => b.addEventListener("click", () => {
      state.scene = b.dataset.scene;
      persist();
      renderSceneTabs();
      renderSidebar();
      pushMap();
      rerenderViews();
    }));
  }

  // ---------- views ----------

  function showView(v) {
    state.view = v;
    persist();
    document.querySelectorAll(".view-tab").forEach(b => b.setAttribute("aria-selected", String(b.dataset.view === v)));
    document.querySelectorAll(".view").forEach(s => s.classList.toggle("active", s.id === "view-" + v));
    $("#btnExport").classList.toggle("hidden", v !== "map");
    if (v === "map") { map.resize(); map.request(); }
    else renderView(v);
  }

  function rerenderViews() {
    renderedViews.clear();
    if (state.view !== "map") renderView(state.view);
  }

  function renderView(v) {
    if (renderedViews.has(v)) return;
    renderedViews.add(v);
    const root = $("#view-" + v);
    IK.charts.disposeIn(root);
    root.innerHTML = "";
    if (!M.sessions.length) return;
    ({ overview: renderOverview, segments: renderSegments, deaths: renderDeaths, curve: renderCurve, perf: renderPerf })[v](root);
  }

  function head(root, title, sub, extra) {
    const h = document.createElement("div");
    h.className = "view-head";
    h.innerHTML = `<h2>${esc(title)}</h2><p>${esc(sub || "")}</p>${extra || ""}`;
    root.appendChild(h);
    return h;
  }
  function gridEl(root) { const g = document.createElement("div"); g.className = "grid"; root.appendChild(g); return g; }

  // ---- overview ----

  function renderOverview(root) {
    const sel = M.selected();
    const scenes = M.sceneList();
    head(root, t("ov.title"), t("ov.sub", { n: sel.length, scenes: scenes.length }));
    const T = IK.theme.current;

    let time = 0, deaths = 0, fpsW = 0, fpsT = 0, hitches = 0;
    sel.forEach(s => {
      const st = M.sessionStats(s);
      time += st.time; deaths += st.deaths; hitches += st.hitches;
      if (isFinite(st.fps)) { fpsW += st.fps * st.time; fpsT += st.time; }
    });
    const hours = Math.floor(time / 3600), mins = Math.round((time % 3600) / 60);

    // progress-binned sparklines: deaths and fps across normalized session progress
    const BINS = 24, dBins = new Array(BINS).fill(0), fBins = new Array(BINS).fill(0), fN = new Array(BINS).fill(0);
    sel.forEach(s => {
      const c = M.curveData(s);
      if (!c) return;
      c.pct.forEach(p => dBins[Math.min(BINS - 1, Math.floor((p / 100) * BINS))]++);
      const smp = s.samples;
      for (let i = 0; i < smp.n; i++) {
        const p = (smp.t[i] - c.t0) / c.span;
        if (p < 0 || p > 1 || !isFinite(smp.fps[i])) continue;
        const b = Math.min(BINS - 1, Math.floor(p * BINS));
        fBins[b] += smp.fps[i]; fN[b]++;
      }
    });

    let coverageHtml = "";
    if (state.scene && M.layouts && M.layouts.scenes[state.scene]) {
      const cov = IK.heat.coverage(M.sceneData(state.scene), M.layouts.scenes[state.scene]);
      if (cov && cov.surface) coverageHtml = kpi(t("kpi.coverage"), Math.round(cov.ratio * 100) + "<small>%</small>", t("kpi.coverage.foot", { scene: state.scene }));
    }

    const k = document.createElement("div");
    k.className = "kpis";
    k.innerHTML =
      kpi(t("kpi.sessions"), sel.length, sel.filter(s => s.hasEvents).length + " / " + sel.length + " " + t("side.events")) +
      kpi(t("kpi.playtime"), hours ? `${hours}<small>h</small> ${mins}<small>m</small>` : `${mins}<small>${t("kpi.min")}</small>`, "") +
      kpi(t("kpi.deaths"), deaths, "", "sparkD") +
      kpi(t("kpi.rate"), time ? (deaths / (time / 60)).toFixed(2) : "–", "") +
      kpi(t("kpi.fps"), fpsT ? Math.round(fpsW / fpsT) : "–", "", "sparkF") +
      kpi(t("kpi.hitches"), Math.round(hitches), "") +
      coverageHtml;
    root.appendChild(k);
    const sd = k.querySelector("#sparkD"), sf = k.querySelector("#sparkF");
    if (sd) IK.charts.sparkline(sd, dBins, T.ramps.death.stops[1]);
    if (sf) IK.charts.sparkline(sf, fBins.map((v, i) => (fN[i] ? Math.round(v / fN[i]) : null)), T.series[0]);

    const g = gridEl(root);
    new IK.charts.Card(g, { title: t("chart.sceneTime"), sub: t("chart.sceneTime.sub"), span: 6 }).render(IK.charts.sceneBars(scenes, "time"));
    new IK.charts.Card(g, { title: t("chart.sceneDeaths"), sub: t("chart.sceneDeaths.sub"), span: 6 }).render(IK.charts.sceneBars(scenes, "deaths"));

    const tableCard = new IK.charts.Card(g, { title: t("chart.sessions"), span: 12, noChart: true, csv: true });
    tableCard.render({
      table: {
        sortBy: 0, asc: true,
        columns: [{ label: t("col.session") }, { label: t("col.build") }, { label: t("col.device") }, { label: t("col.duration"), num: true },
          { label: t("col.deaths"), num: true }, { label: t("col.rate"), num: true }, { label: t("col.fps"), num: true }, { label: t("col.format") }],
        rows: sel.map(s => {
          const st = M.sessionStats(s);
          const device = [s.meta.gpu, s.meta.quality].filter(Boolean).join(" · ");
          return {
            cells: [`<span class="sw" style="background:${IK.theme.sessionColor(s.slot)}"></span>${esc(s.name)}`, esc(s.build), esc(device), IK.fmtTime(st.time), st.deaths, st.rate.toFixed(2), isFinite(st.fps) ? Math.round(st.fps) : "–", s.hasEvents ? "v2 · " + t("side.events") : "v1 · " + t("side.inferred")],
            raw: [s.meta.startedAt || s.name, s.build, device, st.time, st.deaths, st.rate, st.fps, s.format],
          };
        }),
      },
    });
  }

  function kpi(label, value, foot, sparkId) {
    return `<div class="kpi"><span class="label">${esc(label)}</span><span class="value">${value}</span>${sparkId ? `<div class="spark" id="${sparkId}"></div>` : ""}${foot ? `<span class="foot">${esc(foot)}</span>` : ""}</div>`;
  }

  // ---- segments ----

  function renderSegments(root) {
    if (!state.scene) return;
    const data = M.sceneData(state.scene);
    head(root, t("sg.title"), t("sg.sub", { scene: state.scene }), data.anyInferred ? `<p class="muted">ⓘ ${esc(t("seg.inferredNote"))}</p>` : "");
    const g = gridEl(root);
    const segs = data.segments.filter(s => s.passes);
    const maxMean = Math.max(0.01, ...segs.map(s => s.mean));
    const T = IK.theme.current;
    const ramp = T.ramps.death.stops;
    const maxTotal = Math.max(1, ...segs.map(s => s.total));
    const pill = key => `<span class="cp-pill">${esc(M.cpLabel(data, key))}</span>`;

    new IK.charts.Card(g, { title: t("sg.table"), span: 12, noChart: true, csv: true }).render({
      table: {
        onRow: i => { showView("map"); map.focusSegment(segs[i]); },
        columns: [{ label: "#", num: true }, { label: t("col.segment") }, { label: t("col.attempts"), num: true }, { label: t("col.total"), num: true },
          { label: t("col.mean"), num: true }, { label: t("col.median"), num: true }, { label: t("col.firstTry"), num: true },
          { label: t("col.time"), num: true }, { label: t("col.quits"), num: true }, { label: t("col.difficulty") }],
        rows: segs.map((s, i) => {
          const heat = s.total / maxTotal;
          const bg = IK.theme.rgba(ramp[1], 0.08 + heat * 0.5);
          const to = s.to ? pill(s.to) : `<span class="muted">${esc(s.endReason === "stop" ? t("seg.stop") : t("seg.exit"))}</span>`;
          return {
            i,
            cells: [i + 1, `<span class="seg-name">${pill(s.from)} → ${to}${s.inferred ? ' <span class="muted">*</span>' : ""}</span>`, s.passes,
              `<span class="cell-heat" style="background:${bg}">${s.total}</span>`, s.mean.toFixed(2), s.median != null ? s.median : "–",
              s.firstTry != null ? Math.round(s.firstTry * 100) + "%" : "–", IK.fmtTime(s.medianTime), s.quits || "",
              `<div class="bar-cell"><div class="track"><div class="bar" style="width:${(s.mean / maxMean) * 100}%"></div></div>${IK.theme.ratingHtml(s.mean)}</div>`],
            raw: [i, M.segLabel(data, s), s.passes, s.total, s.mean, s.median, s.firstTry, s.medianTime, s.quits, s.mean],
          };
        }),
      },
    });
    // one row per segment: the bar chart grows with the route instead of skipping labels
    const rowsH = Math.max(320, 50 + segs.length * 20) + "px";
    const meanCard = new IK.charts.Card(g, { title: t("chart.segMean"), sub: t("chart.segMean.sub"), span: 6 });
    const scatterCard = new IK.charts.Card(g, { title: t("chart.segScatter"), sub: t("chart.segScatter.sub"), span: 6 });
    if (meanCard.chartEl) meanCard.chartEl.style.height = scatterCard.chartEl.style.height = rowsH;
    meanCard.render(withTable(IK.charts.segMeanBars(data), segs, data));
    scatterCard.render(withTable(IK.charts.segScatter(data), segs, data));
    const matrix = new IK.charts.Card(g, { title: t("chart.segMatrix"), sub: t("chart.segMatrix.sub"), span: 12 });
    if (matrix.chartEl) matrix.chartEl.style.height = Math.max(240, 60 + segs.length * 26) + "px";
    matrix.render(withTable(IK.charts.segMatrix(data), segs, data));
  }

  function withTable(spec, segs, data) {
    if (spec.empty || spec.table) return spec;
    spec.table = {
      columns: [{ label: t("col.segment") }, { label: t("col.mean"), num: true }, { label: t("col.total"), num: true }, { label: t("col.time"), num: true }],
      rows: segs.map(s => ({ cells: [esc(M.segLabel(data, s)), s.mean.toFixed(2), s.total, IK.fmtTime(s.medianTime)], raw: [s.order, s.mean, s.total, s.medianTime] })),
    };
    return spec;
  }

  // ---- deaths ----

  function renderDeaths(root) {
    if (!state.scene) return;
    const data = M.sceneData(state.scene);
    head(root, t("dt.title"), t("dt.sub", { scene: state.scene, n: data.deaths.length }), !data.anyEvents ? `<p class="muted">ⓘ ${esc(t("noEvents"))}</p>` : "");
    const g = gridEl(root);
    new IK.charts.Card(g, { title: t("chart.causes"), span: 5, size: "short" }).render(IK.charts.causeBars(data));
    new IK.charts.Card(g, { title: t("chart.hazards"), sub: t("chart.hazards.sub"), span: 7, size: "short" }).render(IK.charts.hazardBars(data));
    new IK.charts.Card(g, { title: t("chart.states"), span: 5 }).render(IK.charts.stateBars(data));
    new IK.charts.Card(g, { title: t("chart.life"), sub: t("chart.life.sub"), span: 7 }).render(IK.charts.lifeHistogram(data));

    const traps = data.traps;
    const card = new IK.charts.Card(g, { title: t("chart.traps"), sub: t("chart.traps.sub"), span: 12, noChart: true, csv: true });
    if (!traps.length) {
      card.tableEl.innerHTML = `<div class="empty-note">${esc(data.anyEvents ? t("trap.none") : t("noEvents"))}</div>`;
      return;
    }
    card.render({
      table: {
        onRow: i => { showView("map"); map.moveTo(traps[i].x, traps[i].y, 18); },
        columns: [{ label: t("col.checkpoint") }, { label: t("col.where") }, { label: t("col.count"), num: true }, { label: t("col.avgLife"), num: true }],
        rows: traps.map((tr, i) => ({
          i,
          cells: [tr.cp ? `<span class="cp-pill">CP${String(tr.cp.order).padStart(2, "0")}</span> ${esc(tr.cp.name)}` : "–", `${tr.x.toFixed(1)}, ${tr.y.toFixed(1)}`, tr.count, tr.avgLife.toFixed(1) + " " + t("unit.s")],
          raw: [tr.cp ? tr.cp.order : 999, tr.x, tr.count, tr.avgLife],
        })),
      },
    });
  }

  // ---- curve ----

  function renderCurve(root) {
    const sel = M.selected();
    const h = head(root, t("cv.title"), t("cv.sub"));
    const ctl = document.createElement("div");
    ctl.style.cssText = "display:flex;gap:14px;align-items:center;margin-left:auto";
    ctl.innerHTML = `<label class="tb-range">${t("cv.window")} <input type="range" min="1" max="20" value="${state.curveWindow}" id="cvWin"><span class="num" id="cvWinV">${state.curveWindow}%</span></label>
      <div class="seg"><button data-x="norm" aria-pressed="${state.curveX === "norm"}">${t("cv.xNorm")}</button><button data-x="time" aria-pressed="${state.curveX === "time"}">${t("cv.xTime")}</button></div>`;
    h.appendChild(ctl);
    const g = gridEl(root);
    const rate = new IK.charts.Card(g, { title: t("chart.rate"), sub: t("chart.rate.sub"), span: 12, size: "tall", group: "curve" });
    const cum = new IK.charts.Card(g, { title: t("chart.cumulative"), span: 12, size: "tall", group: "curve" });
    const draw = () => { const c = IK.charts.curveCharts(sel, state.curveWindow, state.curveX); rate.render(c.rate); cum.render(c.cum); };
    draw();
    ctl.querySelector("#cvWin").addEventListener("input", e => { state.curveWindow = +e.target.value; ctl.querySelector("#cvWinV").textContent = state.curveWindow + "%"; persist(); draw(); });
    ctl.querySelectorAll("[data-x]").forEach(b => b.addEventListener("click", () => {
      state.curveX = b.dataset.x; persist();
      ctl.querySelectorAll("[data-x]").forEach(o => o.setAttribute("aria-pressed", String(o === b)));
      draw();
    }));
  }

  // ---- perf ----

  function renderPerf(root) {
    const sel = M.selected();
    head(root, t("pf.title"), t("pf.sub"));
    const g = gridEl(root);
    new IK.charts.Card(g, { title: t("chart.fps"), span: 12, size: "short", group: "perf" }).render(IK.charts.perfLine(sel, "fps", t("axis.fps")));
    new IK.charts.Card(g, { title: t("chart.frame"), sub: t("chart.frame.sub"), span: 12, group: "perf" }).render(IK.charts.perfLine(sel, "frame", t("axis.ms"), { hitches: true, log: true }));
    new IK.charts.Card(g, { title: t("chart.gc"), span: 12, size: "short", group: "perf" }).render(IK.charts.perfLine(sel, "gc", t("axis.mb"), { zoom: true }));
    new IK.charts.Card(g, { title: t("chart.box"), sub: t("chart.box.sub"), span: 6 }).render(IK.charts.frameBox(sel));
    const hasDraw = sel.some(s => { for (let i = 0; i < s.samples.n; i += 50) if (s.samples.draw[i] >= 0) return true; return false; });
    new IK.charts.Card(g, { title: t("chart.render"), sub: t("chart.render.sub"), span: 6 })
      .render(hasDraw ? IK.charts.perfLine(sel, "draw", t("axis.batches")) : { empty: t("chart.render.none") });
  }

  // ---------- map chrome ----------

  // Art and Blend need the scene's rendered art; without it the map shows the schematic and the
  // two buttons say why instead of pretending
  function syncToolbar() {
    const art = map.art && !map.art.failed;
    const effective = art ? map.opts.baseMode : "schematic";
    document.querySelectorAll("#baseMode [data-mode]").forEach(b => {
      const needsArt = b.dataset.mode !== "schematic";
      b.disabled = needsArt && !art;
      b.title = b.disabled ? t("map.noArtHint") : "";
      b.setAttribute("aria-pressed", String(b.dataset.mode === effective));
    });
    $("#dimSlider").value = Math.round(map.opts.dim * 100);
    $("#dimWrap").classList.toggle("hidden", effective === "schematic");
  }

  function buildLayerPanel() {
    const p = $("#layerPanel");
    const T = IK.theme.current;
    const ramp = name => `<span class="ramp" style="background:${T.ramps[IK.HEAT_RAMP[name]].css}"></span>`;
    const icon = svg => `<span class="ramp" style="display:grid;place-items:center">${svg}</span>`;
    const row = (name, lead, desc) => `<div class="layer-row ${map.layers[name] ? "on" : ""}" data-layer="${name}">${lead}<span>${t("layer." + name)}</span><span class="switch"></span>${desc ? `<span class="desc">${t("layer." + name + ".d")}</span>` : ""}</div>`;
    const ico = d => icon(`<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">${d}</svg>`);
    p.innerHTML =
      `<h5>${t("map.group.heat")}</h5>` +
      row("deathHeat", ramp("deathHeat"), true) +
      row("lethality", ramp("lethality"), true) +
      row("presence", ramp("presence"), true) +
      row("perf", ramp("perf"), true) +
      row("mech", ramp("mech"), true) +
      `<div class="layer-sub"><select id="mechSel">${["wall", "ceiling", "rope", "air"].map(m => `<option value="${m}" ${map.opts.mech === m ? "selected" : ""}>${t("mech." + m)}</option>`).join("")}</select></div>` +
      row("contours", ico('<path d="M2 11c3-6 9-6 12 0M5 11c1.5-3 4.5-3 6 0"/>'), false) +
      row("coverage", icon(`<svg viewBox="0 0 16 16" width="16" height="16"><rect x="2" y="7" width="12" height="3" rx="1" fill="${T.coverage}"/></svg>`), true) +
      `<h5>${t("map.group.marks")}</h5>` +
      row("deaths", icon(IK.theme.shapeSvg("triangle", T.causeColor("Spike")).replace("<svg", '<svg width="14" height="14"')), false) +
      row("unstucks", icon(`<svg viewBox="0 0 16 16" width="14" height="14"><path d="M8 2 14 8 8 14 2 8z" fill="${T.status.warning}"/></svg>`), true) +
      row("paths", ico('<path d="M2 12c3-8 6 0 12-8"/>'), false) +
      `<div class="layer-sub"><select id="pathSel"><option value="session" ${map.opts.pathColor === "session" ? "selected" : ""}>${t("pathColor.session")}</option><option value="time" ${map.opts.pathColor === "time" ? "selected" : ""}>${t("pathColor.time")}</option></select></div>` +
      row("segments", ico('<path d="M3 12Q8 2 13 12"/><path d="m11 11 2 1 .5-2"/>'), false) +
      row("hazards", ico('<circle cx="8" cy="8" r="4"/><path d="M8 2v2M8 12v2M2 8h2M12 8h2"/>'), false) +
      `<h5>${t("map.group.level")}</h5>` +
      row("checkpoints", ico('<circle cx="8" cy="6" r="3"/><path d="M8 9v5"/>'), false) +
      row("doors", ico('<rect x="4" y="2" width="8" height="12" rx="1"/><path d="M10 8h.5"/>'), false) +
      row("props", ico('<path d="m8 2 1.8 3.9 4.2.4-3.2 2.8 1 4.1L8 11l-3.8 2.2 1-4.1L2 6.3l4.2-.4z"/>'), false);
    p.querySelectorAll("[data-layer]").forEach(r => r.addEventListener("click", () => {
      const name = r.dataset.layer, on = !map.layers[name];
      map.setLayer(name, on);
      r.classList.toggle("on", on);
    }));
    p.querySelector("#mechSel").addEventListener("change", e => { map.setOption("mech", e.target.value); if (!map.layers.mech) { map.setLayer("mech", true); buildLayerPanel(); } });
    p.querySelector("#pathSel").addEventListener("change", e => map.setOption("pathColor", e.target.value));
  }

  function updateReplayUi(r) {
    if (!map || !map.data) return;
    const max = map.data.maxTime || 1;
    $("#rpSlider").value = Math.round((r.t / max) * 1000);
    $("#rpTime").textContent = `${IK.fmtTime(r.t)} / ${IK.fmtTime(max)}`;
    $("#rpPlay").innerHTML = r.playing
      ? '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M7 5h4v14H7zM13 5h4v14h-4z"/></svg>'
      : '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>';
    if (!r.on) return;
    // per-session progress, refreshed a few times a second at most
    const now = performance.now();
    if (updateReplayUi.last && now - updateReplayUi.last < 150 && r.playing) return;
    updateReplayUi.last = now;
    $("#replayPanel").innerHTML = map.data.sessions.map(e => {
      const deaths = e.deaths.filter(d => d.ts <= r.t).length;
      const present = r.t <= e.tEnd;
      const color = IK.theme.sessionColor(e.session.slot);
      return `<div class="rp-row"><span class="sw" style="background:${color};margin:0"></span><span>${esc(e.session.name)}</span>
        <span class="num ${present ? "" : "muted"}">${present ? t("replay.deaths", { n: deaths }) : t("replay.absent")}</span>
        <div class="bar"><i style="width:${Math.min(100, (r.t / Math.max(1, e.tEnd)) * 100)}%;background:${color}"></i></div></div>`;
    }).join("");
  }

  function exportMap() {
    if (!map.data) return;
    const sel = M.selected();
    const dates = sel.map(s => s.meta.startedAt).filter(Boolean).sort();
    const range = dates.length ? `${dates[0].slice(0, 10)} – ${dates[dates.length - 1].slice(0, 10)}` : "";
    const title = t("export.title", { scene: state.scene });
    const sub = `${t("export.sessions", { n: map.data.sessions.length, range })}   ·   ${t("export.layers", { layers: map.activeLayerNames() })}`;
    const c = map.exportImage(title, sub);
    IK.charts.download(c.toDataURL("image/png"), `inkform_${state.scene.replace(/\s+/g, "_")}_${new Date().toISOString().slice(0, 10)}.png`);
  }

  // handle for the console (and for scripted checks): IK.app.map.focusHottest() etc.
  IK.app = { get map() { return map; }, state, showView, loadFiles };

  boot();
})();
