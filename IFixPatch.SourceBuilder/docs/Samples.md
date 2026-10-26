# 示例

本仓库提供两组自建示例：

| 示例 | 用途 | 输入要求 |
| --- | --- | --- |
| [roundtrip](../../samples/roundtrip/README.md) | IR 重建、解析和 IL 文本往返 | .NET 10 SDK，无需游戏文件 |
| [sourcebuilder](../../samples/sourcebuilder/README.md) | 实例方法与静态 helper 的 replacement 模板 | 使用者提供同版本、已注入 IFix 的目标元数据 |

示例路径均相对仓库根目录。SourceBuilder 模板使用自定义 `Example.Calculator` 契约，
用于展示签名和文件布局；实际目标应从 `catalog.json` 中选择。

示例用于检查离线工具行为；目标客户端是否可用，需要单独验证。
