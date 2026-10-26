# IFixPatch.Recompiler

从 IR 重建补丁，或以原始补丁为基础装配编辑后的 IL。解析与导出使用 Decompiler。
以下命令在仓库根目录执行。

```bash
dotnet build IFixPatch.Recompiler -c Release

# IR 重建，保留原始输入文件。
dotnet run -c Release --project IFixPatch.Decompiler -- export patch.bytes --dummy-dll DummyDll -o patch.ir.json
dotnet run -c Release --project IFixPatch.Recompiler -- validate patch.ir.json
dotnet run -c Release --project IFixPatch.Recompiler -- repatch --ir patch.ir.json -o patch.rebuilt.bytes
cmp patch.rebuilt.bytes patch.bytes

# IL 装配。
dotnet run -c Release --project IFixPatch.Decompiler -- disasm patch.bytes --dummy-dll DummyDll -o patch.il
dotnet run -c Release --project IFixPatch.Recompiler -- assemble patch.il --base patch.bytes --dummy-dll DummyDll -o patch.assembled.bytes
```

未修改的 IL 装配结果可与原文件逐字节比较；修改后应另行检查结构和运行行为。
`assemble` 从 `--base` 保留侧表，并重算标签、分支、Switch 借用槽和异常区域位置。
`validate` 检查 IR 约束；`assemble` 另有方法体边界检查。
`repatch --patch` 可用 `--dummy-dll` 提供接口元数据；没有足够元数据、未完整解析或有尾随字节时拒绝写出。
错误索引、指令、标签和边界使操作失败，原输出文件保持不变。
`.meta`、`.extern_types`、`.intern_strings` 是只读投影，修改侧表应使用 IR。
`Ldc_I8/R8` 用 `bits64=0x...` 保留原始位模式，包括 NaN、无穷和负零。
所有离线检查均不代替目标客户端验证。

无需实际补丁文件的示例见 [samples/roundtrip](../samples/roundtrip/README.md)。
