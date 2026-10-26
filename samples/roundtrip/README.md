# 补丁往返示例

本例为自建 IR，包含返回常量 `42` 的方法、示例类型名和 opcode 映射。
magic 和侧表均为合成数据，不用于加载到目标客户端。
以下命令在仓库根目录执行，只需 .NET 10 SDK：

```bash
mkdir -p .work/roundtrip
dotnet run -c Release --project IFixPatch.Recompiler -- validate samples/roundtrip/sample.ir.json
dotnet run -c Release --project IFixPatch.Recompiler -- repatch --ir samples/roundtrip/sample.ir.json -o .work/roundtrip/sample.bytes
dotnet run -c Release --project IFixPatch.Decompiler -- info .work/roundtrip/sample.bytes --opcode-map samples/roundtrip/opcodes.txt
dotnet run -c Release --project IFixPatch.Decompiler -- disasm .work/roundtrip/sample.bytes --opcode-map samples/roundtrip/opcodes.txt -o .work/roundtrip/sample.il
dotnet run -c Release --project IFixPatch.Recompiler -- assemble .work/roundtrip/sample.il --base .work/roundtrip/sample.bytes -o .work/roundtrip/rebuilt.bytes
cmp .work/roundtrip/sample.bytes .work/roundtrip/rebuilt.bytes
```

可修改 IR 中的 `Ldc_I4` 操作数，再重建为另一个文件并用 `info` 或 `disasm` 检查。
修改后的结果不应继续要求与原文件逐字节一致。
C# 反编译还需要声明目标成员的 DummyDll，本例不包含该输入。
