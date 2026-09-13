# 耳机图片覆盖

首页使用官方独立左耳、右耳和闭合充电盒图片，与对应的电量和状态放在同一列。切换型号后更新三张图片；断开后与旧电量一起置灰。

已覆盖项目支持的全部 **39 个 TWS 型号**：27 个 vivo、12 个 iQOO。官方配置明确复用的资源共用同一套文件，最终嵌入 111 张透明 PNG，约 4.55 MiB。

| 品牌 | 已覆盖型号 |
|---|---|
| vivo TWS | 1、2、2e、3、3 Pro、3e、3i、4、4 HiFi、5、5 HiFi、5 Pro、5e、5i |
| vivo TWS Air | Air、Air Pro、Air2、Air3、Air3 Pro |
| vivo 其他 TWS | Neo、X1、A1、A1 Pro、A2、A3、A4、A5 |
| iQOO TWS | 1、1e、1i、2、5、5e、5i |
| iQOO TWS Air | Air、Air Pro、Air2、Air3、Air3 Pro |

每个型号选用一套固定官方配色，Air3 Pro 沿用 model 169 的深色。当前不根据协议回报猜测实际配色。`Headphones` 通用名称及未识别型号保留通用矢量示意图；图片覆盖不增加协议能力声明。

图片预览：[27 款 vivo TWS](images/vivo-tws-catalog.jpg) · [12 款 iQOO TWS](images/iqoo-tws-catalog.jpg)。

## 来源与复现

旧型号图片来自官方 APK 的 `assets/flash_connect/<file_model>/left_1`、`right_1`、`close_1`。新型号的左右耳来自官方机型包；闭盒图来自 `after_tip_circle` 的首帧，按透明分隔区裁出最右侧的闭合充电盒。未镜像生成左右耳，未重绘盒盖。

[来源清单](device-art-sources.json) 包含全部源文件、源包及输出校验值和裁剪坐标。[应用图片目录](../src/VivoPods.App/Assets/Devices/catalog.json) 是运行时型号映射，名称匹配复用项目的型号归一化规则。

已提取的 APK、官方配置和下载包放在 `.vs/VivoTwsApp/` 时，安装 Pillow 后运行：

```powershell
python scripts/import-device-art.py --source .vs/VivoTwsApp
```

脚本校验机型 ZIP 的 SHA-256，导入图片并生成映射及来源清单。图片最长边不超过 512 像素，保留透明通道；应用仅加载内嵌 PNG，不加载原始动画或访问资源服务器。位图按需缓存，退出时释放。

桌面 `--smoke` 验证所有 TWS 型号的内嵌图片解码，以及不同型号、浅深主题、最小窗口、断开状态和未知型号的实际显示。
