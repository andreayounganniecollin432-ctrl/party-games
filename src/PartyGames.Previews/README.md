# Party Games 界面预览（不开游戏出图）

这个工程把 Party Games 的每一屏都画成 PNG，不需要启动 FFXIV。用的是 Glasswork 官方的
[Glasswork.Preview](https://www.nuget.org/packages/Glasswork.Preview) 工具：真 Dear ImGui、真玻璃材质、真字体
栅格化，只是渲染到离屏纹理再存成图片。

也就是说：**改一行间距就能立刻看到效果**，不必进游戏、不必重载插件、不必找人开局。

## 为什么能这么干

插件的界面本来就是「服务端下发一个 `GameView`，客户端照着画」。预览工程因此只要**造一个和服务器一模一样的
`RoomSnapshot`**，再用同一套布局代码画出来，看到的就是玩家会看到的东西。

- `Mocks.cs` —— 八个游戏各一份快照，字段和服务器 `BuildView` 出来的同构（标题、副标题、私密信息、玩家卡、动作、积分、日志）。
- `RoomScreen.cs` —— 通用房间渲染器：一个渲染器覆盖全部游戏，因为这本来就是服务端驱动的界面。
- `LobbyScreen.cs` —— 选游戏页。
- `Chrome.cs` —— 面板、卡片、输入框这些共用零件。
- `Catalog.cs` —— 游戏目录与战绩的样本数据（和 `GameRegistry` 的 id / 名称 / 简介 / 人数一致）。

## 跑

```powershell
# 全部屏 -> preview-out\
.\render-previews.ps1

# 只出一屏
.\render-previews.ps1 -Scenes lobby

# 换背景（默认用插件的 assets\default-bg.png 更接近真实观感）
.\render-previews.ps1 -Backdrop src\PartyGames.Plugin\assets\default-bg.png
```

前置条件和 `build.ps1` 一样：.NET 10 SDK + Dalamud 开发包（`DALAMUD_HOME`，默认找
`%APPDATA%\XIVLauncherCN\addon\Hooks\dev`）。

## 中文渲染：需要给工具打个补丁

Glasswork.Preview 的字体加载器把字形范围写死成 Latin-only，汉字全画成 `?`。这是工具的限制，不是工程的问题。
`tools\patch-preview-cjk.ps1` 会把工具 DLL 里那条范围表的常量改成涵盖 CJK（8 字节，长度不变）：

```powershell
# 1. 装一份本地工具副本
dotnet new tool-manifest
dotnet tool install Glasswork.Preview --prerelease

# 2. 把工具目录复制出来，补上 Dalamud 的 native 依赖
#    （cimgui.dll / cimguizmo.dll / Dalamud.Bindings.ImGui.dll / HexaGen.Runtime.dll）

# 3. 打中文补丁
.\tools\patch-preview-cjk.ps1 -Dll <工具目录>\Glasswork.Preview.dll

# 4. 渲染时指定中文 TTF
.\render-previews.ps1 -Font C:\Windows\Fonts\simhei.ttf
```

工具升级后补丁要重打（脚本会把原件备份成 `.orig`，定位失败会直接报错而不是乱改）。

### 已知的预览限制

| 现象 | 原因 | 影响 |
|---|---|---|
| 输入框只画外观、不能编辑 | 这个 Dalamud 版本下，headless 里任何 ImGui 输入控件都会 AV（工具自带的 `forms` 场景同样崩） | 排版能看，交互要在游戏里验 |
| 全角标点（，。：）用 ASCII 代替 | 补丁只留了两段范围，塞不下 0xFF00 区 | 排版宽度略有差异 |
| 只有一个字号（16px） | 工具默认的界面字体按单一字号栅格化 | 标题的字重和游戏里可能不同 |

## 出图清单

| 屏 | 说明 |
|---|---|
| `lobby` | 选游戏：在线人数、战绩、八个游戏的卡片、建房/加房 |
| `game-undercover` | 谁是卧底：私密词、玩家描述状态、投票票数 |
| `game-werewolf` | 狼人杀：夜晚、狼人视角、刀人动作、房主设置 |
| `game-roulette` | 俄罗斯轮盘：轮到谁、扣扳机 |
| `game-snowball` | 堆雪球：雪球读数、滚/加固/砸人 |
| `game-wordchain` | 词语接龙：上一棒、输入框 |
| `game-turtlesoup` | 海龟汤：汤面、主持人的是/否/无关 |
| `game-doudizhu` | 斗地主：图形手牌、牌型提示、出牌/要不起/认输 |
| `game-xiangqi` | 中国象棋：圆盘棋子、楚河汉界、认输 |
