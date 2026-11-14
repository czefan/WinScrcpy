<p align="center">
  <img src="assets/icon.png" alt="WinScrcpy" width="84" height="84" />
</p>

<h1 align="center">WinScrcpy</h1>

基于 `.NET Framework 4` 与 `WinForms` 编写的 Windows 桌面 `scrcpy` / `adb` 启动器，使用系统自带 `csc.exe` 编译为单文件可执行程序，无后台常驻服务。

## 功能说明

- **设备扫描**：并发执行 `adb devices -l` 与 `adb mdns services` 并归并显示设备通道，扫描阶段不发起 `adb connect`。
- **常用地址探活**：对配置文件中 `savedEndpoints` 与 mDNS 候选端口并发执行 TCP 端口连通性检测；离线 mDNS 记录自动过滤，在线的常用无线目标标记为「常用待连」。
- **按需连接与投屏**：支持单台点击 **投屏**，或勾选多台后点击 **投屏所选**。待连无线设备会先按需执行 `adb connect`，随后以无控制台子进程方式启动 `scrcpy`；全部选中项启动完成后主窗口退出。
- **无线调试配对（Android 11+）**：提供 **扫码配对** 与 **配对码配对** 两种方式，配合 `adb mdns services` 轮询与 `adb pair` 完成配对。
- **手动连接与常用地址管理**：支持完整 `IP:Port`（含 IPv6）或网段快捷拼装单次连接，并支持保存为常用地址；可通过 **更多 ▾ -> 管理常用地址…** 在表格窗口中集中编辑地址、显示别名及去重。
- **截屏到剪贴板**：通过 `adb exec-out screencap -p` 读取原始 PNG 流并写入系统剪贴板，操作后保留主窗口。
- **设备行右键终端**：右键设备行可打开 **进入 ADB Shell** 或 **打开本地 ADB 终端**（预先设置 `ANDROID_SERIAL` 与 `adb` 路径），优先使用 Windows Terminal (`wt`) 与 PowerShell 7 (`pwsh`)，不可用时回退至系统控制台与 `powershell`。

## 配置说明（`winscrcpy.json`）

首次启动若不存在 `winscrcpy.json`，程序会自动生成默认配置；也可复制 [winscrcpy.example.json](winscrcpy.example.json) 为 `winscrcpy.json` 后修改：

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

### 运行环境

1. Windows 10 或 Windows 11（内置 `.NET Framework 4.x`）。
2. 已安装 `adb` 与 `scrcpy`，并已加入系统 `PATH` 或在 `winscrcpy.json` 中指定绝对路径。

### 编译与启动

在项目根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\WinScrcpy.exe
```

编译产物为当前目录下的 `WinScrcpy.exe`。

### 内置自检与界面预览

```powershell
.\WinScrcpy.exe --selftest
.\WinScrcpy.exe --preview .\preview-out
```

## 目录结构

```text
WinScrcpy/
├─ assets/
│  ├─ app.ico                   # 应用程序图标
│  └─ icon.png                  # README 展示图标
├─ build.ps1                    # 使用系统自带 csc.exe 编译生成 WinScrcpy.exe
├─ winscrcpy.example.json       # 配置文件示例
├─ LICENSE                      # MIT 开源协议
└─ src/
   ├─ Program.cs                # 程序入口、程序集信息、DPI 感知与命令行参数分发
   ├─ Models.cs                 # 设备信息与通道状态定义
   ├─ ConfigManager.cs          # JSON 配置解析、端点规范化校验与设备配置匹配
   ├─ AdbScanner.cs             # adb devices 与 mDNS 服务解析归并
   ├─ DeviceRunner.cs           # ADB 连接/配对、TCP 探活、scrcpy 与终端启动及截屏
   ├─ QrEncoder.cs              # QR 码矩阵生成与位图渲染
   ├─ UiTheme.cs                # DPI 缩放、窗口图标加载与自绘控件
   ├─ MainForm.cs               # 主窗口界面与交互逻辑
   ├─ PairDialog.cs             # 无线调试配对对话框（扫码 / 配对码）
   ├─ ManualConnectDialog.cs    # 手动 IP:Port 直连对话框
   ├─ SavedEndpointsDialog.cs   # 常用无线地址与别名管理对话框
   └─ SelfTests.cs              # 内置单元测试与 UI 布局自检
```

## 开源协议

基于 [MIT License](LICENSE) 发布。
