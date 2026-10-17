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
| **6 宝箱与胡萝卜采集** | 内置新月岛宝箱 / 福运胡萝卜候选刷新点数据；优先前往对象表里**真实存在**的目标，没有目标时按候选点巡逻 |
| **7 幻影职业 Buff** | 自动切到对应幻影职业，施放增益后切回原职业：不屈之躯、迅足、罗密欧的歌谣、轻快舞步 |
| **8 刷怪** | 在指定半径内聚怪，聚够数量后进入战斗，可限制等级差与是否飞行 |
| **9 两歧塔陷阱** | 运行时扫描陷阱对象并在界面列表中标出坐标，非服务器共享数据 |
| **10 收益统计** | 按小时统计经验与货币（银 / 金）获取速度 |
| **11 总控与设置** | 循环总开关、紧急停止（F12）、模块独立开关、DTR 状态栏、运行日志与统计 |

### 与 bozjalone 的关系

bozjalone 是闭源插件，它的「胡萝卜」「宝箱」「两歧塔」模块各带一个几 MB 的预计算
JSON（2 个文件合计约 10 MB），里面是候选点/路线数据。本插件**不打包这些预计算路线**：

- 宝箱与胡萝卜的**候选点坐标**是公开的游戏数据，本插件内置了一份精简版本；
- 「这个点现在到底有没有宝箱」只有**对象表**能回答，所以采集优先看对象表，
  候选点只用于「没东西可捡时去哪儿转一圈」；
- 两歧塔陷阱同理——位置由运行时扫描得到，不依赖任何上传/下载的共享数据，
  因此也不会把你的位置发到任何第三方服务器。

本插件**不做**众包位置上传，也**不内置** Illegal Mode 那种「全自动打本」行为。

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
- **BossMod Reborn** — 可选的战斗 AI 开关（本插件不驱动它为必需依赖）

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
| `/bzj hunt` | 打印采集数据规模与当前选中的采集目标 |
| `/bzj buff` | 打印幻影职业、各增益的状态 ID / 技能 ID 与当前剩余时间 |
| `/bzj stats` | 打印经验与货币的每小时收益 |
| `/bzj reload` | 刷新游戏数据缓存 |
| `/bzj clear` | 清空运行日志 |

## 安装

### 通过自定义仓库源

1. 游戏内 `/xlplugins` → **Settings** → **Experimental** → **Custom Plugin Repositories**
2. 填入（任选其一）：

   ```
   https://ghfast.top/https://raw.githubusercontent.com/solenmingco/BozjaAssistant/main/pluginmaster.json
   ```

   ```
   https://ghproxy.net/https://raw.githubusercontent.com/solenmingco/BozjaAssistant/main/pluginmaster.json
   ```

   ```
   https://gh-proxy.com/https://raw.githubusercontent.com/solenmingco/BozjaAssistant/main/pluginmaster.json
   ```

   > **关于「发布后仍显示无需更新」**
   >
   > 这不是某一个镜像的问题。实测 GitHub 自己更新 `raw.githubusercontent.com` 上的文件就有约
   > **1 分钟**延迟，各镜像在此之上还有自己的缓存，所以发布后短时间内看到旧版本是正常的。
   >
   > 实测一次发布后逐次轮询（发布的是一次只改文档的版本）：
   >
   > | 地址 | 0s | 20s | 40s | 60s | 80s | 100s | 120s |
   > | --- | --- | --- | --- | --- | --- | --- | --- |
   > | GitHub 直连 | 旧 | 旧 | 旧 | 旧 | 新 | 新 | 新 |
   > | `ghfast.top` | 新 | 新 | 新 | 旧 | 新 | 新 | 新 |
   > | `gh-proxy.com` | 新 | 新 | 新 | 新 | 新 | 新 | 新 |
   > | `ghproxy.net` | 旧 | 旧 | 旧 | 旧 | 旧 | 旧 | 旧 |
   >
   > 结论：**发布后等 1~2 分钟再 Scan for updates**。国内网络下 `ghfast.top` 或
   > `gh-proxy.com` 都是可用选择；`ghproxy.net` 在这台机器上追新版最慢。
   >
   > 不要用 `cdn.jsdelivr.net/...@main/...`：它对**分支引用**缓存最长 7 天，
   > 会出现「已发布却持续几天显示无需更新」。

3. 保存后回到插件列表，搜索 **Bozja** 安装即可。

> 怀疑缓存时，最快的确诊方式是在浏览器直接打开你填的那个源地址，看
> `AssemblyVersion` 字段是多少——比在游戏里反复点刷新快得多。

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
- **不内置预计算路线。** 采集优先读对象表；候选点数据只是「没东西可捡时的巡逻方向」。
  这样数据规模从 bozjalone 的约 10 MB 降到几十 KB，也不会随版本过期而失效。
- **幻影职业增益分三段推进。** 切职业 → 放技能 → 切回原职业，每段跨一个 tick，
  因为职业切换后游戏要一拍才认；用单个布尔量无法表达这三态，会丢掉原职业。

## 目录结构

```
BozjaAssistant/
├── BozjaAssistant/
│   ├── Automation/          # 自动化逻辑
│   │   ├── AutomationController.cs   # 状态机主循环
│   │   ├── TargetSelector.cs         # 目标选择（纯函数，无副作用）
│   │   ├── CombatModule.cs           # 战斗与外部循环插件联动
│   │   ├── DropAlertModule.cs        # 背包增量监控
│   │   ├── HuntModule.cs             # 宝箱 / 胡萝卜采集
│   │   ├── MobFarmerModule.cs        # 聚怪
│   │   ├── BuffModule.cs             # 幻影职业增益
│   │   ├── ForkedTowerModule.cs      # 两歧塔陷阱扫描
│   │   ├── StatsModule.cs            # 经验 / 货币收益
│   │   ├── NavmeshIpc.cs             # vnavmesh IPC 封装
│   │   └── LifestreamIpc.cs          # Lifestream IPC 封装
│   ├── Data/                # 游戏数据读取
│   │   ├── FateScanner.cs            # FATE 表快照
│   │   ├── CriticalEngagementScanner.cs  # 紧急遭遇战表快照
│   │   ├── AethernetScanner.cs       # 以太水晶分层与跨层规划
│   │   ├── HuntDataStore.cs          # 内置候选点数据
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
