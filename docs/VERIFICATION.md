# 验证记录

验证日期：2026-09-11。环境：Windows 11（build 26200）、.NET SDK 9.0.312、x64。

## 软件验证

| 项目 | 证据 | 结果 |
|---|---|---|
| 整体编译 | `dotnet build VivoPodsManager.sln -c Release` | 通过，0 警告 / 0 错误 |
| 协议和会话 | `dotnet run --project tests/VivoPods.Tests -c Release` | 55 项通过 |
| 桌面启动与交互 | `--smoke --output artifacts/ui` | 通过 |
| 浅色 / 深色及五个页面 | `artifacts/ui/*.png` | 已实际渲染并检查概览界面 |
| Windows 蓝牙设备发现 | `--probe --connect` | 发现 3 个经典设备和 1 个 BLE 入口 |

自动验证包含：参考抓包精确字节、每个拆包位置、粘包、XOR 错误、扩展长度、GATT 编码、电量未知值、佩戴位、二进制固件、完整多连接表、通知代际差异、有效握手、设置回读、超时后不伪造成功、断开置灰、切换会话。桌面烟测还执行了噪声切换、EQ、左右耳长按配置、双击设置、多设备切换以及后台电量刷新不覆盖未提交选项。

## 尚待真机验证

本机发现 `vivo TWS Air3 Pro`、`iQOO TWS Air`、`iQOO TWS Air Pro` 的配对记录，但检测时系统均报告未连接。因此尚未证明本项目能从当前真实耳机收到握手、电量和控制确认。

需要取出耳机并在 Windows 蓝牙设置中连接后执行：

```powershell
dotnet run --project tests/VivoPods.Tests -c Release -- --probe --connect
```

真机验收包括：

1. 初始化完成，读到真实左右耳和盒电量、固件版本。
2. 三档噪声切换后实际音效及回报一致。
3. 官方 App 或耳机手势改动后，桌面状态刷新。
4. 摘下 / 佩戴 / 入盒状态及断线重新连接。
5. 对应型号的 EQ、手势、查找及多设备功能。

演示与协议回归不能替代上述硬件验证。不同系列的实验功能也仍需分别验证。
