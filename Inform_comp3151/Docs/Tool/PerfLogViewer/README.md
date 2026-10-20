# PerfLog Viewer

一个静态网页工具：把 `PerformanceRecorder` 写出的 `perf_*.csv` 拖进去，就能在真实关卡上看死亡、动线、检查点分段难度和性能。不需要安装或构建，也不联网，数据不会离开本机。

## 使用

1. 在游戏的 **Video › Perf Recording** 打开录制。**Perf Rate** 建议 0.5 s 或 0.1 s，动线会更细。日志写到 `Docs/PerfLogs/`。
2. 在 Unity 中执行 **Tools › Inkform › Analytics › Export Level Layouts + Art**。
   - 会生成 `data/level_layouts.js`，以及每个关卡一张实景 `*.jpg`。
   - 只需要几何时可以用不带 `+ Art` 的版本，速度更快。
   - 实景分辨率在 **Art Resolution** 子菜单里调，默认 12 px/格。
   - 上面两个菜单只导出 World 里登记的房间。不在 World 里的场景（比如 `Lab_base`）先在 Unity 里打开，再执行 **Export Current Scene + Art**。
     - 它只渲染当前场景的实景，其他场景沿用之前导出的图。
     - 场景路径会记进 `data/extra_scenes.txt`，以后执行任何导出都会带上它。
     - 不想再导出时，删掉对应那一行，以及它的 `*.jpg` 和 `*.art.json`。
3. 双击 `index.html`，或执行 Unity 菜单 **Open PerfLog Viewer**，然后把一个或多个 CSV 拖进页面。

## 视图

| 标签 | 内容 |
|---|---|
| 关卡地图 | 实景、示意、混合三种底图，叠加以下图层：死亡热力、致死率（死亡 ÷ 经过的命数）、停留热力、性能卡顿、机制使用（墙滑、天花板、绳索、空中）、未到达区域、死亡等值线、动线、分段难度弧线、检查点、致死物排行、脱困（玩家用 Unstuck 的位置：琥珀色为就近推出，红色为传回检查点）。另有幽灵回放，并可导出带图例的 PNG。 |
| 总览 | KPI、各场景时长与死亡、会话表 |
| 检查点分段 | 每段的经过次数、死亡、首次通过率、中位耗时、难度评级。点一行会跳到地图上对应位置。另有分段 × 会话矩阵。 |
| 死亡分析 | 死因、致死物 Top 10、死亡时状态、存活时长分布、重生陷阱列表 |
| 难度曲线 | `difficulty_curve.py` 的交互版，可以切换归一化进度或实际时间 |
| 性能 | FPS、帧时间（含卡顿峰值）、GC，三张图联动缩放。另有各场景帧时间箱线图和渲染统计。 |

地图操作：

| 操作 | 效果 |
|---|---|
| 滚轮 | 缩放 |
| 拖动 | 平移 |
| 双击 | 聚焦死亡最多处 |
| `0` | 适配视图 |
| `+` / `-` | 缩放 |
| 方向键 | 平移 |
| 回放时按空格 | 播放 / 暂停 |
| 悬停在侧栏会话上 | 地图上只高亮该会话 |

## 日志格式（format=2）

样本行不变。新增的事件都写成以 `#` 开头的注释行，所以旧工具（包括 `difficulty_curve.py`）照常可用：

```
#E-schema,death,time_s,play_s,scene,x,y,hit_x,hit_y,cause,source,state,rope,vx,vy,life_s,seg,seg_deaths
#E-schema,respawn,time_s,play_s,scene,x,y,seg
#E-schema,checkpoint,time_s,play_s,scene,x,y,name,seg
#E-schema,segment,time_s,play_s,scene,seg,from,from_x,from_y,to,to_x,to_y,start_s,duration_s,play_dur_s,deaths,end_reason
```

- 字段一律按 schema 里的名字读取，不要按位置读。
- `play_s`：只在 Playing 状态下累计的时间，不含暂停和过场。
- `segment`：两次检查点之间的一段。`end_reason` 取值为 `checkpoint`、`scene` 或 `stop`。

旧日志（format=1，没有事件行）也能读：

- 死亡从 `deaths` 列的增量推断，位置取上一行的坐标。
- 有布局数据时，分段由轨迹经过检查点触发盒来推断。

这两类推断数据在界面上都会标为“推断”。

## Unity 内叠加

**Tools › Inkform › Analytics › PerfLog Scene Overlay** 会把同样的死亡点、热力和动线直接画在 Scene 视图的真实美术上，配色和形状与本工具一致。

## 文件

```
index.html  css/viewer.css
js/i18n.js     中英文案
js/theme.js    设计令牌 → Canvas / ECharts 配色（唯一来源）
js/parse.js    日志解析
js/model.js    会话、场景聚合、分段、路线顺序
js/heat.js     热力场、致死率、覆盖率、等值线
js/mapview.js  地图、图层、回放、导出
js/charts.js   图表卡片
js/app.js      界面与交互
vendor/echarts.min.js   ECharts 5.5.1（Apache-2.0），已本地化
data/          导出器生成的布局与实景图
```
