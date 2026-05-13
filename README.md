<div align="center">
  <img src="RocoPilot/Assets/RocoPilot.png" width="112" alt="RocoPilot" />
  <h1>🧭 RocoPilot</h1>
  <p><strong>窗口捕获 · 奇遇统计 · 自动战斗</strong></p>

  <p>
    <a href="https://github.com/FelixHenrikChristian/RocoPilot/releases/latest"><img src="https://img.shields.io/github/v/release/FelixHenrikChristian/RocoPilot?label=version" alt="Release version" /></a>
    <a href="https://github.com/FelixHenrikChristian/RocoPilot/releases"><img src="https://img.shields.io/github/downloads/FelixHenrikChristian/RocoPilot/total?label=downloads" alt="Downloads" /></a>
    <a href="LICENSE"><img src="https://img.shields.io/github/license/FelixHenrikChristian/RocoPilot" alt="License" /></a>
    <img src="https://img.shields.io/badge/platform-Windows-0078D4?logo=windows" alt="Windows" />
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

RocoPilot 是一款 Windows 桌面辅助工具，面向游戏《洛克王国·世界》相关战斗场景，提供实时画面识别、奇遇统计、自动战斗、异色提醒和日志诊断等功能。

<p align="center">
  <img src="RocoPilot/Assets/LaunchPageCover.png" width="820" alt="RocoPilot 启动页封面" />
</p>

<a id="features"></a>

## ✨ 功能

|  | 功能 | 说明 |
| --- | --- | --- |
| 🔎 | 实时识别 | 自动识别当前画面状态，为统计、遮罩和自动操作提供基础。 |
| 🪟 | 信息遮罩 | 在游戏窗口旁显示运行状态、计数和识别结果，可锁定或重置位置。 |
| 📊 | 奇遇统计 | 记录当前赛季奇遇/污染次数，帮助跟踪每只精灵的进度。 |
| ✨ | 异色提醒 | 识别到异色提示后暂停自动操作，并进入待确认流程。 |
| ⚔️ | 自动战斗 | 支持技能释放顺序、自定义按键序列、回能、捕捉等常见战斗策略。 |
| 📚 | 图鉴同步 | 同步精灵图鉴与头像数据，让统计记录更直观。 |
| 🧾 | 日志诊断 | 内置日志页，方便查看识别、运行和异常信息。 |
| 🚀 | 自动更新 | 支持在线更新器和完整安装包。 |

<a id="usage"></a>

## 🚀 使用

### 下载安装

1. 打开 [Releases](https://github.com/FelixHenrikChristian/RocoPilot/releases/latest) 页面。
2. 下载 `RocoPilot-Setup-v*.exe`，参考提示进行安装。
3. 安装后启动 RocoPilot，并保持目标游戏窗口处于正常状态。

### 启动识别

1. 在“启动”页选择截图方式和 OCR 识别方法。
2. 推荐优先使用 `Windows Graphics Capture` 与 `PaddleOCR V5`。
3. 需要观察识别范围时，可开启“识别区域遮罩”。
4. 需要查看运行状态时，可开启“信息遮罩窗口”。
5. 点击“启动”，确认工具已正常绑定窗口并开始识别。

### 使用奇遇统计

1. 在“实时”页开启“奇遇统计”。
2. 首次使用前建议同步精灵图鉴数据。
3. 识别到奇遇/污染提示后，工具会自动更新统计。
4. 识别到异色提示后，请前往“统计”页确认结果；确认后会写入异色记录，并重置对应精灵的当前赛季计数。

### 使用自动战斗

1. 在“实时”页开启“自动战斗”。
2. 进入“战斗配置”，设置技能释放顺序或自定义按键序列。
3. 根据需要选择奇遇解除后的操作策略，例如回能、继续战技、捕捉或等待手动操作。
4. 自动战斗会在识别到合适的战斗界面后发送按键；识别到异色提示后会暂停本场自动操作。

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
