# 第三方来源与许可

本项目按 GNU GPL v3 only 发布，详见 `LICENSE`。

- **TWS-Pods-PC** — https://github.com/Zhaoyi-ya/TWS-Pods-PC
  - 作者与贡献者：Zhaoyi-ya、相关协议逆向及移植贡献者。
  - 本项目重新实现其 `vivo/` 下 GAIA 帧、命令语义、型号能力矩阵及 wire profile。协议来源文件声明 GNU GPL-3.0-only。
  - 来源读取：2026-09-11，main 分支。能力表和协议说明保留来源归属，完整测试不等同于所有型号真机通过。
- **OppoPodsManager** — https://github.com/Zhaoyi-ya/OppoPodsManager
  - Copyright (C) 2026 Zhaoyi-ya；GPL-3.0-or-later（源仓库 LICENSE）。
  - 参考版本 `f272e9e95bb20bfb8e317af06be776c26f1eb077`。
  - 参考其 UI 功能布局、Models / Protocol / Transport / Services 分层及 Windows 蓝牙发现方式。
  - 未复制源仓库耳机图片、MiSans 字体或应用图标；本项目耳机示意图以 Avalonia 原生矢量控件绘制。
- **Avalonia 11.3.6** — https://github.com/AvaloniaUI/Avalonia，MIT。
- **.NET / Windows SDK projections** — Microsoft，MIT 等相应发行许可。
- Avalonia 的 SkiaSharp、HarfBuzzSharp 等传递依赖适用各自发行许可。发布包保留运行时自带的第三方声明；完整依赖可通过 `dotnet list src/VivoPods.App package --include-transitive` 查看。

vivo / iQOO 名称及商标归各权利人所有，用于说明设备兼容性。本项目非官方应用，与上述商标权利人无隶属关系。
