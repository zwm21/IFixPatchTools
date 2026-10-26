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
`repatch --patch` 不接收元数据，不适合含接口匿名类的补丁，建议先导出 IR 再重建。
所有离线检查均不代替目标客户端验证。

无需实际补丁文件的示例见 [samples/roundtrip](../samples/roundtrip/README.md)。
