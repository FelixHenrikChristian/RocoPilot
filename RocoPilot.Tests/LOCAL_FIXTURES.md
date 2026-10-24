# 本地截图回放

大幅游戏截图放在 `RocoPilot.Tests/Fixtures/legendary-local/`，该目录由 `.gitignore` 排除，不提交到 Git，也不随应用发布。测试构建时复制到测试输出目录；缺少某张素材时，对应测试显示为跳过，不会被算作通过。测试代码、本文档和应用必需的小型识别模板仍纳入版本管理。

当前素材来自 2026-10-25 的用户截图，已裁去窗口边框，保留 `(1, 44, 2048, 1152)` 客户区。文件内容为 2048×1152 PNG，测试还会缩放到 1280×720 并添加窗口偏移验证。

| 文件 | 来源及预期状态 |
| --- | --- |
| wolf-contract.png | 本轮第 1 张，银月狼王，左侧体力选中 |
| wolf-mark.png | 本轮第 2 张，银月狼王，右侧银月印记选中 |
| arena-contract.png | 本轮第 3 张，圆形石台场景，左侧体力选中 |
| arena-mark.png | 本轮第 4 张，圆形石台场景，右侧传说印记选中 |
| bones-contract.png | 本轮第 5 张，巨骨场景，左侧体力选中 |
| bones-mark.png | 本轮第 6 张，巨骨场景，右侧传说印记选中 |
| capture-wolf.png | 前一轮卡住时的截图，右侧银月印记选中，供异常状态及完整流程回放使用 |

常驻精灵的弹窗没有显示精灵名，文件按可核对的场景命名，避免仅凭截图猜测名称。

在仓库根目录运行六张新截图的回放：

```powershell
dotnet test RocoPilot.Tests/RocoPilot.Tests.csproj -p:Platform=x64 --filter "TestCategory=LocalScreenshots"
```

检查项包括捕捉确认弹窗、左右选中状态、确认按钮位置、点击后的选中状态复查。修改或补充截图后应重新构建，不要使用 `--no-build`，以免读取测试输出目录中的旧文件。日后添加素材时，同时补充文件说明和对应测试预期。
