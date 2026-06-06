<div align="center">
  <img src="RocoPilot/Assets/RocoPilot.png" width="112" alt="RocoPilot" />
  <h1>🧭 RocoPilot</h1>
  <p><strong>自动战斗 · 奇遇计数</strong></p>

  <p>
    <img src="https://img.shields.io/badge/platform-Windows-0078D4?logo=windows" alt="Windows" />
    <a href="https://github.com/FelixHenrikChristian/RocoPilot/releases/latest"><img src="https://img.shields.io/github/v/release/FelixHenrikChristian/RocoPilot?label=version" alt="Release version" /></a>
    <a href="https://github.com/FelixHenrikChristian/RocoPilot/releases"><img src="https://img.shields.io/github/downloads/FelixHenrikChristian/RocoPilot/total?label=downloads" alt="Downloads" /></a>
    <a href="LICENSE"><img src="https://img.shields.io/github/license/FelixHenrikChristian/RocoPilot" alt="License" /></a>
  </p>
  <p>
    <a href="https://github.com/FelixHenrikChristian/RocoPilot/releases/latest">⬇️ 下载</a>
    ·
    <a href="#usage">🚀 使用</a>
    ·
    <a href="#features">✨ 功能</a>
    ·
    <a href="#notice">💡 说明</a>
    ·
    <a href="#license">📜 许可</a>
  </p>
</div>

RocoPilot 是一款 Windows 桌面辅助工具，面向游戏《洛克王国·世界》相关战斗场景，主要提供自动战斗和奇遇计数能力，并通过实时识别、信息遮罩、图鉴预览、热键、日志诊断和自动更新等辅助能力支撑日常使用。

<p align="center">
  <img src="RocoPilot/Assets/LaunchPageCover.png" width="820" alt="RocoPilot 启动页封面" />
</p>

<a id="features"></a>

## ✨ 功能

### 主要功能

|  | 功能 | 说明 |
| --- | --- | --- |
| ⚔️ | 自动战斗 | 支持自定义技能释放顺序、自定义按键序列、可选择奇遇解除策略。异色出现后自动终止操作。 |
| 📊 | 奇遇计数 | 记录赛季奇遇次数，并支持账号管理、数据编辑、异色确认、导入导出和云同步功能。 |

### 辅助能力

|  | 能力 | 说明 |
| --- | --- | --- |
| 🔎 | 实时识别 | 自动识别当前画面状态，为奇遇计数、自动战斗等任务提供基础。 |
| 🪟 | 信息遮罩 | 在游戏窗口旁显示运行状态，显示奇遇计数、自动战斗启用状态和待确认异色提示，可锁定或重置位置。 |
| 📚 | 图鉴预览 | 内置图鉴框架，可选择图鉴源，同步并查看精灵头像、变种和进化链等数据。 |
| ⌨️ | 热键绑定 | 为信息遮罩、奇遇计数和自动战斗配置快捷切换。 |
| 🧾 | 日志诊断 | 内置日志页，方便排查识别、运行和异常信息。 |
| 🚀 | 自动更新 | 支持通过在线更新器或完整安装包升级。 |

<a id="usage"></a>

## 🚀 使用

### 下载安装

1. 打开 [Releases](https://github.com/FelixHenrikChristian/RocoPilot/releases/latest) 页面。
2. 下载 `RocoPilot-Setup-v*.exe`，参考提示进行安装。
3. 安装后启动 RocoPilot，并保持目标游戏窗口处于正常状态。

### 启动识别

1. 在“启动”页选择截图方式和 OCR 识别方法。
2. 推荐优先使用 `Windows Graphics Capture` 与 `PaddleOCR PP-OCRv5`。
3. 需要观察识别范围时，可开启“显示识别区域”。
4. 需要查看运行状态时，可开启“信息遮罩窗口”。
5. 点击“启动”，确认工具已正常绑定窗口并开始识别。

### 使用奇遇计数

1. 在“实时”页开启“奇遇计数”。
2. 首次使用前建议先同步精灵图鉴数据，便于统计页显示头像和匹配精灵名。
3. 识别到当前赛季的奇遇解除信号后，工具会自动更新计数。
4. 可在“统计”页管理账号，手动新增、编辑或删除奇遇和异色记录。
5. 识别到异色提示后，请前往“统计”页确认结果；确认后会写入异色记录，并重置对应精灵的当前赛季计数。
6. 需要备份统计数据时，可在“统计”页导入或导出数据；需要多设备同步时，可设置 Cloudflare R2 云同步，配置说明见 [Cloudflare R2 同步教程](docs/statistics-sync-cloudflare-r2.md)。

### 使用自动战斗

1. 在“实时”页开启“自动战斗”。
2. 进入“战斗配置”，设置技能释放顺序或自定义按键序列。
3. 根据需要选择奇遇解除后的操作策略，例如回能、继续释放技能、捕捉或等待手动操作。
4. 根据情况选择按键输入方式：
   - `PostMessage`：后台窗口消息方式，不要求游戏前台，但可能被游戏屏蔽。
   - `SendInput`：Windows 扫描码输入，要求游戏窗口处于前台。
   - `Interception`：驱动级键盘输入，要求先安装 Interception 驱动并重启电脑，游戏窗口也需要处于前台。
5. 自动战斗会在识别到合适的战斗界面后发送按键；识别到异色提示后会暂停本场自动操作。

### 安装 Interception 驱动

`Interception` 输入方式依赖系统级键盘驱动。首次切换到 `Interception` 时，RocoPilot 会自动检查驱动是否已安装：

1. 如果已安装，会直接使用该输入方式。
2. 如果未安装，会弹出安装引导窗口。
3. 点击“下载并安装”后，RocoPilot 会从 [Interception 最新 Release](https://github.com/oblitum/Interception/releases/latest) 下载 `Interception.zip`，解压并执行官方安装程序。
4. 按 Windows 提示允许管理员权限。
5. 安装命令完成后重启电脑。驱动通常需要重启后才会生效。
6. 重启后启动 RocoPilot，在“实时”页的自动战斗设置中选择 `Interception`。
7. 保持目标游戏窗口处于前台后再启动实时任务和自动战斗。

如果自动下载或安装失败，可以在安装引导窗口中打开官方下载页并手动安装：

1. 打开 [Interception 最新 Release](https://github.com/oblitum/Interception/releases/latest) 下载 `Interception.zip`。
2. 解压压缩包，进入 `command line installer` 目录。
3. 以管理员身份打开终端或命令提示符。
4. 执行安装命令：

   ```powershell
   .\install-interception.exe /install
   ```

5. 重启电脑。驱动安装后通常需要重启才能生效。

如果日志提示 `Interception 未找到可用键盘设备`，先在键盘上按任意键，再重新发送测试按键或重启 RocoPilot。需要卸载驱动时，在同一目录用管理员终端执行：

```powershell
.\install-interception.exe /uninstall
```

卸载后同样建议重启电脑。部分游戏或反作弊环境可能会禁止 Interception 驱动；如果目标环境明确禁止，请不要使用该输入方式。

### 配置热键

在“热键”页可以为信息遮罩、奇遇计数和自动战斗设置快捷切换。热键仅在实时任务运行且目标游戏窗口为焦点时触发，设置时按 `Esc` 可清除绑定。

### 查看日志

如果遇到识别失败、OCR 不可用、窗口绑定失败或自动操作不符合预期，可以打开“日志”页筛选关键信息，也可以在“设置”页打开日志目录查看本地日志文件。

<a id="notice"></a>

## 💡 说明

- 当前版本主要围绕固定画面比例和分辨率进行适配；如果游戏窗口比例、分辨率或 UI 缩放变化较大，识别效果可能下降。
- OCR 和图像识别存在误判可能，重要结果建议手动确认。
- 自动战斗会模拟键盘输入，请确认当前环境和账号风险在你的可接受范围内。
- 脚本运行期间建议保持目标窗口可见，不要频繁最小化、遮挡或切换窗口状态。
- 本工具为个人开发，仅供学习与交流使用，请遵守游戏规则和平台条款，使用风险自负。

<a id="license"></a>

## 📜 许可

本项目根据 [GPL-3.0 许可证](LICENSE) 授权发布，有关详情请参阅 LICENSE 文件。
