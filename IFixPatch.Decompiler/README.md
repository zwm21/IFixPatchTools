# IFixPatch.Decompiler

反编译 [InjectFix](https://github.com/Tencent/InjectFix) 生成的 `*.patch.bytes` 热更补丁的 CLI 工具。输入一份补丁 + Il2CppDumper 产物（`DummyDll` 或 `dump.cs`），输出人类可读的概览、反汇编或 C# 反编译结果。

依赖 `IFixPatch.Core` 共享解析与 IR。

用于分析替换方法、内部闭包、字符串常量及可恢复的方法体控制流。

## 功能

- **解析**：完整读取 13 节二进制布局（magic、外部类型/方法、字符串池、字段、VM 静态字段、匿名类、Wrapper、修复列表等），结合 `FullyParsed` 和 `TrailingBytes == 0` 检查布局对齐。
- **反汇编**：`L0015:` 标签 + `→ L0015` 跳转注释 + 异常区标注，操作数按助记符解码（`StackSpace`、`argc<<16|id`、`Ldstr`/类型/字段、`Ldc_I8/R8` 借槽、`Switch` 目标表）。
- **校验**：内置 181 个 `IFix.Core.Code` 规范名，自动比对缺失/未知名/值或名字冲突，区分完整双射与 literal 被裁剪。
- **反编译**：`Instr[]` → Cecil IL → `ICSharpCode.Decompiler` 出 C#，支持泛型实例、VM 静态字段、闭包合成与栈平衡自查。

不需要游戏文件的使用示例见 [samples/roundtrip](../samples/roundtrip/README.md)。

## 安装 / 构建

需 [.NET 10 SDK](https://dotnet.microsoft.com/download)。本节命令在 `IFixPatch.Decompiler/` 目录执行。

### 框架依赖（体积小，需本机已装 dotnet）

```bash
dotnet build IFixPatch.Decompiler.csproj -c Release
./bin/Release/net10.0/IFixPatch.Decompiler --help
dotnet publish -c Release -o ./publish
./publish/IFixPatch.Decompiler info patch.bytes --dump dump.cs
```

可用 `dotnet run -- <命令>` 构建并运行，或通过 `dotnet bin/Release/net10.0/IFixPatch.Decompiler.dll <命令>` 运行已构建产物。

### 单文件独立二进制（无 dotnet 环境）

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish-single
./publish-single/IFixPatch.Decompiler --help   # 包含目标平台 runtime
# 其他 RID：win-x64 / osx-arm64 / linux-arm64 依目标平台替换 -r
```

未启用裁剪（`PublishTrimmed=false` 为默认）。`ICSharpCode.Decompiler` 与 `Mono.Cecil` 依赖反射，裁剪会导致反编译阶段缺类型。

### dotnet tool

```bash
dotnet pack -c Release -o ../artifacts/packages
dotnet tool install --tool-path ../artifacts/tools --add-source ../artifacts/packages IFixPatch.Decompiler
../artifacts/tools/ifixpatch-decompiler --help
```

## 快速开始

```bash
# 查看帮助
IFixPatch.Decompiler --help
IFixPatch.Decompiler info --help
IFixPatch.Decompiler disasm --help
IFixPatch.Decompiler decompile --help

# 仅判断能否解析到尾（无元数据时含接口的匿名类会早停）
IFixPatch.Decompiler info patch.bytes
```

## 用法

### 子命令

| 命令 | 作用 | 元数据要求 |
|---|---|---|
| `info <PATCH>` | 概览：magic、修复列表、字符串池、匿名类、字节对齐校验（统计） | 无接口匿名类时可省略；含接口匿名类时需要元数据查询接口方法数 |
| `disasm <PATCH>` | 反汇编为可编辑 `patch.il`（`.meta`/`.opcode_map`/`.method`/标签/`.eh`，可由 Recompiler `assemble` 重建） | 同上；机器可重建 |
| `export <PATCH> -o patch.ir.json` | 导出规范 IR（内嵌 `magic`/`opcodeMap`，强约束） | 同上 |
| `decompile <PATCH> --dummy-dll <DIR> -o <out.cs>` | IL 转译并反编译为 C# | 必须 `--dummy-dll`（`dump.cs` 仅短名，无法建 CIL） |

### 通用选项

- `--dummy-dll <DIR>` — Il2CppDumper `DummyDll` 目录（首选，完整命名空间与程序集，按需加载）
- `--dump <FILE>` — 单文件 `dump.cs`（仅解析/反汇编）
- `--opcode-map <FILE>` — 单独覆盖 Code 映射（手写 `Name = 数值` 或指定某个 `dump.cs`）；缺省从元数据源自动提取
- `-o, --output <FILE>` — 输出文件；`info`/`disasm` 默认 stdout，`export`/`decompile` 必需。
  `disasm` 的输出即 `patch.il`，手改后 `IFixPatch.Recompiler assemble patch.il --base patch.bytes --dummy-dll DummyDll -o patch.new.bytes` 可重建（标签与相对偏移自动重算）
- `--json` — `info` 专属，纯 JSON 输出到 `stdout`/`-o`，诊断信息走 `stderr`
- `--version`、`-h/--help` 由框架自动提供；参数互斥或缺参时以非零退出码报错

`--dummy-dll` 与 `--dump` 互斥。

### 元数据源对比

| 源 | 能力 | 说明 |
|---|---|---|
| `--dummy-dll <dir>` | 解析 + 反汇编 + **IL 转译** | 完整 AQN，可直接建 CIL |
| `--dump <dump.cs>` | 解析 + 反汇编 | 短名易重名，无法可靠建 CIL |

元数据预检的解析率与实际转译的重载解析结果分别统计，不应互相替代。

`ShuffleInstruction` 每构建随机置换 opcode 编号，故需此表反汇编才有语义；`magic` 读取头 8 字节，不校验目标运行时兼容性。

### 示例

```bash
# 完整概览与 JSON（便于脚本对比两版本）
IFixPatch.Decompiler info patch.bytes --dump dump.cs --json -o info.json
IFixPatch.Decompiler info patch.bytes --dummy-dll DummyDll --json | jq .fixList

# 反汇编（即 il，可回编）
IFixPatch.Decompiler disasm patch.bytes --dump dump.cs -o patch.il
IFixPatch.Decompiler disasm patch.bytes --dummy-dll DummyDll -o patch.il
IFixPatch.Decompiler export patch.bytes --dummy-dll DummyDll -o patch.ir.json

# 手改后重建（recompiler 侧）
# IFixPatch.Recompiler assemble patch.il --base patch.bytes --dummy-dll DummyDll -o patch.new.bytes

# 反编译
IFixPatch.Decompiler decompile patch.bytes --dummy-dll DummyDll -o patch.decompiled.cs
IFixPatch.Decompiler decompile patch.bytes --dummy-dll DummyDll --opcode-map myMap.txt -o out.cs
```

## 原理简述

- 补丁由 `IFix.Core/CodeTranslator` 以 `BinaryWriter` 串行写出，无 TOC，以 `BinaryReader` 按序读取（7-bit 字符串 + 小端）；所有类型/方法/字段/字符串均为侧表下标，侧表保留完整名字（运行时反射定位），可读性接近无 PDB 的 release 程序集。
- 接口匿名类的 `slot` 表长度取决于接口声明的 `public` 实例方法数（与 `readSlotInfo` 的 `GetMethods` 遍历一致），需元数据源提供。
- `StackSpace`、`Ldc_I8/R8`、`Switch` 会借用后续槽位存放立即数或目标表，解码与字符串引用计数需同流程处理。

实现机制与信息边界见 [docs/Development.md](docs/Development.md)。

## 补丁结构（13 节，按序）

1 magic (u64) · 2 InterfaceBridge (string) · 3 ExternTypes · 4 Methods (`Instr[Code,Operand]` + `Eh[]`) · 5 ExternMethods · 6 InternStrings · 7 Fields · 8 VM 静态字段 · 9 AnonStoreys (fieldTypes/ctor/接口 slot/vtable) · 10 WrapperManager · 11 IdMapSuffix · 12 FixList · 13 NewClasses

`FullyParsed && TrailingBytes == 0` 为布局正确的强信号。

## 验证与局限

[合成输入回归](../CONTRIBUTING.md#构建与验证)检查离线工具行为。
实际补丁应提供同版本元数据，并分别检查完整解析、opcode 映射、转译诊断及栈检查结果。
字节往返不等于业务语义或客户端运行验证。

- `Callvirtvirt` / `Ldvirtftn2` 的目标需要额外的运行时 vtable 布局信息。
- 零引用内部方法和无读写现场的闭包字段可能无法恢复具体签名或类型。
- `magic` 仅读取；opcode literal 缺失时需要补充映射来源。

## 依赖

- .NET 10 SDK
- [Mono.Cecil](https://www.nuget.org/packages/Mono.Cecil) 0.11.6
- [ICSharpCode.Decompiler](https://www.nuget.org/packages/ICSharpCode.Decompiler) 10.1.1.8388
- [System.CommandLine](https://www.nuget.org/packages/System.CommandLine) 2.0.0-beta4.22272.1

## 参见

- 参考实现：[Tencent/InjectFix](https://github.com/Tencent/InjectFix)
- 实现说明：[docs/Development.md](docs/Development.md)

发布时保留 [第三方声明](../THIRD_PARTY_NOTICES.md) 及许可证文件。
