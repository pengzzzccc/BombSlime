/* Design tokens → canvas colors and ECharts themes. The CSS custom properties in viewer.css are the
   single source; this reads them back after every theme switch so the map, the charts and the page
   can never disagree about a color. */
(function () {
  "use strict";
  const IK = (window.IK = window.IK || {});

  // Death causes: fixed categorical slot + a shape, so identity never rests on color alone
  // (the first three slots validate all-pairs for scatter; Blast/Void are reserved causes)
  const CAUSES = {
    Spike:   { slot: 1, shape: "triangle" },
    Burned:  { slot: 2, shape: "diamond" },
    Crushed: { slot: 3, shape: "square" },
    Blast:   { slot: 4, shape: "star" },
    Void:    { slot: 5, shape: "circle" },
    Unknown: { slot: 0, shape: "ring" },
  };
  const CAUSE_ORDER = ["Spike", "Burned", "Crushed", "Blast", "Void", "Unknown"];

  const listeners = [];
  let current = null;

  function cssVar(style, name) { return style.getPropertyValue(name).trim(); }

  function hexToRgb(hex) {
    let h = hex.replace("#", "").trim();
    if (h.length === 3) h = h.split("").map(c => c + c).join("");
    const n = parseInt(h, 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
  }

  function rgba(hex, a) {
    const [r, g, b] = hexToRgb(hex);
    return `rgba(${r},${g},${b},${a})`;
  }

  function mix(a, b, t) { return [0, 1, 2].map(i => Math.round(a[i] + (b[i] - a[i]) * t)); }

  // 256-entry RGBA lookup for a single-hue ramp: color walks faint → mid → peak, alpha rises with
  // value so zero is fully transparent and the level stays readable underneath
  function buildRamp(stops, dark) {
    const c = stops.map(hexToRgb);
    const lut = new Uint8ClampedArray(256 * 4);
    for (let i = 0; i < 256; i++) {
      const t = i / 255;
      const rgb = t < 0.5 ? mix(c[0], c[1], t / 0.5) : mix(c[1], c[2], (t - 0.5) / 0.5);
      const a = i === 0 ? 0 : Math.min(1, Math.pow(t, dark ? 0.55 : 0.65) * 1.08) * (dark ? 0.95 : 0.85);
      lut[i * 4] = rgb[0]; lut[i * 4 + 1] = rgb[1]; lut[i * 4 + 2] = rgb[2]; lut[i * 4 + 3] = Math.round(a * 255);
    }
    return { lut, stops, css: `linear-gradient(90deg, ${rgba(stops[0], 0.15)}, ${stops[1]}, ${stops[2]})` };
  }

  function read() {
    const style = getComputedStyle(document.documentElement);
    // color-scheme reads back as the active scheme; the page's own luminance is the fallback
    let dark = style.getPropertyValue("color-scheme").trim() === "dark";
    if (!style.getPropertyValue("color-scheme").trim()) {
      const [r, g, b] = hexToRgb(cssVar(style, "--page") || "#ffffff");
      dark = 0.2126 * r + 0.7152 * g + 0.0722 * b < 128;
    }
    const v = n => cssVar(style, n);
    const series = [1, 2, 3, 4, 5, 6, 7, 8].map(i => v(`--s${i}`));
    const ramp = name => buildRamp(v(name).split(",").map(s => s.trim()), dark);

    const t = {
      dark,
      font: v("--font"),
      page: v("--page"), surface1: v("--surface-1"), surface2: v("--surface-2"), surface3: v("--surface-3"),
      ink1: v("--ink-1"), ink2: v("--ink-2"), ink3: v("--ink-3"),
      grid: v("--grid"), axis: v("--axis"), border: v("--border"), accent: v("--accent"),
      series, other: v("--other"),
      status: { good: v("--good"), warning: v("--warning"), serious: v("--serious"), critical: v("--critical") },
      ramps: {
        death: ramp("--heat-death"), presence: ramp("--heat-presence"), lethal: ramp("--heat-lethal"),
        perf: ramp("--heat-perf"), mech: ramp("--heat-mech"),
      },
      coverage: v("--coverage"),
      map: {
        bg: v("--map-bg"), bgtile: v("--map-bgtile"), decor: v("--map-decor"),
        terrain: v("--map-terrain"), terrainEdge: v("--map-terrain-edge"),
        hazard: v("--map-hazard"), hazardEdge: v("--map-hazard-edge"), platform: v("--map-platform"),
        artDim: v("--map-art-dim"), blend: v("--map-blend") || "source-over",
      },
    };
    t.causeColor = cause => {
      const c = CAUSES[cause] || CAUSES.Unknown;
      return c.slot ? series[c.slot - 1] : t.other;
    };
    t.causeShape = cause => (CAUSES[cause] || CAUSES.Unknown).shape;
    t.rgba = rgba;
    return t;
  }

  // Session colors follow the session (its load slot), never its rank in a filtered list
  function sessionColor(slot) {
    const t = current || read();
    return slot < 8 ? t.series[slot] : t.other;
  }

  function echartsTheme(t) {
    const axis = {
      axisLine: { show: true, lineStyle: { color: t.axis, width: 1 } },
      axisTick: { show: false },
      axisLabel: { color: t.ink3, fontSize: 11, fontFamily: t.font },
      splitLine: { show: true, lineStyle: { color: t.grid, width: 1, type: "solid" } },
      nameTextStyle: { color: t.ink3, fontSize: 11 },
    };
    return {
      color: t.series,
      backgroundColor: "transparent",
      textStyle: { fontFamily: t.font, color: t.ink2 },
      title: { textStyle: { color: t.ink1 } },
      legend: { textStyle: { color: t.ink2, fontSize: 12 }, icon: "roundRect", itemWidth: 12, itemHeight: 8, itemGap: 14, pageTextStyle: { color: t.ink3 } },
      tooltip: {
        backgroundColor: t.surface2, borderColor: t.border, borderWidth: 1, padding: [8, 12],
        textStyle: { color: t.ink1, fontSize: 12, fontFamily: t.font },
        extraCssText: "border-radius:10px;box-shadow:0 8px 24px rgba(0,0,0,.25);",
        axisPointer: { lineStyle: { color: t.ink3, width: 1 }, crossStyle: { color: t.ink3 }, label: { backgroundColor: t.surface3, color: t.ink1 } },
      },
      categoryAxis: { ...axis, splitLine: { show: false } },
      valueAxis: { ...axis, axisLine: { show: false } },
      timeAxis: axis,
      logAxis: axis,
      line: { symbol: "none", lineStyle: { width: 2 }, emphasis: { lineStyle: { width: 2.5 } } },
      bar: { itemStyle: { borderRadius: [4, 4, 0, 0] }, barMaxWidth: 28 },
      scatter: { symbolSize: 9, itemStyle: { borderColor: t.surface1, borderWidth: 2 } },
      dataZoom: {
        backgroundColor: "transparent", borderColor: t.border, fillerColor: rgba(t.accent, 0.12),
        handleStyle: { color: t.surface2, borderColor: t.ink3 }, moveHandleStyle: { color: t.ink3 },
        textStyle: { color: t.ink3 }, dataBackground: { lineStyle: { color: t.ink3, opacity: .5 }, areaStyle: { color: t.grid } },
        selectedDataBackground: { lineStyle: { color: t.accent }, areaStyle: { color: rgba(t.accent, 0.2) } },
      },
    };
  }

  function refresh() {
    current = read();
    if (window.echarts) window.echarts.registerTheme(current.dark ? "ikDark" : "ikLight", echartsTheme(current));
    listeners.forEach(fn => fn(current));
    return current;
  }

  function setTheme(mode) {
    document.documentElement.setAttribute("data-theme", mode);
    try { localStorage.setItem("ik.theme", mode); } catch (e) { /* not persisted */ }
    refresh();
  }

  // Shape paths shared by the map, the legend and the tooltips (unit size: fits a 1×1 box at 0,0)
  function shapePath(ctx, shape, x, y, r) {
    ctx.beginPath();
    switch (shape) {
      case "triangle":
        ctx.moveTo(x, y - r * 1.1); ctx.lineTo(x + r, y + r * 0.75); ctx.lineTo(x - r, y + r * 0.75); ctx.closePath(); break;
      case "diamond":
        ctx.moveTo(x, y - r * 1.15); ctx.lineTo(x + r * 0.9, y); ctx.lineTo(x, y + r * 1.15); ctx.lineTo(x - r * 0.9, y); ctx.closePath(); break;
      case "square":
        ctx.rect(x - r * 0.82, y - r * 0.82, r * 1.64, r * 1.64); break;
      case "star": {
        for (let i = 0; i < 10; i++) {
          const a = -Math.PI / 2 + i * Math.PI / 5, rr = i % 2 ? r * 0.48 : r * 1.12;
          i ? ctx.lineTo(x + Math.cos(a) * rr, y + Math.sin(a) * rr) : ctx.moveTo(x + Math.cos(a) * rr, y + Math.sin(a) * rr);
        }
        ctx.closePath(); break;
      }
      default:
        ctx.arc(x, y, r * 0.9, 0, Math.PI * 2);
    }
  }

  function shapeSvg(shape, color, hollow) {
    const fill = hollow ? "none" : color, stroke = color;
    const sw = hollow ? 2 : 0;
    const p = {
      triangle: `<path d="M7 1.6 13 11.6H1z" fill="${fill}" stroke="${stroke}" stroke-width="${sw}"/>`,
      diamond: `<path d="M7 .8 13 7 7 13.2 1 7z" fill="${fill}" stroke="${stroke}" stroke-width="${sw}"/>`,
      square: `<rect x="2" y="2" width="10" height="10" rx="1" fill="${fill}" stroke="${stroke}" stroke-width="${sw}"/>`,
      star: `<path d="M7 .8 8.8 5 13.3 5.3 9.8 8.2 11 12.7 7 10.2 3 12.7 4.2 8.2.7 5.3 5.2 5z" fill="${fill}" stroke="${stroke}" stroke-width="${sw}"/>`,
      circle: `<circle cx="7" cy="7" r="5.5" fill="${fill}" stroke="${stroke}" stroke-width="${sw}"/>`,
      ring: `<circle cx="7" cy="7" r="4.6" fill="none" stroke="${stroke}" stroke-width="2"/>`,
    };
    return `<svg viewBox="0 0 14 14" aria-hidden="true">${p[shape] || p.circle}</svg>`;
  }

  // Difficulty rating on mean deaths per attempt: status colors, always shown with icon + label
  const RATINGS = [
    { key: "easy", max: 0.5, status: "good", icon: "M3 7.5 6 10.5 11 4.5" },
    { key: "moderate", max: 1.5, status: "warning", icon: "M7 3.5v4M7 10.2v.3" },
    { key: "hard", max: 3, status: "serious", icon: "M7 3v5M7 10.2v.3M3 12h8" },
    { key: "brutal", max: Infinity, status: "critical", icon: "M3.5 3.5l7 7M10.5 3.5l-7 7" },
  ];
  function rating(meanDeaths) {
    return RATINGS.find(r => meanDeaths < r.max) || RATINGS[RATINGS.length - 1];
  }
  function ratingHtml(meanDeaths) {
    const r = rating(meanDeaths);
    const c = (current || read()).status[r.status];
    return `<span class="rating" style="color:${c}"><svg viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="7" cy="7" r="6.2" stroke-width="1.4"/><path d="${r.icon}" transform="translate(0 0)"/></svg>${IK.t("rating." + r.key)}</span>`;
  }

  IK.theme = {
    CAUSES, CAUSE_ORDER,
    get current() { return current || refresh(); },
    refresh, setTheme, onChange(fn) { listeners.push(fn); },
    sessionColor, rgba, hexToRgb, shapePath, shapeSvg, rating, ratingHtml,
    get echartsName() { return (current || refresh()).dark ? "ikDark" : "ikLight"; },
  };
})();
