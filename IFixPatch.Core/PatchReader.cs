using System.Text;

namespace IFixPatch.Core;

/// <summary>
/// 解析 IFix 的 *.patch.bytes。布局严格顺序、无 TOC，逐节读取。
/// magic 由目标 IFix.Core 定义；此处保留输入值，不校验目标运行时兼容性。
/// 参考序列化端 CodeTranslator.Serialize / 反序列化端 PatchManager.Load。
/// </summary>
public sealed class PatchReader(Stream s, IMetadataSource? types = null)
{
    private readonly BinaryReader _r = new(s, Encoding.UTF8, leaveOpen: true);
    private readonly IMetadataSource? _types = types;

    public static PatchFile ReadFile(string path, IMetadataSource? types = null)
    {
        using var fs = File.OpenRead(path);
        return new PatchReader(fs, types).Read();
    }

    public PatchFile Read()
    {
        var p = new PatchFile
        {
            // 1) magic（uint64，小端）+ 2) 接口桥类型
            Magic = _r.ReadUInt64(),
            InterfaceBridgeType = _r.ReadString()
        };

        // 3) 外部类型表：后续一切类型引用都是这张表的下标
        int externTypeCount = ReadCount("externTypeCount");
        for (int i = 0; i < externTypeCount; i++)
            p.ExternTypes.Add(_r.ReadString());

        // 4) 方法字节码表（按 methodId 稠密排列，不写 id）
        int methodCount = ReadCount("methodCount");
        for (int id = 0; id < methodCount; id++)
        {
            var m = new IFixMethod { Id = id };
            int codeSize = ReadCount("codeSize");
            for (int i = 0; i < codeSize; i++)
                m.Code.Add(new Instr { Code = _r.ReadInt32(), Operand = _r.ReadInt32() });

            int ehCount = ReadCount("ehCount");
            for (int i = 0; i < ehCount; i++)
            {
                m.Handlers.Add(new Eh
                {
                    HandlerType = _r.ReadInt32(),
                    CatchTypeId = _r.ReadInt32(),
                    TryStart = _r.ReadInt32(),
                    TryEnd = _r.ReadInt32(),
                    HandlerStart = _r.ReadInt32(),
                    HandlerEnd = _r.ReadInt32(),
                });
            }
            p.Methods.Add(m);
        }

        // 5) 外部方法签名表
        int externMethodCount = ReadCount("externMethodCount");
        for (int i = 0; i < externMethodCount; i++)
            p.ExternMethods.Add(ReadMethodSig());

        // 6) 字符串常量池
        int internCount = ReadCount("internCount");
        for (int i = 0; i < internCount; i++)
            p.InternStrings.Add(_r.ReadString());

        // 7) 字段表
        int fieldCount = ReadCount("fieldCount");
        for (int i = 0; i < fieldCount; i++)
        {
            var f = new FieldEntry
            {
                IsNew = _r.ReadBoolean(),
                DeclaringTypeId = _r.ReadInt32(),
                Name = _r.ReadString(),
            };
            if (f.IsNew)
            {
                f.FieldTypeId = _r.ReadInt32();
                f.CctorId = _r.ReadInt32();
            }
            p.Fields.Add(f);
        }

        // 8) VM 内静态字段表
        int staticFieldCount = ReadCount("staticFieldCount");
        for (int i = 0; i < staticFieldCount; i++)
            p.StaticFields.Add(new StaticFieldEntry
            {
                FieldTypeId = _r.ReadInt32(),
                CctorId = _r.ReadInt32(),
            });

        // 9) 匿名类/闭包表
        int anonCount = ReadCount("anonCount");
        for (int i = 0; i < anonCount; i++)
        {
            var a = new AnonStorey();
            int fieldNum = ReadCount("fieldNum");
            for (int j = 0; j < fieldNum; j++)
                a.FieldTypes.Add(_r.ReadInt32());
            a.CtorId = _r.ReadInt32();
            a.CtorParamNum = _r.ReadInt32();

            // slot info：interfaceCount==0 时干净；否则每个接口后跟的 methodId 数量
            // 由该接口“已声明 public 实例方法数”决定（对应 readSlotInfo 的 GetMethods 遍历）。
            // 该数字由元数据源提供；缺少对应信息时无法继续解析。
            int interfaceCount = ReadCount("interfaceCount");
            if (interfaceCount != 0)
            {
                a.HasInterfaces = true;
                if (_types == null)
                {
                    p.AnonStoreys.Add(a);
                    p.FullyParsed = false;
                    p.ParseNote =
                        $"匿名类 #{i} 含 {interfaceCount} 个接口，slot 表长度依赖接口方法数，" +
                        "需提供包含接口方法数的元数据源才能解析。已解析到此处停止。";
                    return p;
                }

                for (int k = 0; k < interfaceCount; k++)
                {
                    int itfId = _r.ReadInt32();
                    var itfAqn = itfId >= 0 && itfId < p.ExternTypes.Count ? p.ExternTypes[itfId] : null;
                    if (itfAqn == null ||
                        !_types.TryGetDeclaredInstanceMethodCount(itfAqn, out int methodNum))
                    {
                        p.AnonStoreys.Add(a);
                        p.FullyParsed = false;
                        p.ParseNote =
                            $"匿名类 #{i} 的接口 #{k}（type#{itfId}" +
                            $"{(itfAqn == null ? "" : " = " + DumpCsIndex.NormalizeAqn(itfAqn))}）" +
                            "在元数据源里查不到方法数，无法确定 slot 表长度。已解析到此处停止。";
                        return p;
                    }
                    a.Interfaces.Add(new AnonStorey.ItfSlots { TypeId = itfId });
                    for (int s = 0; s < methodNum; s++)
                        a.Interfaces[^1].MethodIds.Add(_r.ReadInt32());
                }
            }

            int vTableSize = ReadCount("vTableSize");
            for (int j = 0; j < vTableSize; j++)
                a.VTable.Add(_r.ReadInt32());
            p.AnonStoreys.Add(a);
        }

        // 10) Wrapper 管理器 + 11) IDMAP 后缀
        p.WrapperManagerType = _r.ReadString();
        p.IdMapSuffix = _r.ReadString();

        // 12) 修复列表（interpretMethods）：签名 + methodId
        int fixCount = ReadCount("fixCount");
        for (int i = 0; i < fixCount; i++)
            p.FixList.Add(new FixEntry { Method = ReadMethodSig(), MethodId = _r.ReadInt32() });

        // 13) 新增类列表
        int newClassCount = ReadCount("newClassCount");
        for (int i = 0; i < newClassCount; i++)
            p.NewClasses.Add(_r.ReadString());

        p.FullyParsed = true;
        // 剩余字节可提示布局错位或附加数据；恰好到达 EOF 不证明补丁语义正确。
        var stream = _r.BaseStream;
        if (stream.CanSeek)
        {
            p.TrailingBytes = stream.Length - stream.Position;
            if (p.TrailingBytes != 0)
                p.ParseNote = $"解析结束时剩余 {p.TrailingBytes} 字节未消费；可能存在附加数据或布局计数不符。";
        }
        return p;
    }

    private int ReadCount(string label)
    {
        var count = _r.ReadInt32();
        if (count < 0 || _r.BaseStream.CanSeek && count > _r.BaseStream.Length - _r.BaseStream.Position)
            throw new InvalidDataException($"Invalid {label}: {count}");
        return count;
    }

    private ExternMethodSig ReadMethodSig()
    {
        var sig = new ExternMethodSig
        {
            IsGenericInstance = _r.ReadBoolean(),
            DeclaringTypeId = _r.ReadInt32(),
            Name = _r.ReadString()
        };

        if (sig.IsGenericInstance)
        {
            int genericArgCount = ReadCount("genericArgCount");
            for (int i = 0; i < genericArgCount; i++)
                sig.GenericArgTypeIds.Add(_r.ReadInt32());

            int paramCount = ReadCount("paramCount");
            for (int i = 0; i < paramCount; i++)
            {
                var pr = new ExternMethodSig.Param { IsGeneric = _r.ReadBoolean() };
                if (pr.IsGeneric) pr.GenericName = _r.ReadString();
                else pr.TypeId = _r.ReadInt32();
                sig.Params.Add(pr);
            }
        }
        else
        {
            int paramCount = ReadCount("paramCount");
            for (int i = 0; i < paramCount; i++)
                sig.Params.Add(new ExternMethodSig.Param { TypeId = _r.ReadInt32() });
        }
        return sig;
    }
}
