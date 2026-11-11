# 本地截图测试

完整游戏截图、截图回放素材及其来源索引仅保存在本地，不提交到公共仓库。

- `LocalScreenshots/`：原图归档，按日期和功能保存，保留原图，不覆盖历史素材。
- `Fixtures/`：本地测试图片，按测试代码引用的文件名放置；公开网页的 HTML 测试素材仍纳入版本管理。
- `SCREENSHOT_CATALOG.md`、`LOCAL_FIXTURES.md`：本地来源、界面状态、派生素材与验证记录，均由 Git 忽略。

应用必需的小型识别模板位于应用的 `Configuration/RecognitionAssets/`，提交前检查不含 UID、玩家昵称、聊天或其他个人信息。官网展示截图只使用演示数据。

在仓库根目录运行：

```powershell
dotnet test RocoPilot.Tests -p:Platform=x64
```

缺少本地截图时，对应回放测试报告为跳过，不计为通过；其余测试正常执行。补充素材后重新构建，避免使用 `--no-build` 读取旧输出。迁移机器时自行传输本地素材和索引，不通过公开 Git 仓库同步。

`ChallengeRecognitionResolutionTests` 覆盖凝练印记的选中状态、数量不足提示，以及跳过回合的顺延箭头与队列变化。运行：

```powershell
dotnet test RocoPilot.Tests -p:Platform=x64 --filter FullyQualifiedName~ChallengeRecognitionResolutionTests
```

- 七档 16:9 客户区（960×540 至 2560×1440）分别覆盖无边框及 `(12,30)` 客户区偏移，复用本地捕捉和战斗截图。
- 1920×1440、3440×1440 的矩阵用例是按顶部中央／右侧锚定的合成布局，只验证坐标映射，不能代替原生布局实拍。
- 箭头位置对照通过移动实拍箭头构造；不足提示的合成对照只验证模板缩放与搜索范围。独立的实拍测试读取 `flower-seed-condensed-insufficient.png`、`flower-seed-skip-turn-second.png`（2048×1152 纯客户区），缺少时跳过。
- 原生 4:3 箭头测试使用 `1920x1440.json` 和对应模板，读取 `flower-seed-skip-turn-first-4x3.png`、`flower-seed-skip-turn-second-4x3.png`（1920×1440 纯客户区），缺少时跳过。来源和裁剪记录只写入本地索引。
