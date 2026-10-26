# IFixPatch.Core

IFix 补丁的共享核心库。供 Decompiler、Recompiler 与 SourceBuilder 使用。

## 包含

- `PatchModel` / `PatchReader` / `PatchWriter` — 13 节二进制布局，完整解析与尾随字节统计
- `OpCodeMap` / `OpCodeTable` — 181 个 `IFix.Core.Code` 映射与 Shuffle 还原
- `IMetadataSource` / `DummyDllSource` / `DumpCsIndex` — `DummyDll` / `dump.cs` 元数据
- `Disassembler` / `AqnResolver` / `StackBalanceCheck`
- `IR/PatchDocument` — `patch.ir.json` 规范 IR，内嵌 `magic` 与 `opcodeMap`，`Validate()` 强约束
- `IR/PatchTextAssembly` — `patch.il` 可编辑文本装配（`.meta`/`.opcode_map`/`.method`/`Lxxxx:`/`.eh`），标签与分支偏移自动重算

## 构建

在仓库根目录执行：

```bash
dotnet build IFixPatch.Core -c Release
```

## 产物

`PatchDocument.FromPatchFile` / `ToPatchFile` 用于 `patch.bytes <-> ir.json` 转换；应使用完整元数据并比较往返字节，`PatchWriter` 与 `PatchReader` 镜像。
