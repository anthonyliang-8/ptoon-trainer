using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Usage: UeDump.exe [outDir]   -> writes sdk.txt (classes/structs with property offsets) and objects.txt
class Dump
{
    static readonly HashSet<string> StructClasses = new HashSet<string> {
        "Class", "BlueprintGeneratedClass", "WidgetBlueprintGeneratedClass", "AnimBlueprintGeneratedClass",
        "ScriptStruct", "UserDefinedStruct", "DynamicClass" };

    static Ue ue;

    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        ue = new Ue(new Mem("pla_toon-Win64-Shipping"));
        List<long> objs = ue.AllObjects();
        Console.WriteLine("{0} objects", objs.Count);

        StringBuilder sdk = new StringBuilder(), all = new StringBuilder();
        foreach (long o in objs)
        {
            string cls = ue.ClassName(o);
            all.AppendFormat("0x{0:X} {1}\n", o, ue.FullName(o));
            if (!StructClasses.Contains(cls)) continue;
            long super = ue.M.Ptr(o + Ue.STRUCT_SUPER);
            sdk.AppendFormat("{0} {1} : {2}  // size 0x{3:X}\n", cls, ue.FullName(o).Substring(cls.Length + 1),
                super == 0 ? "-" : ue.ObjName(super), ue.M.I32(o + Ue.STRUCT_SIZE));
            for (long f = ue.M.Ptr(o + Ue.STRUCT_CHILDREN); f != 0; f = ue.M.Ptr(f + Ue.FIELD_NEXT))
            {
                string ft = ue.ClassName(f);
                if (!ft.EndsWith("Property")) continue;
                sdk.AppendFormat("    0x{0:X4} [0x{1:X}] {2,-40} {3}\n", ue.PropOffset(f),
                    ue.M.I32(f + Ue.PROP_ELEMSIZE) * ue.M.I32(f + Ue.PROP_ARRAYDIM), TypeName(f), ue.ObjName(f));
            }
        }
        File.WriteAllText(Path.Combine(outDir, "sdk.txt"), sdk.ToString());
        File.WriteAllText(Path.Combine(outDir, "objects.txt"), all.ToString());
        Console.WriteLine("Wrote sdk.txt and objects.txt to " + Path.GetFullPath(outDir));
    }

    static string TypeName(long prop)
    {
        string t = ue.ClassName(prop);
        long inner = ue.M.Ptr(prop + Ue.PROP_INNER);
        switch (t)
        {
            case "ObjectProperty": case "ClassProperty": case "WeakObjectProperty": case "SoftObjectProperty":
            case "StructProperty": case "ByteProperty": case "EnumProperty":
                return inner == 0 ? t : t + "<" + ue.ObjName(inner) + ">";
            case "ArrayProperty":
                return inner == 0 ? t : "TArray<" + TypeName(inner) + ">";
            case "BoolProperty":
                return string.Format("BoolProperty(mask 0x{0:X2} @+{1})", ue.M.U8(prop + Ue.BOOL_BYTEMASK), ue.M.U8(prop + Ue.BOOL_BYTEOFFSET));
        }
        return t;
    }
}
