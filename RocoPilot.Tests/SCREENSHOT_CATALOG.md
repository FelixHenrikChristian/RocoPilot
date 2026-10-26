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

	## 2026-10-25：普通提交触发发布工作流后跳过构建

- 原图：[ordinary-commit-release-check-skipped.png](LocalScreenshots/2026-10-25/github-actions/ordinary-commit-release-check-skipped.png)，来源为本对话附件 `codex-clipboard-366f48db-c8aa-4ce3-8bb3-f45d8ebdd322.png`，尺寸 2549×1403；原样保存，未裁剪。SHA-256：`F8EBA74A72EF825A7C7781A7264DFAABC822380DA9F01B3146ACE7FC6FE6AD3F`。
- 界面状态：GitHub 仓库首页提交 `be6ce5e` 的检查面板，Build release assets 显示成功（19 秒），Create GitHub release 显示跳过。
- 测试用途：排查普通提交为何触发发布工作流，区分“工作流运行”与“实际构建／发布”；无需 OCR 或图像识别测试，未派生测试素材。
- 对应证据：[Actions 40170468948](https://github.com/FelixHenrikChristian/RocoPilot/actions/runs/40170468948)。该提交调整主项目的 Debug OCR 依赖与模型，改动 `RocoPilot/RocoPilot.csproj`，命中既有 paths 过滤；日志确认 v0.4.4.0 标签已存在，SDK 安装、还原、打包、上传和 Release 创建均跳过。
- 已知限制：截图仅展示检查摘要，无法单独判断步骤是否真正构建；上述执行结论来自对应 Actions 步骤与日志。当前 v0.4.4.0 Release 为草稿，目标为 `3502601`，不包含后续 `be6ce5e` 修改。

## 2026-10-26：新赛季确认界面布局调整前

- 来源：本侧对话用户提供的两张实际界面预览截图，原样保存并核对 SHA-256，未裁剪或覆盖历史素材。
- [异色捕获确认](LocalScreenshots/2026-10-26/new-season-statistics/shiny-confirmation-before.png)：附件 `codex-clipboard-c9a4cf84-a361-4633-9d5d-2c9a65bf1ae9.png`。界面为“确认暂存异色”，示例精灵火花、异色前奇遇 55 次，赛季仍显示“新赛季（待更新）”。SHA-256：`E62461A0E255B429A6F108D746780B86E8A1112DC1F000CF393D630C04029133`。
- [更新后确认归档](LocalScreenshots/2026-10-26/new-season-statistics/statistics-transfer-before.png)：附件 `codex-clipboard-524d5bb7-daeb-4a15-a51f-e4a1299fcb52.png`。每项为多行卡片，选择框标为“奇遇”，字段过宽，底部按钮为“确认计入并清理”。SHA-256：`FE447CD3DFB672E68AA29B039B62813DD2E25256AA1CBDC6223EBC35D3E5F289`。
- 用途：作为名称、统一确认标题、单行列表和选择提示的修改前视觉依据；对应 `PendingShinyConfirmationPanel`、`TemporaryStatisticsDialog` 及赛季名称展示断言。未新增 OCR 派生样本。
- 已知限制：截图来自独立示例预览，其窗口大小和主题与用户正式应用不同；画面中的赛季、日期、精灵和次数属于示例，不代表真实捕获记录，也不证明同步或入账成功。

## 2026-10-26：普通调试版启动后的云同步冲突提示

- [用户原图](LocalScreenshots/2026-10-26/new-season-statistics/debug-app-sync-conflict.png)：附件 `codex-clipboard-4e34ab8b-35e4-49dc-be01-3a63826244a9.png`，原样归档。SHA-256：`D53EE277828F598545E5707D85E92D6D672598B319EEC445C85D2B3104081E55`。
- 界面：正式统计页面显示账号统计，云同步提示旧版数据与升级时快照不同，并提示未覆盖或重复入账。
- 用途：记录普通调试版仍会走真实启动及同步流程的问题；界面预览改为独立入口，只使用内存示例数据，不初始化真实设置、统计服务或云同步。
- 已知限制：截图中的“未覆盖或重复入账”是程序提示，不能单凭截图证明启动前后本地统计完全一致；本次未对真实记录执行恢复、导入或覆盖操作。未派生 OCR 测试样本。

## 2026-10-26：独立归档预览的分组与焦点反馈

- [用户原图](LocalScreenshots/2026-10-26/new-season-statistics/transfer-list-before-cards.png)：附件 `codex-clipboard-5bb41f9f-9442-4de8-ae78-add72bb90429.png`，原样归档。SHA-256：`143F800100B723A35942D4FA47570E4303F414BECF0A8EE94D6AFDB6811D5029`。
- 界面：深色独立预览中的新赛季归档弹窗，单行输入布局、方形选择框、奇遇与异色仅用文字分组，已选 1 项，火花名称输入框处于编辑状态。
- 用途：参考正式统计卡片，改为奇遇和异色两张卡片、圆角选择按钮。用户随后决定保留 WinUI 默认焦点行为，点击空白失焦的试验代码已撤回。
- 已知限制：截图只展示单帧状态，不能单独证明焦点交互；布局及选择行为在独立示例程序中验证，不访问真实账号或同步服务。

## 2026-10-26：升级后的旧版云端快照冲突

- [用户原图](LocalScreenshots/2026-10-26/new-season-statistics/sync-legacy-conflict-after-commit.png)：附件 `codex-clipboard-bdf8d6ab-42af-4680-b68d-82eacf6039a3.png`，原样归档，未裁剪。SHA-256：`0AC2B8523881FE11CD4AA523FEBC365C7376DF2642D004F4F3797F5F4AA0B366`。
- 界面状态：统计页显示异色 60、S4 奇遇 195，云同步报告旧版数据与升级时快照不同。
- 测试用途：核对本地、云端和同步基线的指纹差异，复现升级误判；不使用截图推断账号数据是否完整，未派生 OCR 素材。
- 已知限制：截图仅证明出现冲突提示；实际差异须通过只读统计副本核对，不能据此直接选择覆盖任一端。
- 核对结果：本地仅完成旧格式清理，云端正式奇遇新增 5 次；离线回放修复后 S4 奇遇从 195 更新到 200，异色仍为 60。副本回放未修改真实设置、同步基线或云端对象。通用回归入口：`StatisticsLegacySyncTests`。
