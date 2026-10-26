# IFixPatchTools

[InjectFix](https://github.com/Tencent/InjectFix) 热更新补丁的解析、反编译、重建与源码构建工具，基于 .NET 10。

## 项目

| 项目 | 用途 |
| --- | --- |
| `IFixPatch.Core` | 二进制读写、元数据解析、中间表示（IR）与校验 |
| `IFixPatch.Decompiler` | `info`、`disasm`、`export`、`decompile` |
| `IFixPatch.Recompiler` | `assemble`、`repatch`、`validate` |
| `IFixPatch.SourceBuilder.Core` | 源码编译、方法体移植与版本适配 |
| `IFixPatch.SourceBuilder` | `prepare`、`build`、`adapter`、`generate`、`verify` |

`patch.ir.json` 保存补丁的结构及 opcode 映射；`patch.il` 是用于编辑方法体的文本表示，
装配时需要 `--base` 提供其余侧表。

## 构建

安装 [.NET 10 SDK](https://dotnet.microsoft.com/download)。以下命令均在仓库根目录执行：

```bash
dotnet build IFixPatch.Decompiler -c Release
dotnet build IFixPatch.Recompiler -c Release
dotnet build IFixPatch.SourceBuilder -c Release
```

SourceBuilder 生成补丁还需要目标版本的元数据和指定版本的 InjectFix 源码，
见 [SourceBuilder 使用说明](IFixPatch.SourceBuilder/README.md)。

## 快速开始

不需要游戏文件的示例见 [samples/roundtrip](samples/roundtrip/README.md)。
分析实际补丁时，自行提供 `patch.bytes` 和同版本的 `DummyDll`：

```bash
dotnet run -c Release --project IFixPatch.Decompiler -- info patch.bytes --dummy-dll DummyDll
dotnet run -c Release --project IFixPatch.Decompiler -- decompile patch.bytes --dummy-dll DummyDll -o decompiled.cs

# 未修改的 IR 往返应与输入逐字节一致。
dotnet run -c Release --project IFixPatch.Decompiler -- export patch.bytes --dummy-dll DummyDll -o patch.ir.json
dotnet run -c Release --project IFixPatch.Recompiler -- validate patch.ir.json
dotnet run -c Release --project IFixPatch.Recompiler -- repatch --ir patch.ir.json -o patch.rebuilt.bytes
cmp patch.rebuilt.bytes patch.bytes

# 未修改的 IL 文本往返。
dotnet run -c Release --project IFixPatch.Decompiler -- disasm patch.bytes --dummy-dll DummyDll -o patch.il
dotnet run -c Release --project IFixPatch.Recompiler -- assemble patch.il --base patch.bytes --dummy-dll DummyDll -o patch.assembled.bytes
cmp patch.assembled.bytes patch.bytes
```

修改 IL 后应写入另一个输出文件，并重新检查解析结果和目标运行时行为；修改后的补丁不要求与原文件一致。
字节往返和栈检查用于离线验证，不代表目标客户端已接受或执行补丁。

## 文档

- [Decompiler](IFixPatch.Decompiler/README.md)：解析、反汇编、IR 导出与 C# 反编译。
- [Recompiler](IFixPatch.Recompiler/README.md)：IR 重建与 IL 装配。
- [SourceBuilder](IFixPatch.SourceBuilder/README.md)：从独立 C# 实现构建补丁。
- [贡献说明](CONTRIBUTING.md)：验证要求与 Conventional Commits 提交规范。

## 许可证

本项目代码采用 [MIT 许可证](LICENSE)，版权归 cca2878 所有。
第三方组件及其许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
目标程序集、DummyDll、原始补丁及用户提供的其他输入不包含在本项目的授权范围内。
