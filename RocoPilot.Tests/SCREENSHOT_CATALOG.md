# 截图素材索引

本索引供后续对话和识别逻辑维护使用。每次收到项目相关截图都保存原图并更新索引，流程见 [LOCAL_FIXTURES.md](LOCAL_FIXTURES.md)。原图位于被 Git 忽略的 `LocalScreenshots/`，只保存在当前机器；索引和测试代码可提交。不要把“索引存在”当作“本机原图存在”，使用前检查路径。

## 2026-10-25：应用首页头图确认

- [确认原图 forest-village-approved.png](LocalScreenshots/2026-10-25/launch-cover/forest-village-approved.png)：用户附件 `codex-clipboard-42032763-7bb1-451a-b295-1d610f3e9043.png`，尺寸 **1983×793**，原样归档，未裁剪。
- 内容与状态：用户选定的卡通森林树屋插画，仅包含头图素材，不是应用运行截图，也不代表游戏内真实场景。
- 用途与对应素材：原样替换 `RocoPilot/Assets/LaunchPageCover.png`，用于首页头图视觉核对；附件、归档和应用资源 SHA-256 一致。
- 已知限制：未包含应用叠加的标题、渐变遮罩及边框；不用于 OCR 或游戏流程识别测试。尺寸与原头图相同，沿用现有 5:2 布局和资源引用。

## 2026-10-25：传说精灵列表、交互、缺少印记与捕捉确认

来源：当前对话中用户提供的 10 张附件，均已保存原始 PNG 并核对 SHA-256 与附件一致。原图均为 **2051×1197**，包含游戏窗口标题栏和边框；游戏客户区为 **x=1，y=44，宽=2048，高=1152**。以下路径相对于本文件。

| 原图 | 界面状态与预期 | 测试用途／已有派生素材 |
| --- | --- | --- |
| [season4-list.png](LocalScreenshots/2026-10-25/legendary/season4-list.png) | 传说精灵列表：圣羽翼王、龙息帕尔、银月狼王；用户说明前两只常驻，银月狼王限时 | OCR 名称、图鉴匹配、临时刷新列表；已有 `Fixtures/legendary-list-season4.png` |
| [wolf-world-challenge.png](LocalScreenshots/2026-10-25/legendary/wolf-world-challenge.png) | 直接传送到银月狼王后，大世界显示“挑战!”及“你好”，F 对应挑战 | 回归此前 F3→传送循环；已有 `Fixtures/legendary-wolf-interaction.png` |
| [wolf-missing-mark.png](LocalScreenshots/2026-10-25/legendary/wolf-missing-mark.png) | 提示当前未持有银月印记、无法进行捕捉；显示取消和确认 | 不依赖印记名称，取消→ESC→停止并通知；已有 `Fixtures/legendary-missing-mark.png` |
| [wolf-capture-stalled.png](LocalScreenshots/2026-10-25/legendary/wolf-capture-stalled.png) | 捕捉确认；体力 114、银月印记 1；右侧选中 | 旧图案模板导致 Unknown 的问题现场；客户区副本 `Fixtures/legendary-local/capture-wolf.png` |
| [wolf-contract-selected.png](LocalScreenshots/2026-10-25/legendary/wolf-contract-selected.png) | 银月狼王捕捉确认，左侧选中 | 客户区副本 `Fixtures/legendary-local/wolf-contract.png` |
| [wolf-mark-selected.png](LocalScreenshots/2026-10-25/legendary/wolf-mark-selected.png) | 银月狼王捕捉确认，右侧银月印记选中 | 客户区副本 `Fixtures/legendary-local/wolf-mark.png` |
| [arena-contract-selected.png](LocalScreenshots/2026-10-25/legendary/arena-contract-selected.png) | 常驻精灵，圆形石台背景，左侧选中 | 客户区副本 `Fixtures/legendary-local/arena-contract.png` |
| [arena-mark-selected.png](LocalScreenshots/2026-10-25/legendary/arena-mark-selected.png) | 常驻精灵，圆形石台背景，右侧普通传说印记选中 | 客户区副本 `Fixtures/legendary-local/arena-mark.png` |
| [bones-contract-selected.png](LocalScreenshots/2026-10-25/legendary/bones-contract-selected.png) | 常驻精灵，紫色巨骨背景，左侧选中 | 客户区副本 `Fixtures/legendary-local/bones-contract.png` |
| [bones-mark-selected.png](LocalScreenshots/2026-10-25/legendary/bones-mark-selected.png) | 常驻精灵，紫色巨骨背景，右侧普通传说印记选中 | 客户区副本 `Fixtures/legendary-local/bones-mark.png` |

### 解释边界

- 用户将最后六张作为当前三只传说精灵各自左右选中状态的补充。两个常驻组的弹窗未显示精灵名称，尚未单独确认石台／巨骨场景与圣羽翼王／龙息帕尔的对应关系，因此按场景命名，不作猜测。
- 左侧蓝色资源在游戏文案中称“契约印记”，对话中也称“体力”。左侧选中是紫底，未选中是灰底；右侧图案随印记变化，当前识别只依赖左侧两种状态。
- 最后六张显示体力 120、普通传说印记 35 或银月印记 1；左侧选中的文案显示本周还可使用 0 次。**选中状态不代表资源或次数可用**，这些素材验证识别与点击目标，不证明允许捕捉。
- 这些是截图回放素材，不是实际游戏操作成功的录像。精灵准备界面“开始”按钮的问题现场已在下方新增专题中归档；该截图离线识别成功，仍需核对运行时失败帧与用户截图的差异。

### 附件来源

原附件均名为 `codex-clipboard-<ID>.png`，临时目录可能被清理，后续应使用上面的归档文件。

| 归档名 | 附件 ID |
| --- | --- |
| season4-list | 6c6af3bd-2ae6-4718-84f7-621e60f5214e |
| wolf-world-challenge | b8d1a7a2-6293-40ec-9541-92a12677e787 |
| wolf-missing-mark | 4916b9d7-67bc-4705-b8fa-b3a6e98054e4 |
| wolf-capture-stalled | a1e5ed87-8f77-440b-8d94-0c6f01213324 |
| wolf-contract-selected | 7f28525c-8f8b-485a-9608-72f65d2957d7 |
| wolf-mark-selected | 65859b9e-b2cd-422b-9e00-c8c4f9c21ff5 |
| arena-contract-selected | fda849be-07d1-4005-8f12-011e1f4796b1 |
| arena-mark-selected | 0b547028-7d03-45b0-a51b-08f06761a0c9 |
| bones-contract-selected | 2a694e72-a521-4ce3-893a-190b91f5c137 |
| bones-mark-selected | 1d58727b-4356-4508-a2fb-e3a13548b5bc |

### 测试入口

- `LegendaryLocalScreenshotTests`：六张补充截图，在 2048×1152 和 1280×720 下检查弹窗、选中状态、确认位置及切换完成判断。
- `FlowerSeedScreenRecognizerTests`：列表 OCR、直接传送后的交互、缺印记提示、遮挡右侧图案／数量、左侧状态无法识别等。
- `FlowerSeedChallengeRunnerTests`：完整挑战回放，包括银月印记选中状态短暂不可读时等待，不提前确认。
- `LegendaryChallengeFlowTests`：阶段推进、捕捉方式偏好、缺少印记时退出。
- `IndependentTaskServiceTests`：缺少专属印记后的停止结果及通知设置。

本组捕捉确认素材最近验证：2026-10-25，14 项相关用例通过；六张补图均覆盖两种分辨率。重跑命令见 [本地回放说明](LOCAL_FIXTURES.md)。既有 `Fixtures/flower-seed-*.png` 和 `Fixtures/legendary-*.png` 还包含列表、地图、难度、准备、战斗、奖励等样本，可先按文件名和上述测试查找；它们的原始来源未在本对话核实，不冒充本次附件归档。

## 2026-10-25 03:13：挑战准备未点击开始

- [原图 preparation-start-stalled.png](LocalScreenshots/2026-10-25/legendary/preparation-start-stalled.png)：附件 `codex-clipboard-9c3793d3-e6b8-4a5f-9a87-8dfcfb14168b.png`，原图 **2048×1152**，已经是客户区，不能再次按 `(1,44)` 裁剪。已按原样归档并校验 SHA-256。
- [用户粘贴日志](LocalScreenshots/2026-10-25/legendary/preparation-start-stalled.log.txt)：来源为本次附件 `Pasted text.txt`，包含 03:13:52 至 03:14:35 的等待及超时信息。
- [源日志对应任务片段](LocalScreenshots/2026-10-25/legendary/preparation-task-source.log.txt)：来自本机 `app-20261025.log`，保留 03:12:22 至 03:14:35 的任务过程。
- 界面：挑战准备弹窗、1 个我方精灵、3 个神秘帮手、取消和开始按钮。任务源日志确认目标为圣羽翼王。
- 已确认：03:13:03.915 识别为 Preparation，但动作 None、按钮为空；03:14:34.638 超时，期间没有“开始”点击。上一处确认点击坐标为客户区 `(1184,946)`。
- 2026-10-25 临时诊断回放：当前代码识别该图为 Preparation，开始按钮匹配 **1.000**，匹配矩形 `(1147,931,81,41)`。既有 `legendary-preparation.png` 得分约 **0.999984**；Release 运行目录与源码 `start.png` 的 SHA-256 相同。因此不能把本次问题直接归因于模板、阈值或截图中按钮位置。
- 分析时发现：点击服务会把鼠标留在点击位置；修复前 WGC 会话只关闭捕获边框，没有显式关闭光标捕获。坐标重叠是调查线索，未保存的运行时失败帧不能被当前用户截图代替。
- 用户后续确认：上一个“本周契约印记次数已用尽”弹窗点击确认后，鼠标恰好停在“开始”文字处；只移开鼠标即可恢复点击，切出游戏再切回也能恢复。因此问题已收敛到鼠标停留导致的识别画面变化。优先验证排除 WGC 系统光标捕获；仍需区分系统光标遮挡与游戏自身悬停样式，不能把二者视为同一机制。
- 用户确认后采用最小修复：Windows 10 2004（19041）及以上的 WGC 会话在 `StartCapture()` 前设置 `IsCursorCaptureEnabled = false`，不修改模板、阈值、鼠标点击或重试行为。版本判断遵循项目现有最低版本 17763，避免在旧系统调用不存在的接口。该配置只影响捕获的系统光标，不会消除游戏自己的悬停样式。
- 客户区副本：`Fixtures/legendary-local/preparation-start-stalled.png`；回放入口为 `LegendaryLocalScreenshotTests.PreparationStartIsRecognizedWithoutCursor`，检查两种分辨率的开始按钮及复查位置。此测试验证无光标截图识别，真实悬停场景仍应在新会话中复测；已经运行的旧进程不会自动使用新代码。
- 修复验证：8 项本地截图回放通过；补上版本判断后再次运行两种分辨率的准备界面测试通过。临时诊断使用新代码对实际游戏窗口创建 WGC 会话，取得 **2052×1199** 帧，并读取确认 `IsCursorCaptureEnabled=False`。诊断未发送鼠标／键盘输入，不等同于完整自动挑战验收；临时诊断代码已移除。
