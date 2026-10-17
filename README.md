# Bozja / 新月岛 助手（BozjaAssistant）

面向国服（卫月 / Dalamud CN）的 FFXIV 特殊野外内容自动化辅助插件，同时支持：

- **博兹雅战线 / 扎杜诺尔高原**（Bozja：Southern Front / Zadnor）
- **新月岛 南部 / 北部**（Occult Crescent，7.2+）

本插件是在研究民间插件 **bozjalone**（作者 ヌルさん，闭源二进制分发）的功能后，
**从零用 C# 重写的开源实现**，不复用任何其源码或二进制。

主命令：`/bozja` 或 `/bzj`

---

## 功能模块

| 模块 | 说明 |
| --- | --- |
| **1 FATE 模块** | 扫描当前地图全部 FATE，按可配置优先级（最近 / 进度最低 / 剩余时间最少 / 等级最高）自动寻路，支持距离、进度、剩余时间过滤与加成 FATE 优先 |
| **2 紧急遭遇战 CE** | 读取游戏 `DynamicEvent` 表，实时监控「报名中 / 准备中 / 战斗中」状态，报名或热身阶段即自动前往，支持名称黑名单与优先级高于 FATE |
| **3 掉落提醒** | 监控背包增量，获得指定物品（德米亚特玛、灵魂碎片等）时聊天栏提示 + 音效，物品列表可在界面内维护 |
| **4 自动战斗** | 自动锁定交战距离内最近的敌对目标；技能释放交给外部循环插件，本插件不自行实现任何职业循环 |
| **5 通用自动化** | 死亡自动复活（可设延迟）、复活后自动返回、长距离移动自动上坐骑（到达目标附近后自动下坐骑）、以太水晶跨层移动 |
| **6 总控与设置** | 循环总开关、紧急停止（F12）、模块独立开关、DTR 状态栏、运行日志与统计 |

### 以太水晶跨层移动（新月岛）

新月岛的**上 / 中 / 下三层是三块互不相通、垂直堆叠的独立区域**，中间没有坡道或楼梯，
只能靠内部以太之光水晶互相传送。vnavmesh 不知道这件事：把别层的坐标交给它，它只会走到
本岛边缘然后停下，看起来像是插件无视了这个目标。

本插件在**下发寻路之前**先做跨层决策：

1. 以太水晶是普通的 `ObjectKind.Aetheryte` 对象，世界坐标随时可从对象表读到——不查表、不猜偏移。
2. 按高度聚类，垂直间隔超过 12 米的归为不同层。
3. 目标与玩家不同层时：先走到**本层**最近的水晶，到达后请 Lifestream 传送到**目标层**离目的地最近的落点。
4. 同层时行为与之前完全一致，不走这一步。
5. 每个目标只传送一次，落点水晶恰好也是最近水晶时不会来回横跳。

传送是通过 `AethernetTeleportByPlaceNameId`（PlaceName 行号）发出的，与客户端语言无关。
插件**不自己开水晶菜单**——那样会在别的客户端语言下失效。

诊断：

```
/bzj aether
```

打印每个水晶被判为哪一层、PlaceNameId、坐标与距离，以及当前目标的分层与路线规划。
层是从高度推断的，若分组与你在游戏里看到的层不符，这条命令会直接暴露出来。

## 依赖（均为可选）

缺失时对应功能自动禁用并在界面中提示，不会报错崩溃。

- **[vnavmesh](https://github.com/awgil/ffxiv_navmesh)** — 自动寻路（必需，否则无法移动）
- **[Lifestream](https://github.com/NightmareXIV/Lifestream)** — 跨区域传送、以太水晶跨层传送
- **Rotation Solver Reborn** 或 **WrathCombo** — 自动战斗时释放技能

## 命令

| 命令 | 说明 |
| --- | --- |
| `/bozja` / `/bzj` | 打开主窗口 |
| `/bzj on` | 开始自动循环 |
| `/bzj off` | 停止自动循环 |
| `/bzj panic` | 紧急停止（需手动解除） |
| `/bzj status` | 在聊天栏显示当前状态 |
| `/bzj fates` | 打印所有 FATE 的原始字段与筛选判定 |
| `/bzj aether` | 打印以太水晶列表、分层与跨层路线规划 |
| `/bzj reload` | 刷新游戏数据缓存 |
| `/bzj clear` | 清空运行日志 |

## 安装

### 通过自定义仓库源

1. 游戏内 `/xlplugins` → **Settings** → **Experimental** → **Custom Plugin Repositories**
2. 填入（二选一，都实测实时）：

   ```
   https://ghfast.top/https://raw.githubusercontent.com/solenmingco/BozjaAssistant/main/pluginmaster.json
   ```

   ```
   https://ghproxy.net/https://raw.githubusercontent.com/solenmingco/BozjaAssistant/main/pluginmaster.json
   ```

   > **为什么不用其它地址**（2026-02 实测，同一次发布后逐一看返回的版本号）
   >
   > | 地址 | 发布后返回 |
   > |---|---|
   > | `ghfast.top` / `ghproxy.net` | 新版本 ✅ |
   > | `raw.githubusercontent.com` 直连 | 新版本 ✅，但国内多数网络**解析不到这个域名** |
   > | `cdn.jsdelivr.net/...@main/...` | 能拉到，但对**分支引用**缓存最长 7 天，会出现「已发布却一直显示无需更新」 |
   > | `gh-proxy.com` | ❌ **仍返回上一个版本**，过期后依然如此，实际会卡住更新检测 |
   >
   > 早先推荐过 `gh-proxy.com`，那是只测了可用性没测新鲜度，已纠正。

3. 保存后回到插件列表，搜索 **Bozja** 安装即可。

> 若刚发布完就查、却看到旧版本：先等 1 分钟再 **Scan for updates**。
> 确认清单里到底是哪个版本，可以在浏览器打开你填的那个源地址直接看
> `AssemblyVersion` 字段——这比在游戏里反复点刷新快得多。

### 手动安装

下载 [Releases](https://github.com/solenmingco/BozjaAssistant/releases) 中的 `latest.zip`，
解压到 `%APPDATA%\XIVLauncher\plugin\BozjaAssistant\`（卫月为对应目录），重启游戏。

## 从源码构建

需要 **.NET 10 SDK**。

```bash
dotnet build BozjaAssistant/BozjaAssistant.csproj -c Release
```

产物：

```
BozjaAssistant/bin/Release/BozjaAssistant.dll          # 插件本体
BozjaAssistant/bin/Release/BozjaAssistant/latest.zip   # 可分发压缩包
```

## 设计说明

几个刻意的取舍，便于后续维护：

- **不实现职业循环。** 循环是按职业、按版本持续变化的，专业插件做得更好。
  本模块只负责「锁定目标」和「维持交战距离」——这两件事恰恰是玩家在
  FATE 之间赶路时，循环插件不会替你做的部分。
- **状态机在框架线程上跑，且 tick 限流（200ms）。** 游戏状态变化没那么快，
  没必要逐帧扫描；同时避免任何阻塞调用。
- **异常全部就地吞掉并记录。** 框架回调里抛异常会被 Dalamud 直接摘掉，
  等于静默禁用插件——这比功能暂时失效更糟。
- **寻路用 `PathfindAndMoveCloseTo` 而非 `MoveTo`。** FATE / CE 标记常落在
  悬崖、平台或建筑里，站到「附近」才是玩家真正会做的事。
- **不硬编码任何国服行号。** 物品名、状态等一律运行时从游戏表读取，
  因为国服与国际服的 RowId 并不一致。
- **紧急停止是闩锁。** 触发后必须在界面手动解除，避免误触发后自动续跑。

## 目录结构

```
BozjaAssistant/
├── BozjaAssistant/
│   ├── Automation/          # 自动化逻辑
│   │   ├── AutomationController.cs   # 状态机主循环
│   │   ├── TargetSelector.cs         # 目标选择（纯函数，无副作用）
│   │   ├── CombatModule.cs           # 战斗与外部循环插件联动
│   │   ├── DropAlertModule.cs        # 背包增量监控
│   │   ├── NavmeshIpc.cs             # vnavmesh IPC 封装
│   │   └── LifestreamIpc.cs          # Lifestream IPC 封装
│   ├── Data/                # 游戏数据读取
│   │   ├── FateScanner.cs            # FATE 表快照
│   │   ├── CriticalEngagementScanner.cs  # 紧急遭遇战表快照
│   │   └── GameDataCatalog.cs        # 物品等表数据缓存
│   ├── Windows/             # 界面
│   ├── Configuration.cs     # 配置（持久化为 JSON）
│   ├── ActivityLog.cs       # 内存日志
│   └── Plugin.cs            # 入口
└── build.ps1
```

## 免责声明

本插件为第三方非官方工具。使用自动化工具可能违反游戏用户协议，
**使用风险由使用者自行承担**。请勿用于影响其他玩家体验的场景。

## 许可

[MIT](LICENSE)
