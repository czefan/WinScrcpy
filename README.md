<p align="center">
  <img src="assets/icon.png" alt="WinScrcpy" width="128" height="128" />
</p>

<h1 align="center">WinScrcpy</h1>

基于 `.NET 10` 与 `WinForms` 编写的 Windows 桌面 `scrcpy` / `adb` 启动器，无后台常驻服务。

## 功能说明

- **设备扫描**：异步分阶段并发执行 `adb devices -l` 与 `adb mdns services` 并归并显示设备通道，扫描阶段不发起 `adb connect`。
- **常用地址探活**：对配置文件中 `savedEndpoints` 与 mDNS 候选端口并发执行有限并发度的 TCP 端口连通性检测；离线 mDNS 记录自动过滤，在线的常用无线目标标记为「常用待连」。
- **按需连接与投屏**：支持单台点击 **投屏**，或勾选多台后点击 **投屏所选**。待连无线设备会先按需执行 `adb connect`，随后以脱离窗口子进程启动 `scrcpy` 并写入独立启动日志（`%TEMP%\WinScrcpy\logs`）；全部选中项启动确认后主窗口退出，若有失败或取消则保留窗口提示错误细节。
- **无线调试配对（Android 11+）**：提供 **扫码配对** 与 **配对码配对** 两种方式，配合 `adb mdns services` 轮询与 `adb pair` 完成配对。
- **手动连接与常用地址管理**：支持完整 `IP:Port`（含 IPv6）或网段快捷拼装单次连接，并支持保存为常用地址；可通过 **更多 ▾ -> 管理常用地址…** 在表格窗口中集中编辑地址、显示别名及去重。
- **截屏到剪贴板**：通过 `adb exec-out screencap -p` 读取原始 PNG 流并写入系统剪贴板，操作后保留主窗口。
- **设备行右键终端**：右键设备行可打开 **进入 ADB Shell** 或 **打开本地 ADB 终端**（预先设置 `ANDROID_SERIAL` 与 `adb` 路径），优先使用 Windows Terminal (`wt`) 与 PowerShell 7 (`pwsh`)，不可用时回退至系统控制台与 `powershell`。
- **高 DPI 与工具解析**：启用 WinForms `PerMonitorV2` 高 DPI 适配；外部工具固定按「显式配置绝对路径 → 程序目录 → 系统 `PATH`」顺序查找，并向 `scrcpy` 透传同一份 `ADB` 环境变量。

## 配置说明（`winscrcpy.json`）

程序采用便携模式，从 `WinScrcpy.exe` 所在目录读取 `winscrcpy.json`（若从旧版迁移或切换构建输出目录，需将原 `winscrcpy.json` 复制到对应产物目录）。保存配置时会先写同目录临时文件再原子替换目标并保留 `winscrcpy.json.bak` 备份；若原配置文件损坏，程序会保留损坏文件并在状态栏提示警告，且在修复或移除损坏文件前阻止自动覆盖。首次启动若不存在 `winscrcpy.json`，会自动生成默认配置；也可复制 [winscrcpy.example.json](winscrcpy.example.json) 为 `winscrcpy.json` 后修改：

```json
{
  "adbPath": "adb",
  "scrcpyPath": "scrcpy",
  "globalArgs": [
    "--stay-awake"
  ],
  "savedEndpoints": [
    "192.168.1.50:5555"
  ],
  "devices": {
    "0123456789ABCDEF": {
      "alias": "主力机",
      "args": []
    }
  }
}
```

- `adbPath` / `scrcpyPath`：`adb` 与 `scrcpy` 可执行文件路径；填写 `"adb"` / `"scrcpy"` 时从系统 `PATH` 查找。
- `globalArgs`：默认传递给 `scrcpy` 的全局参数数组。
- `savedEndpoints`：常用无线调试地址列表，未指定端口时默认补全 `:5555`。
- `devices`：按硬件序列号、`adb` 序列号、端点或 mDNS 基础名匹配（不区分大小写）：
  - `alias`：列表展示与 `scrcpy` 窗口标题所使用的别名；留空时自动显示设备型号或序列号。
  - `args`：该设备的独立 `scrcpy` 参数数组；为空数组 `[]` 时继承 `globalArgs`。

## 快捷键与鼠标操作

- `F5`：重新扫描设备
- `Esc`：关闭当前窗口（表格单元格处于编辑状态或配对进行中时除外）
- 设备行右键：打开 **进入 ADB Shell** / **打开本地 ADB 终端** 菜单
- 手动连接或配对码输入框按 `Enter`：提交连接或配对

## 编译与运行

### 运行与构建环境

1. Windows 10 或 Windows 11（x64）。
2. 从源码构建需要安装 **.NET 10 SDK**；运行普通构建产物需要 **.NET 10 Desktop Runtime**，自包含单文件发布产物无需额外安装运行时。
3. 已安装 `adb` 与 `scrcpy`，并已加入系统 `PATH` 或在 `winscrcpy.json` 中指定绝对路径。

### 构建、发布与启动

在项目根目录执行：

```powershell
# 普通构建（输出到 bin\Release\net10.0-windows\WinScrcpy.exe）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\bin\Release\net10.0-windows\WinScrcpy.exe
```

单文件发布与可选图标生成（按发布类型分别输出到 `bin\Release\net10.0-windows\win-x64\publish-framework-dependent\WinScrcpy.exe` 与 `publish-selfcontained\WinScrcpy.exe`）：

```powershell
# 框架依赖单文件发布（需目标机具备 .NET 10 Desktop Runtime）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Publish

# 自包含单文件发布（无需预装 .NET 运行时）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Publish -SelfContained

# 打包便携发布压缩包（输出到 bin\Release\WinScrcpy-win-x64-selfcontained.zip）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Publish -SelfContained -Package

# 可选：重新生成 assets\app.ico 与 assets\icon.png
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\generate-icon.ps1 -Force
```

也可直接使用 `dotnet build .\WinScrcpy.csproj -c Release` 或 `dotnet publish .\WinScrcpy.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`。

### 自动化测试

```powershell
# 构建后直接运行独立测试项目
.\build.ps1 -Test

# 或直接使用 dotnet test 运行
dotnet test .\WinScrcpy.sln -c Release

# 运行性能与产物体积基线脚本（默认先构建并输出到 bin\Release\baseline-report.md；可用 -NoBuild 跳过构建）
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\measure-baseline.ps1
```

## 目录结构

```text
WinScrcpy/
├─ assets/
│  ├─ app.ico                     # 应用程序图标
│  └─ icon.png                    # README 展示图标
├─ scripts/
│  ├─ generate-icon.ps1           # 可选图标生成脚本
│  └─ measure-baseline.ps1        # 性能与产物体积基线脚本
├─ WinScrcpy.sln                  # 解决方案（应用项目 + 测试项目）
├─ WinScrcpy.csproj               # .NET 10 WinForms 应用项目文件
├─ build.ps1                      # dotnet build / publish / test 入口
├─ winscrcpy.example.json         # 配置文件示例
├─ LICENSE                        # MIT 开源协议
├─ src/
│  ├─ Program.cs                  # 程序入口与异常边界
│  ├─ Models.cs                   # 设备信息与通道状态定义
│  ├─ ConfigManager.cs            # JSON 配置解析、原子保存与备份、端点校验与匹配
│  ├─ ProcessRunner.cs            # 外部工具路径解析与异步进程/流读取封装
│  ├─ DetachedProcessLauncher.cs  # Win32 脱离进程启动与日志句柄监控
│  ├─ AdbScanner.cs               # adb devices 与 mDNS 服务解析归并
│  ├─ AdbClient.cs                # ADB 查询、连接、断开、配对与截屏
│  ├─ DeviceDiscoveryService.cs   # 设备扫描编排、异步 TCP 探活与信息补全
│  ├─ ScrcpyLauncher.cs           # scrcpy 参数构造、启动监控与批量投屏结果
│  ├─ TerminalLauncher.cs         # Windows Terminal / PowerShell 启动与脚本构造
│  ├─ QrEncoder.cs                # QR 码矩阵生成与位图渲染
│  ├─ UiTheme.cs                  # PerMonitorV2 DPI 换算、窗口图标与自绘控件
│  ├─ MainForm.cs                 # 主窗口界面与用户交互
│  ├─ PairDialog.cs               # 无线调试配对对话框（扫码 / 配对码）
│  ├─ ManualConnectDialog.cs      # 手动 IP:Port 直连对话框
│  └─ SavedEndpointsDialog.cs     # 常用无线地址与别名管理对话框
└─ tests/
   └─ WinScrcpy.Tests/            # 独立 xUnit 测试项目（含 STA UI 与预览验证）
```

## 开源协议

基于 [MIT License](LICENSE) 发布。
