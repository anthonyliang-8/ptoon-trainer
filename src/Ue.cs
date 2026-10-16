using System;
using System.Collections.Generic;

// UE 4.20 reflection reader: GNames, GUObjectArray, UClass/UProperty walking.
public class Ue
{
    // UObjectBase
    public const int OBJ_INDEX = 0x0C, OBJ_CLASS = 0x10, OBJ_NAME = 0x18, OBJ_OUTER = 0x20;
    // UField / UStruct
    public const int FIELD_NEXT = 0x28, STRUCT_SUPER = 0x30, STRUCT_CHILDREN = 0x38, STRUCT_SIZE = 0x40;
    // UProperty
    public const int PROP_ARRAYDIM = 0x30, PROP_ELEMSIZE = 0x34, PROP_OFFSET = 0x44, PROP_INNER = 0x70;
    // UBoolProperty: FieldSize, ByteOffset, ByteMask, FieldMask
    public const int BOOL_BYTEOFFSET = 0x71, BOOL_BYTEMASK = 0x72;

    const int NAMES_PER_CHUNK = 16384, OBJS_PER_CHUNK = 65536, ITEM_SIZE = 24;

    public Mem M;
    public long GNames;        // TNameEntryArray* (Chunks[] inline at offset 0)
    public long GObjectsArray; // &GUObjectArray.ObjObjects
    public bool Chunked;
    Dictionary<int, string> nameCache = new Dictionary<int, string>();

    public Ue(Mem m)
    {
        M = m;
        long ds; int dsz;
        if (!M.Section(".data", out ds, out dsz)) throw new Exception(".data section not found");
        FindGNames(ds, dsz);
        FindGObjects(ds, dsz);
    }

    void FindGNames(long ds, int dsz)
    {
        byte[] data = M.Bytes(ds, dsz);
        for (int i = 0; i + 8 <= data.Length; i += 8)
        {
            long v = BitConverter.ToInt64(data, i);
            if (!Mem.IsPtr(v)) continue;
            long chunk0 = M.Ptr(v);
            if (!Mem.IsPtr(chunk0)) continue;
            long e0 = M.Ptr(chunk0);
            if (!Mem.IsPtr(e0) || M.Ansi(e0 + 0x10, 5) != "None") continue;
            long e1 = M.Ptr(chunk0 + 8);
            if (!Mem.IsPtr(e1) || M.Ansi(e1 + 0x10, 13) != "ByteProperty") continue;
            GNames = v;
            Console.WriteLine("GNames  = 0x{0:X} (static at module+0x{1:X})", v, ds + i - M.Base);
            return;
        }
        throw new Exception("GNames not found");
    }

    void FindGObjects(long ds, int dsz)
    {
        byte[] data = M.Bytes(ds, dsz);
        for (int i = 0; i + 32 <= data.Length; i += 8)
        {
            long objs = BitConverter.ToInt64(data, i);
            if (!Mem.IsPtr(objs)) continue;
            int max = BitConverter.ToInt32(data, i + 16), num = BitConverter.ToInt32(data, i + 20);
            int maxChunks = BitConverter.ToInt32(data, i + 24), numChunks = BitConverter.ToInt32(data, i + 28);
            // Chunked layout: Objects, PreAllocatedObjects, MaxElements, NumElements, MaxChunks, NumChunks
            if (num > 1000 && num <= max && max < 10000000 &&
                numChunks == (num + OBJS_PER_CHUNK - 1) / OBJS_PER_CHUNK && maxChunks >= numChunks)
            {
                long chunk0 = M.Ptr(objs);
                if (ValidItems(chunk0)) { GObjectsArray = ds + i; Chunked = true; break; }
            }
            // Fixed layout: Objects, MaxElements, NumElements
            max = BitConverter.ToInt32(data, i + 8); num = BitConverter.ToInt32(data, i + 12);
            if (num > 1000 && num <= max && max < 10000000 && ValidItems(objs))
            {
                GObjectsArray = ds + i; Chunked = false; break;
            }
        }
        if (GObjectsArray == 0) throw new Exception("GObjects not found");
        Console.WriteLine("GObjects = module+0x{0:X} chunked={1} count={2}", GObjectsArray - M.Base, Chunked, ObjectCount);
    }

    bool ValidItems(long items)
    {
        if (!Mem.IsPtr(items)) return false;
        for (int k = 0; k < 3; k++)
        {
            long o = M.Ptr(items + k * ITEM_SIZE);
            if (!Mem.IsPtr(o) || M.I32(o + OBJ_INDEX) != k) return false;
        }
        return true;
    }

    public int ObjectCount { get { return M.I32(GObjectsArray + (Chunked ? 20 : 12)); } }

    public long ObjectAt(int idx)
    {
        long items = M.Ptr(GObjectsArray);
        if (Chunked) items = M.Ptr(items + 8 * (idx / OBJS_PER_CHUNK));
        return M.Ptr(items + ITEM_SIZE * (Chunked ? idx % OBJS_PER_CHUNK : idx));
    }

    // Reads every object pointer in bulk (one read per chunk) - much faster than ObjectAt in a loop.
    public List<long> AllObjects()
    {
        List<long> res = new List<long>();
        int n = ObjectCount;
        long objs = M.Ptr(GObjectsArray);
        for (int c = 0; c * OBJS_PER_CHUNK < n; c++)
        {
            long items = Chunked ? M.Ptr(objs + 8 * c) : objs + (long)c * OBJS_PER_CHUNK * ITEM_SIZE;
            int cnt = Math.Min(OBJS_PER_CHUNK, n - c * OBJS_PER_CHUNK);
            byte[] b = M.Bytes(items, cnt * ITEM_SIZE);
            if (b == null) continue;
            for (int k = 0; k < cnt; k++)
            {
                long o = BitConverter.ToInt64(b, k * ITEM_SIZE);
                if (o != 0) res.Add(o);
            }
        }
        return res;
    }

    public string Name(int idx)
    {
        string s;
        if (nameCache.TryGetValue(idx, out s)) return s;
        long chunk = M.Ptr(GNames + 8 * (idx / NAMES_PER_CHUNK));
        long entry = M.Ptr(chunk + 8 * (idx % NAMES_PER_CHUNK));
        if (entry == 0) s = "";
        else if ((M.I32(entry) & 1) != 0) s = M.Wide(entry + 0x10, 256);
        else s = M.Ansi(entry + 0x10, 256);
        nameCache[idx] = s;
        return s;
    }

    public string ObjName(long obj)
    {
        string n = Name(M.I32(obj + OBJ_NAME));
        int num = M.I32(obj + OBJ_NAME + 4);
        return num > 0 ? n + "_" + (num - 1) : n;
    }

    public long ClassOf(long obj) { return M.Ptr(obj + OBJ_CLASS); }
    public string ClassName(long obj) { return ObjName(ClassOf(obj)); }

    public string FullName(long obj)
    {
        string s = ObjName(obj);
        for (long o = M.Ptr(obj + OBJ_OUTER); o != 0; o = M.Ptr(o + OBJ_OUTER)) s = ObjName(o) + "." + s;
        return ClassName(obj) + " " + s;
    }

    public bool IsA(long obj, string className)
    {
        for (long c = ClassOf(obj); c != 0; c = M.Ptr(c + STRUCT_SUPER))
            if (ObjName(c) == className) return true;
        return false;
    }

    // Finds a class (native, blueprint or nativized blueprint) by short name, e.g. "BP_Sold2_Pawn_C".
    Dictionary<string, long> classes;
    public long FindClass(string name)
    {
        if (classes == null)
        {
            classes = new Dictionary<string, long>();
            foreach (long o in AllObjects())
            {
                string c = ClassName(o);
                if (c == "Class" || c == "BlueprintGeneratedClass" || c == "DynamicClass") classes[ObjName(o)] = o;
            }
        }
        long cls;
        if (!classes.TryGetValue(name, out cls)) throw new Exception("Class not found: " + name);
        return cls;
    }

    // Offset of a named property on a class, throwing if it does not exist.
    public int Off(long cls, string name)
    {
        long p = FindProp(cls, name);
        if (p == 0) throw new Exception("Property not found: " + ObjName(cls) + "." + name);
        return PropOffset(p);
    }

    public long FindObject(string fullName)
    {
        foreach (long o in AllObjects()) if (FullName(o) == fullName) return o;
        return 0;
    }

    // Finds a property by name on a UStruct (walking super structs); returns the UProperty pointer.
    public long FindProp(long ustruct, string name)
    {
        for (long s = ustruct; s != 0; s = M.Ptr(s + STRUCT_SUPER))
            for (long f = M.Ptr(s + STRUCT_CHILDREN); f != 0; f = M.Ptr(f + FIELD_NEXT))
                if (ObjName(f) == name) return f;
        return 0;
    }

    public int PropOffset(long prop) { return M.I32(prop + PROP_OFFSET); }

    // Offset of a property on the class of a live object, e.g. Offset(pawn, "Health"). -1 if missing.
    public int Offset(long obj, string name)
    {
        long p = FindProp(ClassOf(obj), name);
        return p == 0 ? -1 : PropOffset(p);
    }
}
