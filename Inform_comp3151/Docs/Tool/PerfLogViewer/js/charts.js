/* Chart cards + chart builders. A card owns one ECharts instance, a "table" twin of the same data
   (every chart is readable without hover or color), and a PNG export. Builders are pure: data in,
   { option, table } out — so a theme or language switch just rebuilds. */
(function () {
  "use strict";
  const IK = (window.IK = window.IK || {});
  const t = (k, p) => IK.t(k, p);
  const esc = s => String(s == null ? "" : s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const live = new Set();

  class Card {
    constructor(parent, o) {
      this.o = o;
      const el = document.createElement("div");
      el.className = `card span-${o.span || 12}`;
      el.innerHTML = `
        <div class="card-head">
          <h4>${esc(o.title)}${o.sub ? `<small>${esc(o.sub)}</small>` : ""}</h4>
          <div class="card-tools">
            ${o.noChart ? "" : `<button class="tool" data-act="table" aria-pressed="false">${t("action.table")}</button>`}
            ${o.noChart ? "" : `<button class="tool" data-act="png">${t("action.png")}</button>`}
            ${o.csv ? `<button class="tool" data-act="csv">${t("action.exportCsv")}</button>` : ""}
          </div>
        </div>
        ${o.noChart ? "" : `<div class="chart ${o.size || ""}"></div>`}
        <div class="table-wrap ${o.noChart ? "" : "hidden"}"></div>`;
      parent.appendChild(el);
      this.el = el;
      this.chartEl = el.querySelector(".chart");
      this.tableEl = el.querySelector(".table-wrap");
      el.querySelector('[data-act="table"]')?.addEventListener("click", e => this.toggleTable(e.currentTarget));
      el.querySelector('[data-act="png"]')?.addEventListener("click", () => this.png());
      el.querySelector('[data-act="csv"]')?.addEventListener("click", () => this.csv());
      live.add(this);
    }

    render(spec) {
      this.spec = spec;
      if (spec.empty) {
        if (this.chartEl) this.chartEl.innerHTML = `<div class="empty-note">${esc(spec.empty)}</div>`;
        this.tableEl.innerHTML = "";
        return this;
      }
      if (this.chartEl && spec.option) {
        if (!this.chart) {
          this.chart = echarts.init(this.chartEl, IK.theme.echartsName, { renderer: "canvas" });
          this.ro = new ResizeObserver(() => this.chart && this.chart.resize());
          this.ro.observe(this.chartEl);
          if (this.o.group) { this.chart.group = this.o.group; echarts.connect(this.o.group); }
          if (this.o.onClick) this.chart.on("click", this.o.onClick);
        }
        this.chart.setOption(spec.option, true);
      }
      if (spec.table) this.tableEl.innerHTML = tableHtml(spec.table);
      if (spec.table && spec.table.onRow) {
        this.tableEl.querySelectorAll("tr[data-i]").forEach(tr => tr.addEventListener("click", () => spec.table.onRow(+tr.dataset.i)));
      }
      bindSort(this.tableEl, spec.table, () => this.render(spec));
      return this;
    }

    toggleTable(btn) {
      const on = btn.getAttribute("aria-pressed") !== "true";
      btn.setAttribute("aria-pressed", String(on));
      btn.textContent = on ? t("action.chart") : t("action.table");
      this.chartEl.classList.toggle("hidden", on);
      this.tableEl.classList.toggle("hidden", !on);
    }

    png() {
      if (!this.chart) return;
      const url = this.chart.getDataURL({ pixelRatio: 2, backgroundColor: IK.theme.current.surface1 });
      download(url, `inkform_${slug(this.o.title)}.png`);
    }

    csv() {
      const tb = this.spec && this.spec.table;
      if (!tb) return;
      const lines = [tb.columns.map(c => csvCell(c.label)).join(",")];
      tb.rows.forEach(r => lines.push(tb.columns.map((c, i) => csvCell(r.raw ? r.raw[i] : stripHtml(r.cells[i]))).join(",")));
      const blob = new Blob(["﻿" + lines.join("\n")], { type: "text/csv;charset=utf-8" });
      download(URL.createObjectURL(blob), `inkform_${slug(this.o.title)}.csv`);
    }

    dispose() {
      if (this.ro) this.ro.disconnect();
      if (this.chart) this.chart.dispose();
      this.chart = null;
      live.delete(this);
    }
  }

  function disposeIn(root) {
    for (const c of [...live]) if (!root || root.contains(c.el)) c.dispose();
  }

  function slug(s) { return String(s).replace(/[^\w一-龥]+/g, "_").slice(0, 40); }
  function stripHtml(s) { const d = document.createElement("div"); d.innerHTML = s; return d.textContent; }
  function csvCell(v) { v = v == null ? "" : String(v); return /[",\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v; }
  function download(url, name) { const a = document.createElement("a"); a.href = url; a.download = name; document.body.appendChild(a); a.click(); a.remove(); }

  // table spec: {columns:[{label, num, sort}], rows:[{cells:[html], raw:[value], i}], sortBy, asc, onRow}
  function tableHtml(tb) {
    const rows = tb.rows.slice();
    if (tb.sortBy != null) {
      const k = tb.sortBy, dir = tb.asc ? 1 : -1;
      rows.sort((a, b) => {
        const x = a.raw ? a.raw[k] : a.cells[k], y = b.raw ? b.raw[k] : b.cells[k];
        if (typeof x === "number" && typeof y === "number") return (x - y) * dir;
        if (x == null) return 1;
        if (y == null) return -1;
        return String(x).localeCompare(String(y), undefined, { numeric: true }) * dir;
      });
    }
    const head = tb.columns.map((c, i) => `<th data-k="${i}" class="${c.num ? "n" : ""} ${tb.sortBy === i ? "sorted" + (tb.asc ? " asc" : "") : ""}">${esc(c.label)}</th>`).join("");
    const body = rows.map(r => `<tr ${tb.onRow ? `class="clickable" data-i="${r.i}"` : ""}>${r.cells.map((c, i) => `<td class="${tb.columns[i].num ? "n" : ""}">${c}</td>`).join("")}</tr>`).join("");
    return `<table class="data"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
  }

  function bindSort(el, tb, rerender) {
    if (!tb) return;
    el.querySelectorAll("th[data-k]").forEach(th => th.addEventListener("click", () => {
      const k = +th.dataset.k;
      tb.asc = tb.sortBy === k ? !tb.asc : false;
      tb.sortBy = k;
      rerender();
    }));
  }

  // ---------- shared option pieces ----------

  const theme = () => IK.theme.current;
  const grid = (o) => ({ left: 8, right: 16, top: 16, bottom: 8, containLabel: true, ...o });
  const axisTip = () => ({ trigger: "axis", axisPointer: { type: "line" } });
  const itemTip = () => ({ trigger: "item" });
  const fmt1 = v => (v == null || !isFinite(v) ? "–" : (Math.round(v * 10) / 10).toString());
  const sessColor = s => IK.theme.sessionColor(s.slot);

  function bar(color, radiusRight) {
    return { color, borderRadius: radiusRight ? [0, 4, 4, 0] : [4, 4, 0, 0] };
  }

  // ---------- overview ----------

  function sceneBars(scenes, metric) {
    if (!scenes.length) return { empty: t("toast.noData") };
    const T = theme();
    const vals = scenes.map(s => (metric === "time" ? s.time / 60 : s.deaths));
    return {
      option: {
        grid: grid({ top: 24 }),
        tooltip: { ...itemTip(), valueFormatter: v => fmt1(v) },
        xAxis: { type: "category", data: scenes.map(s => s.name) },
        yAxis: { type: "value", name: metric === "time" ? t("axis.minutes") : t("axis.deaths") },
        series: [{
          type: "bar", data: vals, itemStyle: bar(T.series[0]), barMaxWidth: 30,
          label: { show: true, position: "top", color: T.ink2, fontSize: 11, formatter: p => fmt1(p.value) },
        }],
      },
      table: {
        columns: [{ label: t("col.scene") }, { label: metric === "time" ? t("axis.minutes") : t("axis.deaths"), num: true }],
        rows: scenes.map((s, i) => ({ cells: [esc(s.name), fmt1(vals[i])], raw: [s.name, vals[i]] })),
      },
    };
  }

  function sparkline(el, values, color) {
    const c = echarts.init(el, IK.theme.echartsName);
    c.setOption({
      animation: false,
      grid: { left: 0, right: 0, top: 2, bottom: 2 },
      xAxis: { type: "category", show: false, data: values.map((_, i) => i), boundaryGap: false },
      yAxis: { type: "value", show: false, min: 0 },
      series: [{ type: "line", data: values, lineStyle: { width: 1.5, color }, areaStyle: { color: IK.theme.rgba(color, 0.15) }, symbol: "none", smooth: 0.3 }],
    });
    new ResizeObserver(() => c.resize()).observe(el);
    return c;
  }

  // ---------- segments ----------

  function segMeanBars(data) {
    const segs = data.segments.filter(s => s.passes);
    if (!segs.length) return { empty: t("toast.noData") };
    const T = theme();
    const labels = segs.map(s => IK.model.segLabel(data, s));
    return {
      option: {
        grid: grid({ left: 12 }),
        tooltip: { ...axisTip(), axisPointer: { type: "shadow" }, valueFormatter: v => v.toFixed(2) },
        xAxis: { type: "value", name: t("axis.meanDeaths") },
        yAxis: { type: "category", data: labels, inverse: true, axisLabel: { fontSize: 11, interval: 0 } },
        series: [{ type: "bar", data: segs.map(s => +s.mean.toFixed(3)), itemStyle: bar(T.series[0], true), barMaxWidth: 14, barCategoryGap: "35%" }],
      },
    };
  }

  function segScatter(data) {
    const segs = data.segments.filter(s => s.passes && s.medianTime != null);
    if (!segs.length) return { empty: t("toast.noData") };
    const T = theme();
    const top = segs.slice().sort((a, b) => b.mean - a.mean).slice(0, 3);
    return {
      option: {
        grid: grid({ top: 28, right: 28 }),
        tooltip: { ...itemTip(), formatter: p => { const s = segs[p.dataIndex]; return `<b>${esc(IK.model.segLabel(data, s))}</b><br>${t("tt.meanDeaths")}: ${s.mean.toFixed(2)}<br>${t("tt.medianTime")}: ${IK.fmtTime(s.medianTime)}<br>${t("tt.attempts")}: ${s.passes}`; } },
        xAxis: { type: "value", name: t("axis.medianTime"), nameLocation: "middle", nameGap: 26 },
        yAxis: { type: "value", name: t("axis.meanDeaths") },
        series: [{
          type: "scatter",
          data: segs.map(s => [s.medianTime, +s.mean.toFixed(3), s.passes]),
          symbolSize: v => Math.max(9, Math.min(26, 6 + Math.sqrt(v[2]) * 4)),
          itemStyle: { color: IK.theme.rgba(T.series[0], 0.85), borderColor: T.surface1, borderWidth: 2 },
          label: { show: true, position: "top", distance: 6, color: T.ink2, fontSize: 11, formatter: p => (top.includes(segs[p.dataIndex]) ? IK.model.segLabel(data, segs[p.dataIndex]) : "") },
          labelLayout: { hideOverlap: true },
        }],
      },
    };
  }

  function segMatrix(data) {
    const segs = data.segments.filter(s => s.passes);
    const sess = data.sessions.map(e => e.session);
    if (!segs.length || !sess.length) return { empty: t("toast.noData") };
    const T = theme();
    const cells = [];
    let max = 1;
    segs.forEach((s, y) => sess.forEach((ss, x) => {
      const inst = s.instances.filter(g => g.session === ss);
      if (!inst.length) return;
      const v = inst.reduce((p, g) => p + (g.deaths || 0), 0);
      max = Math.max(max, v);
      cells.push([x, y, v]);
    }));
    const ramp = T.ramps.death.stops;
    return {
      option: {
        grid: grid({ top: 8, bottom: 40 }),
        tooltip: { ...itemTip(), formatter: p => `${esc(sess[p.value[0]].name)}<br>${esc(IK.model.segLabel(data, segs[p.value[1]]))}<br><b>☠ ${p.value[2]}</b>` },
        xAxis: { type: "category", data: sess.map(s => s.name), splitArea: { show: false }, axisLabel: { rotate: sess.length > 6 ? 35 : 0, fontSize: 11 } },
        yAxis: { type: "category", data: segs.map(s => IK.model.segLabel(data, s)), inverse: true, axisLabel: { fontSize: 11 } },
        visualMap: {
          min: 0, max, calculable: false, orient: "horizontal", left: "center", bottom: 0, itemHeight: 120, itemWidth: 10,
          inRange: { color: T.dark ? [T.surface3, ramp[1], ramp[2]] : [ramp[0], ramp[1], ramp[2]] }, textStyle: { color: T.ink3 },
        },
        series: [{
          type: "heatmap", data: cells,
          label: { show: segs.length * sess.length <= 240, color: T.ink1, fontSize: 10 },
          itemStyle: { borderColor: T.surface1, borderWidth: 2, borderRadius: 3 },
        }],
      },
    };
  }

  // ---------- deaths ----------

  function causeBars(data) {
    const counts = {};
    data.deaths.forEach(d => (counts[d.inferred ? "Unknown" : d.cause] = (counts[d.inferred ? "Unknown" : d.cause] || 0) + 1));
    const causes = IK.theme.CAUSE_ORDER.filter(c => counts[c]);
    if (!causes.length) return { empty: t("toast.noData") };
    const T = theme();
    return {
      option: {
        grid: grid({ left: 12 }),
        tooltip: { ...itemTip() },
        xAxis: { type: "value", name: t("axis.count") },
        yAxis: { type: "category", data: causes.map(c => t("cause." + c)), inverse: true },
        series: [{
          type: "bar", barMaxWidth: 18,
          data: causes.map(c => ({ value: counts[c], itemStyle: bar(T.causeColor(c), true) })),
          label: { show: true, position: "right", color: T.ink2, fontSize: 11 },
        }],
      },
      table: {
        columns: [{ label: t("tt.cause") }, { label: t("col.count"), num: true }],
        rows: causes.map(c => ({ cells: [esc(t("cause." + c)), counts[c]], raw: [c, counts[c]] })),
      },
    };
  }

  function hazardBars(data) {
    const list = data.hazards.slice(0, 10);
    if (!list.length) return { empty: data.anyEvents ? t("toast.noData") : t("noEvents") };
    const T = theme();
    return {
      option: {
        grid: grid({ left: 12 }),
        tooltip: { ...itemTip(), formatter: p => { const h = list[p.dataIndex]; return `<b>${esc(h.name)}</b>${h.prefab ? " · " + esc(h.prefab) : ""}<br>${t("tt.kills")}: ${h.kills}`; } },
        xAxis: { type: "value", name: t("tt.kills"), minInterval: 1 },
        yAxis: { type: "category", data: list.map(h => h.name), inverse: true, axisLabel: { fontSize: 11 } },
        series: [{ type: "bar", data: list.map(h => h.kills), itemStyle: bar(T.series[0], true), barMaxWidth: 16, label: { show: true, position: "right", color: T.ink2, fontSize: 11 } }],
      },
      table: {
        columns: [{ label: t("tt.source") }, { label: t("tt.prefab") }, { label: t("tt.cause") }, { label: t("tt.kills"), num: true }],
        rows: list.map(h => ({ cells: [esc(h.name), esc(h.prefab || ""), esc(t("cause." + h.cause)), h.kills], raw: [h.name, h.prefab, h.cause, h.kills] })),
      },
    };
  }

  function stateBars(data) {
    const counts = {};
    data.deaths.forEach(d => d.state && (counts[d.state] = (counts[d.state] || 0) + 1));
    const states = Object.keys(counts).sort((a, b) => counts[b] - counts[a]);
    if (!states.length) return { empty: t("toast.noData") };
    const T = theme();
    return {
      option: {
        grid: grid({ left: 12 }),
        tooltip: itemTip(),
        xAxis: { type: "value", name: t("axis.count"), minInterval: 1 },
        yAxis: { type: "category", data: states, inverse: true },
        series: [{ type: "bar", data: states.map(s => counts[s]), itemStyle: bar(T.series[0], true), barMaxWidth: 16, label: { show: true, position: "right", color: T.ink2, fontSize: 11 } }],
      },
      table: { columns: [{ label: t("tt.state") }, { label: t("col.count"), num: true }], rows: states.map(s => ({ cells: [esc(s), counts[s]], raw: [s, counts[s]] })) },
    };
  }

  const LIFE_BINS = [[0, 1], [1, 2], [2, 3], [3, 5], [5, 10], [10, 20], [20, 40], [40, 80], [80, Infinity]];
  function lifeHistogram(data) {
    const lives = data.deaths.map(d => d.life).filter(v => v != null);
    if (!lives.length) return { empty: data.anyEvents ? t("toast.noData") : t("noEvents") };
    const T = theme();
    const counts = LIFE_BINS.map(([a, b]) => lives.filter(v => v >= a && v < b).length);
    const labels = LIFE_BINS.map(([a, b]) => (b === Infinity ? `${a}+` : `${a}–${b}`));
    return {
      option: {
        grid: grid({ top: 28 }),
        tooltip: { ...axisTip(), axisPointer: { type: "shadow" } },
        xAxis: { type: "category", data: labels, name: t("axis.seconds"), nameLocation: "end" },
        yAxis: { type: "value", name: t("axis.count"), minInterval: 1 },
        series: [{
          type: "bar", barCategoryGap: "30%",
          data: counts.map((c, i) => ({ value: c, itemStyle: bar(LIFE_BINS[i][1] <= IK.model.TRAP_LIFE ? T.status.critical : T.series[0]) })),
          label: { show: true, position: "top", color: T.ink2, fontSize: 11, formatter: p => (p.value ? p.value : "") },
          markArea: {
            silent: true, itemStyle: { color: IK.theme.rgba(T.status.critical, 0.06) },
            label: { show: true, position: "insideTop", color: T.status.critical, fontSize: 11, formatter: "⚠ " + (IK.i18n.lang === "zh" ? "重生陷阱" : "respawn trap") },
            data: [[{ xAxis: labels[0] }, { xAxis: labels[2] }]],
          },
        }],
      },
      table: { columns: [{ label: t("axis.seconds") }, { label: t("col.count"), num: true }], rows: labels.map((l, i) => ({ cells: [l, counts[i]], raw: [i, counts[i]] })) },
    };
  }

  // ---------- difficulty curve ----------

  function rollingRates(pct, span, windowPct) {
    const out = [];
    const minutes = (windowPct / 100) * span / 60;
    let lo = 0;
    for (let x = 0; x <= 100.0001; x += 0.5) {
      while (lo < pct.length && pct[lo] < x - windowPct) lo++;
      let hi = lo, n = 0;
      while (hi < pct.length && pct[hi] <= x) { n++; hi++; }
      out.push([+x.toFixed(2), minutes > 0 ? +(n / minutes).toFixed(3) : 0]);
    }
    return out;
  }

  function curveCharts(sessions, windowPct, xMode) {
    const rows = sessions.map(s => ({ s, c: IK.model.curveData(s) })).filter(r => r.c);
    if (!rows.length) return { rate: { empty: t("toast.noData") }, cum: { empty: t("toast.noData") } };
    const few = rows.length <= 4;
    const xName = xMode === "time" ? t("axis.minutes") : t("axis.progress");
    const toX = (r, pct) => (xMode === "time" ? +((pct / 100) * r.c.span / 60).toFixed(2) : pct);
    const legend = { top: 0, type: "scroll" };
    const base = { grid: grid({ top: 36, right: few ? 96 : 16 }), tooltip: { ...axisTip(), valueFormatter: v => fmt1(v) }, legend };
    const rate = {
      ...base,
      xAxis: { type: "value", name: xName, nameLocation: "middle", nameGap: 26, min: 0, max: xMode === "time" ? undefined : 100 },
      yAxis: { type: "value", name: t("axis.rate") },
      series: rows.map(r => ({
        type: "line", name: r.s.name, showSymbol: false, smooth: 0.25,
        data: rollingRates(r.c.pct, r.c.span, windowPct).map(([x, y]) => [toX(r, x), y]),
        lineStyle: { width: 2, color: sessColor(r.s) }, itemStyle: { color: sessColor(r.s) },
        endLabel: { show: few, formatter: p => p.seriesName, color: sessColor(r.s), fontSize: 11 },
        emphasis: { focus: "series" },
      })),
    };
    const cum = {
      ...base,
      xAxis: { type: "value", name: xName, nameLocation: "middle", nameGap: 26, min: 0, max: xMode === "time" ? undefined : 100 },
      yAxis: { type: "value", name: t("axis.deaths"), minInterval: 1 },
      series: rows.map(r => {
        const pts = [[0, 0]];
        r.c.pct.forEach((p, i) => { pts.push([toX(r, p), i]); pts.push([toX(r, p), i + 1]); });
        pts.push([toX(r, 100), r.c.pct.length]);
        return {
          type: "line", name: r.s.name, showSymbol: false, data: pts,
          lineStyle: { width: 2, color: sessColor(r.s) }, itemStyle: { color: sessColor(r.s) },
          endLabel: { show: few, formatter: p => `${r.s.name}  ${r.c.pct.length}`, color: sessColor(r.s), fontSize: 11 },
          emphasis: { focus: "series" },
        };
      }),
    };
    const table = {
      columns: [{ label: t("col.session") }, { label: t("col.duration"), num: true }, { label: t("col.deaths"), num: true }, { label: t("col.rate"), num: true }],
      rows: rows.map(r => ({ cells: [esc(r.s.name), (r.c.span / 60).toFixed(1), r.c.pct.length, (r.c.pct.length / (r.c.span / 60)).toFixed(2)], raw: [r.s.name, r.c.span, r.c.pct.length, r.c.pct.length / (r.c.span / 60)] })),
    };
    return { rate: { option: rate, table }, cum: { option: cum, table } };
  }

  // ---------- performance ----------

  function perfSeries(sessions, field, scenesOnly) {
    return sessions.map(s => {
      const smp = s.samples, pts = [];
      for (let i = 0; i < smp.n; i++) {
        if (scenesOnly && !IK.parse.isGameplayScene(smp.sceneNames[smp.scene[i]])) continue;
        const v = smp[field][i];
        if (isFinite(v)) pts.push([+(smp.t[i] / 60).toFixed(3), v]);
      }
      return { s, pts };
    });
  }

  function perfLine(sessions, field, yName, opts) {
    const rows = perfSeries(sessions, field, false).filter(r => r.pts.length);
    if (!rows.length) return { empty: t("toast.noData") };
    const T = theme();
    const series = rows.map(r => ({
      type: "line", name: r.s.name, showSymbol: false, sampling: "lttb", data: r.pts,
      lineStyle: { width: 1.5, color: sessColor(r.s) }, itemStyle: { color: sessColor(r.s) }, emphasis: { focus: "series" },
    }));
    if (opts && opts.hitches) {
      rows.forEach(r => {
        const smp = r.s.samples, pts = [];
        for (let i = 0; i < smp.n; i++) if (smp.hitches[i] > 0 && isFinite(smp.maxFrame[i])) pts.push([+(smp.t[i] / 60).toFixed(3), Math.min(smp.maxFrame[i], 400)]);
        if (pts.length) series.push({
          type: "scatter", name: r.s.name, data: pts, symbolSize: 7, z: 5,
          itemStyle: { color: sessColor(r.s), borderColor: T.surface1, borderWidth: 1.5 }, tooltip: { valueFormatter: v => fmt1(v) + " ms" },
        });
      });
    }
    return {
      option: {
        grid: grid({ top: 30, bottom: opts && opts.zoom ? 46 : 8 }),
        legend: { top: 0, type: "scroll", data: rows.map(r => r.s.name) },
        tooltip: { ...axisTip(), valueFormatter: v => fmt1(v) },
        xAxis: { type: "value", name: t("axis.minutes"), min: 0, axisLabel: { formatter: v => v } },
        // frame time spans 1 ms windows to 400 ms hitches: a log axis keeps both readable
        yAxis: opts && opts.log ? { type: "log", logBase: 10, name: yName, min: 0.5 } : { type: "value", name: yName, scale: true },
        dataZoom: opts && opts.zoom ? [{ type: "inside", xAxisIndex: 0 }, { type: "slider", xAxisIndex: 0, height: 18, bottom: 6 }] : [{ type: "inside", xAxisIndex: 0 }],
        series,
      },
    };
  }

  function quartiles(a) {
    const s = a.slice().sort((x, y) => x - y);
    const q = p => { const i = (s.length - 1) * p, lo = Math.floor(i), hi = Math.ceil(i); return s[lo] + (s[hi] - s[lo]) * (i - lo); };
    return [s[0], q(0.25), q(0.5), q(0.75), q(0.98)];
  }

  function frameBox(sessions) {
    const byScene = new Map();
    for (const s of sessions) {
      const smp = s.samples;
      for (let i = 0; i < smp.n; i++) {
        const name = smp.sceneNames[smp.scene[i]];
        if (!IK.parse.isGameplayScene(name) || !isFinite(smp.frame[i])) continue;
        if (!byScene.has(name)) byScene.set(name, []);
        byScene.get(name).push(smp.frame[i]);
      }
    }
    const names = [...byScene.keys()].sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
    if (!names.length) return { empty: t("toast.noData") };
    const T = theme();
    const q = names.map(n => quartiles(byScene.get(n)));
    return {
      option: {
        grid: grid({ top: 24 }),
        tooltip: { trigger: "item", formatter: p => `<b>${esc(p.name)}</b><br>p2…p98 · median<br>${p.value.slice(1).map(v => fmt1(v)).join(" · ")} ms` },
        xAxis: { type: "category", data: names },
        yAxis: { type: "value", name: t("axis.ms"), scale: true },
        series: [{ type: "boxplot", data: q, itemStyle: { color: IK.theme.rgba(T.series[0], 0.25), borderColor: T.series[0], borderWidth: 1.5 }, boxWidth: [12, 40] }],
      },
      table: {
        columns: [{ label: t("col.scene") }, { label: "p25", num: true }, { label: "p50", num: true }, { label: "p75", num: true }, { label: "p98", num: true }],
        rows: names.map((n, i) => ({ cells: [esc(n), fmt1(q[i][1]), fmt1(q[i][2]), fmt1(q[i][3]), fmt1(q[i][4])], raw: [n, q[i][1], q[i][2], q[i][3], q[i][4]] })),
      },
    };
  }

  IK.charts = { Card, disposeIn, tableHtml, sparkline, sceneBars, segMeanBars, segScatter, segMatrix, causeBars, hazardBars, stateBars, lifeHistogram, curveCharts, perfLine, frameBox, esc, download };
})();
