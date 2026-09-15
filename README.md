# Valheim PingHud — 服务器延迟 / 丢包监控

在 HUD 上显示**当前服务器的延迟（ms）和丢包率（%）**，外观与 [Aidin's DayTimeCountdown](../Aidin-DayTimeCountdown)
的小面板保持一致（同一个字体、描边、半透明底图、同样的 200×30 尺寸），
但默认放在**小地图正下方**，而 DayTimeCountdown 默认放在小地图正上方，两者不会重叠。
即使把 DayTimeCountdown 改成“显示在小地图下方”，本插件也会自动避让（见下文）。

```
┌──────────────────────────────┐   ← DayTimeCountdown（小地图上方，默认）
│ Day 128            13m 42s   │
└──────────────────────────────┘
              ┌───────┐
              │ 小地图 │
              └───────┘
┌──────────────────────────────┐   ← PingHud（小地图下方）
│ 延迟 45 ms        丢包 0.0%  │
└──────────────────────────────┘
```

---

## 1. 显示内容

| 行 | 左侧 | 右侧 | 说明 |
|----|------|------|------|
| 第 1 行 | 延迟 `45 ms` | 丢包 `0.4%` | 默认显示，按阈值自动染成绿 / 黄 / 红 |
| 第 2 行 | 抖动 `3.2 ms` | 质量 `99%` | 需开启“显示详细信息” |
| 第 3 行 | `↓ 12.4 kB/s` | `↑ 3.1 kB/s` | 需开启“显示详细信息” |

没有数据源的格子会显示 `--`（暂无数据）或 `N/A`（该连接方式不提供），**不会编造数字**。
详细行为见下面 1.1 / 1.2 —— 这三种连接方式的可用数据差别很大，务必先看。

- 单人游戏 / 未连接服务器时默认**隐藏**（可在配置里改成显示“未连接”）。
- 游戏隐藏 HUD（`Hud.IsVisible()` 为 false）时同步隐藏。
- 默认按 **F8** 临时显示 / 隐藏（Valheim 自身占用了 F1、F2、F3、F9、F11，F8 未被占用）。

### 1.1 三种连接方式的数据可用性（**最重要**）

Valheim 有 4 种 socket 实现，**它们提供的统计数据完全不同**，这是"延迟 0 ms / 丢包 100% 但能正常游玩"的唯一原因：

| Socket 实现 | 什么时候用 | 延迟 | 丢包 / 质量 | 带宽 |
|---|---|---|---|---|
| `ZSteamSocket` | Steam P2P（好友邀请 / 服务器浏览器 / 关掉跨平台） | ✅ 真实 | ✅ 真实 | ✅ 真实 |
| `ZPlayFabSocket` ⚠️ | **跨平台联机（Crossplay）**，走 PlayFab 中继 | ❌ 硬编码 0 | ❌ 硬编码 0 | ✅ 真实 |
| `ZSocket2` | 直连 IP / 局域网（TCP） | ❌ 硬编码 0 | ❌ 硬编码 0 | ✅ 真实 |
| `ZSteamSocketOLD` | 旧版 Steam 传输 | ❌ 全 0 | ❌ 全 0 | ❌ 全 0 |

`ZPlayFabSocket` 和 `ZSocket2` 都继承 `ZNetStats`，而 `ZNetStats.GetConnectionQuality()` 的结尾恒为：

```csharp
localQuality = 0f;   // 延迟与连接质量根本不存在
remoteQuality = 0f;
ping = 0;
outByteSec = m_sendRate;   // 只有带宽是真的
inByteSec  = m_recvRate;
```

所以 `0 ms + 100%` 不是"丢包 100%"，而是**该传输层压根不提供延迟/丢包**。
（TCP 也不把丢包暴露给应用层——重传在内核里就完成了。）

**怎么判断自己属于哪一类**：看 `BepInEx\LogOutput.log`，出现 `Connecting to server with PlayFab-backend`
或 `ZPlayFabSocket` = 跨平台；出现 `ZSocket2` = 直连 IP；都没有（用 Steam 邀请/浏览器进服）= Steam P2P。

**想要真实的丢包率**：用 Steam P2P 连接（关掉世界/服务器的 Crossplay，用 Steam 好友邀请或服务器浏览器），
此时走 `ZSteamSocket`，丢包与延迟都是 Steam 的实测值。

### 1.2 各传输层的据来源与显示规则

| 面板显示 | Steam P2P | 跨平台 / 直连 TCP | 数据来源 |
|----------|-----------|-------------------|----------|
| 延迟 | ✅ `m_nPing` | ✅ 游戏自身 RPC ping/pong 实测 | 见下 |
| 丢包 | ✅ `1 − m_flConnectionQualityLocal` | `N/A`（无数据源） | Valve: 端到端按序送达包比例 |
| 抖动 | ✅ 最近 32 次采样标准差 | ✅ 同样计算 | 由延迟样本算出 |
| 质量 | ✅ `m_flConnectionQualityLocal` | `N/A` | 同上 |
| 上下行 | ✅ `m_flOut/InBytesPerSec` | ✅ 同样可用 | `ZNetStats` 字节计数 |

标记含义：**`--`** = 暂时还没有数据；**`N/A`** = 这种连接方式不提供该项数据。

> **延迟**：Steam P2P 用 `m_nPing`；其他传输层改用游戏自身的 RPC ping/pong 实测——
> `ZRpc` 每 **1 秒**发一次 ping（`m_pingInterval = 1`），收到回包时把 `m_timeSinceLastPing` 归零，
> 而 `m_pingTimer` 正是"发出 ping 到现在"的计时，两者相减即为真实往返时间。
> 游戏每帧只处理一次网络，所以该值会被量化到一帧（60fps 下约 16 ms），已做平滑处理。
>
> **丢包**：Valve 在 `steamnetworkingtypes.h` 对 `m_flConnectionQualityLocal` 的定义是
> *"Percentage of packets delivered end-to-end in order"*，所以 `1 − 质量` 就是实打实的丢包率：
> `0.0%` = 全部送达，`100%` = 一个都没送到（真出现时游戏早就掉线了）。
> 非 Steam 传输层没有等价数据源，因此显示 `N/A` 而不是编造一个数字。

---

## 2. 安装

### 用 r2modman / Thunderstore Mod Manager（推荐）

1. 打开 Valheim 的 profile → **Settings → Import local mod**，选择本目录（含 `manifest.json`）；
   或者直接把 `dist\ValheimPingHud.dll` 拖进 profile 的 `BepInEx\plugins\` 下任意子文件夹。
2. 启动游戏即可，配置文件会自动生成。

### 手动安装

把 `dist\ValheimPingHud.dll` 复制到：

```
<Valheim>\BepInEx\plugins\ValheimPingHud\ValheimPingHud.dll
```

r2modman 用户对应的目录是：

```
%APPDATA%\r2modmanPlus-local\Valheim\profiles\<Profile>\BepInEx\plugins\ValheimPingHud\
```

依赖：**BepInExPack Valheim**（BepInEx 5.4.x）。不需要 Jotunn 或其他前置。

卸载：删掉 `Plugins\ValheimPingHud` 文件夹即可。

---

## 3. 配置

配置文件：`BepInEx\config\kagegawa.valheim.pinghud.cfg`（首次运行后生成，
游戏内按 F1 打开 ConfigurationManager 也能改，如果安装了该插件）。

### 位置（关键：不重叠）

| 配置项 | 默认 | 说明 |
|--------|------|------|
| `Anchor 位置` | `BelowMinimap` | `BelowMinimap` 小地图正下方 / `AboveMinimap` 小地图正上方 / `TopRight` / `TopLeft` / `BottomRight` / `BottomLeft` |
| `Extra X offset 额外X偏移` | `0` | 在所选位置基础上再平移 |
| `Extra Y offset 额外Y偏移` | `0` | 正数向上 |
| `Auto avoid other panels 自动避让其他面板` | `true` | 与下列面板重叠时自动错开 |
| `Avoid panel names 避让面板名称` | `DayTimePanel` | 逗号分隔的 HUD 子物体名；DayTimeCountdown 的面板名就是 `DayTimePanel` |
| `Avoid gap 避让间距` | `8` | 避让后保留的像素间距 |

避让逻辑：每帧用 `RectTransform.GetWorldCorners` 测量本面板与被避让面板在同一个父节点下的矩形，
若水平方向有交叠且垂直方向重叠，就沿“远离方向”整体移动，直到完全让开。
这保证了**无论 DayTimeCountdown 设成小地图上方还是下方，两个面板都不会重叠**。

另外，小地图的矩形是**实时测量**得到的（`Minimap.m_smallRoot`），
测量结果不合理时会退回按 DayTimeCountdown 的布局反推的常量，所以任何 HUD 缩放下都能贴合。
小地图上方只有约 40px 空间，若选了 `AboveMinimap` 又开了“显示详细信息”（面板高 74px），
面板会自动改放到小地图下方，避免顶出屏幕。

### 外观（与 DayTimeCountdown 同名同义，便于统一风格）

`Panel width 面板宽度`(200)、`Panel height 面板高度`(30)、
`Font name 字体名称`(`auto`)、`Font size 字号`(16)、`Font color 字体颜色`、
`Text outline enabled 文字描边`、`Text outline color 描边颜色`、
`Display background 显示背景`、`Background color 背景颜色`、`Padding left and right 左右内边距`。

`Font name = auto` 时：中文界面使用系统中文字体（微软雅黑 / SimHei …，保证汉字能正常渲染），
英文界面使用游戏自带字体 `AveriaSansLibre-Bold`（和 DayTimeCountdown 一致）。
也可以填任意字体名（先找游戏资源，再找系统字体）。

### 显示

`Show ping 显示延迟`、`Show packet loss 显示丢包`、`Show extra details 显示详细信息`、
`Smooth values 数值平滑`、`Show text labels 显示文字标签`、`Ping on the right 延迟显示在右侧`、
`Color by quality 按质量着色`、`Ping good (ms) 延迟良好阈值`(80)、`Ping bad (ms) 延迟较差阈值`(150)、
`Loss good (%) 丢包良好阈值`(1)、`Loss bad (%) 丢包较差阈值`(5)、
`Language 语言`(`auto` / `Chinese` / `English`)。

---

## 4. 从源码构建

仓库结构：

```
ValheimPingHud/
├── src/
│   ├── PingHudPlugin.cs      BepInEx 插件、配置、生命周期
│   ├── NetStatsSampler.cs    网络数据采样与平滑
│   ├── PingHudPanel.cs       HUD 面板、定位与自动避让
│   └── HudStrings.cs         中英文文案与数值格式化
├── tools/verify-refs.ps1     编译产物的引用静态校验
├── build.ps1                 用 VS 自带的 csc 编译
├── ValheimPingHud.csproj     给装了 .NET SDK 的人用
└── dist/ValheimPingHud.dll   构建产物
```

```powershell
# 编译（自动定位 Valheim 与 BepInEx；本机没装 .NET SDK 也能用）
powershell -ExecutionPolicy Bypass -File .\build.ps1

# 编译并安装到 r2modman 的 Default profile
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Deploy

# 校验产物里所有类型/方法/字段引用都能在游戏程序集里解析出来
powershell -ExecutionPolicy Bypass -File .\tools\verify-refs.ps1
```

`build.ps1` 优先使用 Visual Studio 2019/2022 的 `csc.exe`，直接对着
`valheim_Data\Managed` 里的 Mono 程序集编译（`-nostdlib+`），因此不需要 .NET SDK；
如果机器上有 SDK，则回退到 `dotnet build`。

游戏路径可用参数或环境变量指定：
`-ValheimDir`、`-BepInExCoreDir`、`$env:VALHEIM_DIR`、`$env:BEPINEX_CORE_DIR`。

---

## 5. 已知限制

- 「丢包」是从 Steam 连接质量换算的估计值，原因见第 1 节。
- 作为**主机 / 专用服务器**时，`ZNet.GetNetStats` 返回的是所有已连接玩家的平均值，
  面板会显示平均值（此时它代表整体网络状况，而不是单个玩家）。
- 面板挂在 `Hud.m_rootObject` 之下，和 DayTimeCountdown 一样；切场景时会自动重建。
- 目前只在连接服务器（含作为主机且有玩家加入）时显示数据。
