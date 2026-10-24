# 截图素材索引

本索引供后续对话和识别逻辑维护使用。每次收到项目相关截图都保存原图并更新索引，流程见 [LOCAL_FIXTURES.md](LOCAL_FIXTURES.md)。原图位于被 Git 忽略的 `LocalScreenshots/`，只保存在当前机器；索引和测试代码可提交。不要把“索引存在”当作“本机原图存在”，使用前检查路径。

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
- 这些是截图回放素材，不是实际游戏操作成功的录像。精灵准备界面“开始”按钮偶发无法识别的问题，仍缺卡住当时的截图；已有正常准备界面样本不能代替问题现场。

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
