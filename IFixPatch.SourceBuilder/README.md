# IFixPatch.SourceBuilder

将独立 C# 实现编译为目标版本的 IFix replacement patch。工具使用 DummyDll 元数据壳编译引用，
将方法体移植（IL graft）到目标程序集，再由版本适配器调用 InjectFix 生成补丁。

## 准备与构建

以下命令在仓库根目录执行：

```bash
dotnet build IFixPatch.SourceBuilder -c Release

# 适配器使用已核对的上游版本；vendor/ 默认不进入版本控制。
git clone https://github.com/Tencent/InjectFix.git vendor/InjectFix
git -C vendor/InjectFix checkout --detach 5ba42eb8f63fe37134a3c78690f97d8425e13588
```

需要 .NET 10 SDK；构建会还原 `Microsoft.NETFramework.ReferenceAssemblies.net471`。
补丁实现目前以 `net471` 为目标。工具在 `DOTNET_ROOT/sdk` 和 `/usr/share/dotnet/sdk` 查找 Roslyn，
使用其他 SDK 安装位置时应设置 `DOTNET_ROOT`。

目标输入由使用者提供：已注入 IFix 的目标版本 `Assembly-CSharp.dll`、同版本 `IFix.Core.dll`
及其依赖程序集。普通的未注入程序集缺少 IDMAP 信息，不能直接作为 replacement 目标。
模板见 [samples/sourcebuilder](../samples/sourcebuilder/README.md)。

使用其他 InjectFix 源码目录时，在 `adapter` 或自动构建适配器的 `generate` 命令中添加
`--injectfix-root /path/to/InjectFix`。直接使用 MSBuild 时，可传 `-p:InjectFixRoot=...`。

## 工作流

```text
profile + 目标元数据 -> prepare -> 版本壳与 catalog
补丁源码 + manifest -> build -> 方法体移植、规模检查与 build-report
prepared-profile + build-report -> generate -> patch.bytes
prepared-profile + patch.bytes -> verify -> 离线校验报告
```

`prepare` 产物可在目标输入不变时复用；修改源码后重新执行 `build`、`generate`、`verify`。

## 快速开始

将版本元数据放入 `inputs/DummyDll/`，并按样例说明核对目标签名后执行：

```bash
dotnet run --no-build -c Release --project IFixPatch.SourceBuilder -- \
  prepare samples/sourcebuilder/profile.json -o .work/prepared

dotnet run --no-build -c Release --project IFixPatch.SourceBuilder -- \
  build .work/prepared/prepared-profile.json samples/sourcebuilder/patch.json -o .work/sample

dotnet run --no-build -c Release --project IFixPatch.SourceBuilder -- \
  generate .work/prepared/prepared-profile.json .work/sample/build-report.json -o .work/sample/patch.bytes

dotnet run --no-build -c Release --project IFixPatch.SourceBuilder -- \
  verify .work/prepared/prepared-profile.json .work/sample/patch.bytes --build-report .work/sample/build-report.json
```

## 命令

| 命令 | 用法 |
| --- | --- |
| `prepare` | `prepare PROFILE -o DIR` |
| `build` | `build PREPARED_PROFILE MANIFEST -o DIR` |
| `adapter` | `adapter PREPARED_PROFILE -o DIR [--project PROJECT] [--injectfix-root DIR]` |
| `generate` | `generate PREPARED_PROFILE BUILD_REPORT [--injectfix IFix.dll \| --injectfix-root DIR] -o PATCH.bytes` |
| `verify` | `verify PREPARED_PROFILE PATCH.bytes [--build-report REPORT] [-o REPORT.json]` |

`generate` 默认构建适配器；也可先用 `adapter` 构建，再以 `--injectfix DIR/IFix.dll` 复用。
从发布目录运行时，先用 `adapter --project IFixPatch.SourceBuilder/tools/InjectFix.Cli/InjectFix.Cli.csproj`
指定源码项目，再向 `generate` 传入 `--injectfix`。
退出码：成功为 0，参数或执行错误为 1，规模检查或 `verify` 校验失败为 2。

## 输入与源码约定

[profile.json](../samples/sourcebuilder/profile.json) 中的路径相对该文件解析；
可选的 `knownPatchPath` 用于核对 magic。
[patch.json](../samples/sourcebuilder/patch.json) 以 Cecil 完整签名绑定目标和实现方法。
实例目标的实现必须是静态方法，显式首参承接 `this`：

```csharp
public static int Add(Example.Calculator self, int value)
{
    return Normalize(self.Offset + value);
}

private static int Normalize(int value) => value < 0 ? 0 : value;
```

同步、静态、非泛型的辅助方法（helper）会沿调用关系收集、去重并移植为内部方法。
helper 可跨类型、跨文件；`sources` 支持 `*.cs`、`Util/*.cs`、`**/*.cs` 等通配。
`**` 只能作为最后一个目录段，其余目录段必须为字面量；展开为空会报错。

## 产物与校验

`prepare` 输出 `prepared-profile.json`、`clean-shell/`、`compile-ref/`、适配源码、方法与字段 catalog。
`build` 输出实现 DLL/PDB、`Assembly-CSharp.grafted.dll`、`process_cfg` 和 `build-report.json`。
报告记录 binding、IDMAP slot、helper、源码 SHA-256、泛型元数据引用证据及规模诊断。
`generate` 会检查产物与源码哈希，并拒绝含 `fail` 诊断的报告。

`verify` 检查完整解析、零尾随字节、字节往返、非空 fix list 和非空方法体；
提供参考补丁时还会核对 magic。含内部方法的补丁必须提供 `--build-report`，
内部方法数必须与报告中的 helper 数一致。通过时 `errors` 为空。

`nativeSlot` 来源于目标元数据的 IDMAP，必要时应与同版本 Native 调用入口核对。
离线校验不覆盖 AOT 代码可用性、目标业务行为或客户端补丁加载。
报告、生成源码和 PDB 可能包含输入路径、符号和版本元数据，发布前应按内容筛选。

## 进一步阅读

- [能力边界](docs/Capabilities.md)
- [编写实践](docs/Practices.md)
- [示例说明](docs/Samples.md)
