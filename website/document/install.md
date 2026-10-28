---
title: 安装
description: RocoPilot 下载与安装说明。
---

# 安装

RocoPilot 通过 Windows 安装包发布。

## 下载

前往 [下载页](/download/) 下载 `RocoPilot-Setup.exe`。

下载页提供两个来源：

- `GitHub Release`：项目的标准发布来源，也可查看历史版本。
- `Cloudflare R2`：通常更适合 GitHub 访问较慢的网络环境。

::: tip 建议
两个来源提供的是同一个安装包。若 GitHub 访问较慢，可以优先使用 Cloudflare R2。
:::

## 应用安装

1. 下载 `RocoPilot-Setup.exe`。
2. 双击安装包，更改或保持默认安装目录，按提示完成安装。
3. 从开始菜单或桌面快捷方式打开 RocoPilot。

## 驱动安装

只使用识别与统计时，无需安装输入驱动。自动战斗、花种挑战、传说精灵挑战等功能需要模拟输入，当前使用 `Interception`。

### 自动安装（推荐）

1. 在“启动”页选择 `Interception`。未安装驱动时，应用会显示安装引导。
2. 点击“下载并安装”，按系统提示允许管理员权限。
3. 安装完成后重启电脑，再打开游戏和 RocoPilot。
4. 在“启动”页确认输入方式，保持游戏前台，点击“启动”，再开启实时功能或独立任务。

::: note 注意
 `PostMessage` 和 `SendInput` 输入方式已失效，请勿选择。
:::


### 手动安装或卸载驱动（可选）

从 [Interception Releases](https://github.com/oblitum/Interception/releases/latest) 下载 `Interception.zip`，解压并进入 `command line installer` 目录，以管理员身份运行：

```powershell
.\install-interception.exe /install
```

完成后重启电脑。若日志提示“Interception 未找到可用键盘设备”，先按任意键，再重试或重启 RocoPilot。
如果需要卸载驱动，可以在同一目录用管理员终端执行并重启：

```powershell
.\install-interception.exe /uninstall
```

## 更新

RocoPilot 支持通过“设置”页检查更新。

- 打开 `设置`。
- 在`关于`区域点击 `检查更新`。
- 如果检测到新版本，可通过内置更新器完成更新，或前往下载页下载新版本安装包。

::: tip 提示
若应用内更新检查失败，可通过应用安装目录的 `RocoPilot.update.exe` 手动更新，或重新下载完整安装包。更新前建议导出重要统计。
:::

