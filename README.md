# Codex Float for Windows

> 在桌面上一眼看清 Codex 本周还剩多少额度。

Codex Float for Windows 是一个轻量、原生、常驻桌面的 Codex 额度悬浮工具。它通过本机 Codex CLI 的 app-server 协议读取额度，不上传使用数据，也不保存登录凭证。

当前版本：**v1.0.1**

## 核心体验

- 顶层悬浮胶囊持续显示每周剩余额度
- 点击胶囊展开详情；点击展开面板任意位置回缩
- 胶囊背景按剩余额度比例填充
- 剩余大于 50% 显示绿色，21%–50% 显示橙色，20% 以下显示红色
- 支持拖动定位；拖动不会误触展开或回缩
- 逐像素 Alpha 圆角，在高 DPI 屏幕上保持平滑

## 详细数据

展开面板显示：

- 本周剩余百分比
- 下次重置的本地日期和时间
- 距离重置的相对时间
- 当前 Codex 套餐
- 可用重置机会数量
- 最近成功刷新时间

## 系统托盘

右键托盘图标可使用：

- 查看当前版本
- 把胶囊恢复到主屏幕顶部中央
- 立即刷新额度
- 启用或关闭当前用户开机启动
- 退出 Codex Float

左键单击托盘图标会直接找回胶囊并移动到主屏幕顶部中央。

## 自动刷新与状态

- 启动后立即读取
- 每 60 秒自动刷新
- 同一时间只进行一次读取
- 读取失败时显示明确原因并继续后台重试
- 支持 Codex 未安装、未登录、读取超时、协议不支持和缺少每周窗口等状态
- 不会把未知数据伪装成有效额度

## 系统要求

- Windows 10 或 Windows 11（x64）
- 已安装并登录 Codex Desktop 或 Codex CLI
- Windows 自带 .NET Framework 4.x 桌面运行环境

## 安装与使用

1. 获取 `CodexFloat.Windows.exe`。
2. 确认 Codex Desktop 或 Codex CLI 已登录。
3. 双击运行。
4. 胶囊默认出现在主屏幕顶部中央。

程序会依次查找：

1. `CODEX_FLOAT_CODEX_PATH` 指定的可执行文件
2. `%LOCALAPPDATA%\OpenAI\Codex\bin\<版本>\codex.exe`
3. 当前进程 `PATH` 中的 `codex.exe`

使用自定义 Codex 数据目录时，可设置 `CODEX_HOME`。

## 隐私与安全

- 额度通过本机 `codex app-server --stdio` 获取
- 应用不自行解析、输出或保存 `auth.json` 的内容
- `CODEX_HOME` 只传递给本机 Codex CLI
- 不包含遥测、广告或第三方分析
- 不上传额度、套餐、路径或诊断数据
- 开机启动只写入当前用户的 Windows Run 注册表项

完整说明见 [系统功能说明](docs/SYSTEM-FUNCTIONS.md)，安全问题见 [SECURITY.md](SECURITY.md)。

## 从源码构建

项目不需要额外安装 .NET SDK，使用 Windows 自带的 C# 编译器：

```powershell
.\build.ps1
```

构建产物为 `CodexFloat.Windows.exe`。

## 项目结构

```text
CodexFloat.cs                 应用、协议客户端和界面源码
app.manifest                  DPI 与 Windows 兼容性声明
build.ps1                     本地构建脚本
docs/SYSTEM-FUNCTIONS.md      完整系统功能说明
RELEASE_NOTES.md              版本发布说明
```

## 开源许可

本项目使用 [MIT License](LICENSE)。

Codex Float for Windows 是独立社区项目，与 OpenAI 没有关联。Codex 是其相应权利人的商标。
