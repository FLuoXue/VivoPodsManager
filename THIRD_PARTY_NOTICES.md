# 第三方来源与许可

本项目按 GNU GPL v3 only 发布，详见 `LICENSE`。

- **TWS-Pods-PC** — https://github.com/Zhaoyi-ya/TWS-Pods-PC
  - 作者与贡献者：Zhaoyi-ya、相关协议逆向及移植贡献者。
  - 本项目重新实现其 `vivo/` 下 GAIA 帧、命令语义、型号能力矩阵及 wire profile。协议来源文件声明 GNU GPL-3.0-only。
  - 来源读取：2026-09-11，main 分支。能力表和协议说明保留来源归属，完整测试不等同于所有型号真机通过。
- **OppoPodsManager** — https://github.com/Zhaoyi-ya/OppoPodsManager
  - Copyright (C) 2026 Zhaoyi-ya；GPL-3.0-or-later（源仓库 LICENSE）。
  - 参考版本 `f272e9e95bb20bfb8e317af06be776c26f1eb077`。
  - 参考其侧栏设备列表、紧凑卡片、自动识别在线耳机、托盘单击小窗 / 双击主窗口，以及 Models / Protocol / Transport / Services 分层和 Windows 蓝牙发现方式。
  - 未复制源仓库耳机图片、MiSans 字体或应用图标；通用耳机示意图以 Avalonia 原生矢量控件绘制。
- **vivo / iQOO TWS 官方图片** — vivo 耳机 App（`com.android.vivo.tws.vivotws`）及官方机型资源包，整理日期：2026-09-13。
  - 已覆盖 27 个 vivo TWS 型号和 12 个 iQOO TWS 型号。型号与资源复用关系来自官方 `tws_config`，详见 [图片覆盖说明](docs/DEVICE_ART.md)。
  - 左右耳分别来自官方独立图片。早期型号使用 `close_1` 闭盒图；新型号从 `after_tip_circle` 官方动画中裁取已闭合的充电盒，保留透明边缘并按比例缩小。
  - [资源来源清单](docs/device-art-sources.json) 记录原 APK / ZIP 的 SHA-256、下载 URL、原文件路径、裁剪区域和输出文件校验值；资源嵌入 `src/VivoPods.App/Assets/Devices/`。
  - 图片及商标归原权利人所有，不属于本项目原创的 GPL 代码。
- **Avalonia 11.3.6** — https://github.com/AvaloniaUI/Avalonia，MIT。
- **.NET / Windows SDK projections** — Microsoft，MIT 等相应发行许可。
- Avalonia 的 SkiaSharp、HarfBuzzSharp 等传递依赖适用各自发行许可。发布包保留运行时自带的第三方声明；完整依赖可通过 `dotnet list src/VivoPods.App package --include-transitive` 查看。

vivo / iQOO 名称及商标归各权利人所有，用于说明设备兼容性。本项目非官方应用，与上述商标权利人无隶属关系。
