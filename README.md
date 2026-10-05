# ALAC 音乐转换器

将 FLAC / WAV / AIFF / APE 等音频转换为 **Apple Music 兼容的 ALAC 无损格式（.m4a）**，
内置 FFmpeg 引擎，无需安装任何运行时。

---

## 一、快速使用

成品位于 **`发布\`** 目录。

1. 双击 `ALACConverter.exe`
2. 把音频文件（或整个文件夹）拖入窗口，或点「选择文件」
3. 选好输出目录，点「开始转换」

> ⚠️ **必须复制整个 `发布\` 目录**，不可只取 exe。
> 程序是免打包自包含发布，依赖同目录下的 native 库与内置引擎资源。

首次启动会释放内置 FFmpeg（约 108 MB）到 `%TEMP%\ALACConverter\`，
之后启动秒开。

---

## 二、转换规格

| 项目 | 规则 |
|---|---|
| 编码 | ALAC（Apple 无损） |
| 采样率 | 44100 Hz / 48000 Hz；**>48 kHz 自动降至 48 kHz** |
| 位深 | 16 bit；源为 24 bit 时保留 24 bit |
| 声道 | 立体声；**5.1 自动降为立体声** |
| 容器 | mp4（`.m4a`） |
| 视频轨 | 不保留（`-vn`），m4a 仅承载音频 |

**为什么需要这些约束**：Apple Music 会拒收 >48 kHz 或多声道 ALAC。
本工具会自动降采样 / 降声道以保证导入成功。

**已合规文件自动跳过**：若源文件本身已是 44100/48000 Hz 立体声 ALAC，
不会重复转码，日志提示「已是符合 Apple Music 规格的无损文件」。

### 输出校验

默认勾选「转换后校验可播性」：转换完成后自动用 ffmpeg 解码一遍，
确认无错才保留产物；失败则删除并报错。**这是原批处理脚本缺失的关键环节。**

---

## 三、目录结构

```
ALACConverter\
├── ALACConverter.csproj        项目文件
├── App.xaml                    全局资源（含无边框按钮样式）
├── App.xaml.cs                 应用入口
├── MainWindow.xaml             主界面布局
├── MainWindow.xaml.cs          界面逻辑、事件处理
├── Models\
│   └── AppleMusicSpec.cs       Apple Music 规格判定规则
│   └── AudioJob.cs             任务状态模型（INotifyPropertyChanged）
├── Services\
│   ├── ThemeService.cs         深浅色主题管理
│   ├── FfmpegLocator.cs        内置引擎定位与释放
│   ├── TranscodeEngine.cs      ffmpeg 调用、进度解析、可播性校验
│   └── ConversionQueue.cs      队列调度、并发控制
├── Resources\
│   ├── ffmpeg.exe.deflate      内置引擎（deflate 压缩）
│   ├── ALACConverter.ico      应用图标（7 尺寸：16/24/32/48/64/128/256）
│   └── ALACConverterIcon.png  256px 图标副本
├── 发布\                        ★ 成品，可直接使用
└── 第三方许可声明.txt            FFmpeg GPLv2 等许可声明
```

### 图标说明

图标为**黑胶唱片**意象：Apple Music 官方红底（`#FA243C`）+ 白色唱片与唱臂。

| 用途 | 配置方式 |
|---|---|
| exe 文件、资源管理器、任务栏固定项 | `.csproj` 的 `<ApplicationIcon>` |
| 窗口标题栏、Alt-Tab | 运行时 `LoadImage` + `AppWindow.SetIcon` |

> ⚠️ `AppWindow.SetIcon` 在 Windows App SDK 1.7 **只接受 `IconId`**（由 HICON 转换），
> 不接受 PNG 数据流或 `.ico` 路径。必须走 Win32：
> `LoadImage(LR_LOADFROMFILE)` → `Win32Interop.GetIconIdFromIcon` → `SetIcon`。

> ⚠️ 同一文件**不能同时**用 `<Resource>` 和 `<Content>`/`<None Update>` 声明，
> 后者会静默失效。用 `<None Include="..." Link="..." CopyToOutputDirectory="..." />`
> 复制副本才有效。

### 关于 `发布\` 目录里的多语言文件夹

Windows App SDK 会为 88 种语言各部署一份「卫星资源」目录
（如 `de-DE/`、`ja-JP/`），里面只有 `Microsoft.ui.xaml.dll.mui`
等**框架本地化错误提示**，与业务无关。

发布后已清理为：

| 目录 | 是否必需 | 说明 |
|---|---|---|
| `Microsoft.UI.Xaml\` | **必需** | 框架 Assets |
| `NpuDetect\` | **必需** | `NPUDetect.dll` 原生组件 |
| `zh-CN\` | 建议保留 | 中文提示 |

> ⚠️ **DLL 不可移入子目录。** 原生加载器只在 exe 同目录、`System32`、
> `PATH` 三处查找，移走会导致启动失败。这是 WinUI 3 自包含发布的硬约束，
> 不是整理习惯问题。

---

## 四、从源码构建

需要 .NET 9 SDK（`dotnet --version` 应为 9.x）。

```bash
# 免打包自包含发布（推荐，产出 209 MB）
dotnet publish -c Release -r win-x64 --self-contained true -o "发布"
```

**关键项目属性**（已写入 `.csproj`，勿删）：

```xml
<WindowsPackageType>None</WindowsPackageType>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
```

### ⚠️ 不可使用单文件发布

**WinUI 3 官方不支持单文件 exe。** 微软文档原文：

> `dotnet publish` bundles managed assemblies but **cannot produce a single-file EXE**
> for WinUI 3 apps — the native Windows App SDK runtime dependencies must remain
> as separate files.

强行加 `PublishSingleFile=true` 会产出**启动即崩溃**的 exe（exit `0xC0000409`）。

---

## 五、已知实现要点

以下是开发过程中踩过的坑，避免后续重复摸索。

### 1. 弃用 Mica 毛玻璃，改用纯色背板

Mica 会**透出用户的系统强调色**。若用户系统为暖色主题，窗口整体泛红，
与界面的中性灰卡片严重冲突。

改为纯色：深色 `#202020`、浅色 `#F3F3F3`。
**界面类程序若要求配色可控，不要用 Mica/Acrylic。**

### 2. 免打包应用不能使用 ApplicationData

`ApplicationData.Current` 会抛 `0x80073D54`（进程无程序包标识符）。
主题配置改存 `%LOCALAPPDATA%\ALACConverter\theme.cfg` 普通文本文件。

同理，**`FileOpenPicker` / `FolderPicker` / `Clipboard` 在免打包应用下需要窗口 HWND**，
须调 `InitializeWithWindow.Initialize(obj, hwnd)`。

### 3. 后台线程绝不能直接操作 UI

这是本项目**最严重的坑，共复现两次**：

```
COMException (0x8001010E): 应用程序调用一个已为另一线程整理的接口
```

FFmpeg 跑在后台线程，但属性通知（`PropertyChanged`）与日志写入
（`TextBlock.Text`）都会触碰 UI 对象。**必须切回 UI 线程**：

```csharp
// 在 UI 线程上预先捕获一次，存为 readonly 字段
private readonly DispatcherQueue? _uiQueue;

// 后台回调入口处调度
if (!_uiQueue.HasThreadAccess)
    _uiQueue.TryEnqueue(() => { /* 实际操作 UI */ });
```

⚠️ **不要写成惰性属性** `=> DispatcherQueue.GetForCurrentThread()`——
该方法在后台线程调用会**返回 null**，调度静默失效。

⚠️ 慎用 `Progress<T>`：它会捕获**构造处**的 `SynchronizationContext`，
在后台线程构造会把回调投递到错误上下文。已改用自实现的 `InlineProgress`。

### 4. ffmpeg 输出临时文件必须显式指定 `-f`

为防半成品，输出先写 `xxx.m4a.converting` 再改名。但扩展名已不是 `.m4a`，
ffmpeg 无法推断容器格式，报
`Unable to find a suitable output format`。**必须加 `-f mp4`。**

### 5. 剪贴板不需要 HWND

`InitializeWithWindow` 只用于 **Picker 类**对象。
`DataPackage` 未暴露 `IInitializeWithWindow`，强行传入会抛
`Specified cast is not valid`。标准写法：

```csharp
var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
dp.SetText(text);
Clipboard.SetContent(dp);
Clipboard.Flush();
```

### 6. 标题栏对齐：用系统实测值，禁写死像素

自绘标题栏与系统最小化/最大化/关闭按钮对不齐时，读系统尺寸：

```csharp
int height = AppWindow.TitleBar.Height;        // 通常 32
double inset = AppWindow.TitleBar.RightInset;  // 系统按钮区宽度
```

让位控件必须留在**同一布局容器内**消费 `RightInset`
（即放在 `TitleBarDrag` 里靠右对齐），移出去用 `Margin` 模拟必然错位。

### 7. 窗口尺寸按屏幕工作区自适应

```csharp
var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
var r = area.WorkArea;   // RectInt32，直接用 .Width / .Height（无 .Size）
AppWindow.Resize(new SizeInt32(
    Math.Min(1180, Math.Max(900,  r.Width  - 48)),
    Math.Min(860,  Math.Max(620, r.Height - 48))));
```

写死尺寸会在小屏或换显示器时被裁切。

### 8. 同栏两区不要用 `Auto` + `*`

`Auto` 会随内容无限增长，把 `*` 侧压没。
两区并存时**用两个 `*` 按比例分配**（如 `3*` / `2*`），或给 `Auto` 侧加 `MaxHeight`。

### 9. XAML 编译静默失败

`XamlCompiler.exe` 退出码 1 但**不报任何行号**，只有 `error MSB3073`。

已知元凶：从 WPF 抄来的属性（`Border.BorderDashArray` 在 WinUI 3 不支持）。

**排查方法**：

```python
import xml.etree.ElementTree as ET
ET.parse(p)   # 秒查嵌套标签是否配对
```

改 XAML 嵌套结构后**第一件事就是跑这个校验**，比反复试编译快得多。
注意用 `ET.parse()`，**不要用正则做标签配平**（多标签同行会严重误报）。

---

## 六、许可与贡献

本项目采用 **GNU General Public License v2**（见 `LICENSE`）。

之所以必须是 GPL v2：本程序内嵌的 FFmpeg 构建启用了 GPL 特性（`--enable-gpl`），
内嵌 GPL 组件的程序整体分发时须遵循 GPL v2 条款。这是法律约束，不是偏好。

内嵌组件的详细声明见 `第三方许可声明.txt`。

| 组件 | 版本 | 许可 |
|---|---|---|
| FFmpeg | `N-108894-g01b9abd771-20221030` | GPL v2 |
| Microsoft Windows App SDK | 1.7.250606001 | MIT |
| .NET Runtime（自包含） | 9.0 | MIT |

### 参与贡献

欢迎提交 Issue 与 Pull Request。提交代码前请注意：

- 保持 GPL v2 兼容
- 修改 `MainWindow.xaml` 嵌套结构后，先用 XML 解析器校验标签配对
  （WinUI 3 的 XAML 编译器**不报行号**，只给 `MSB3073`）
- 新增依赖前先确认其许可与 GPL v2 兼容
