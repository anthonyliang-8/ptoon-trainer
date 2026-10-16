using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Thin ReadProcessMemory/WriteProcessMemory wrapper for an x64 target.
public class Mem
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, int type, int protect);

    const int PROCESS_VM_READ = 0x10, PROCESS_VM_WRITE = 0x20, PROCESS_VM_OPERATION = 0x8, PROCESS_QUERY_INFORMATION = 0x400;

    public IntPtr Handle;
    public long Base;
    public int ModuleSize;
    public int Pid;

    public Mem(string processName)
    {
        Process[] ps = Process.GetProcessesByName(processName);
        if (ps.Length == 0) throw new Exception("Process not found: " + processName);
        Process p = ps[0];
        Pid = p.Id;
        Base = p.MainModule.BaseAddress.ToInt64();
        ModuleSize = p.MainModule.ModuleMemorySize;
        Handle = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION, false, Pid);
        if (Handle == IntPtr.Zero) throw new Exception("OpenProcess failed: " + Marshal.GetLastWin32Error());
    }

    public static bool IsPtr(long v) { return v > 0x10000 && v < 0x7FFFFFFFFFFF && (v & 7) == 0; }

    public byte[] Bytes(long addr, int size)
    {
        byte[] b = new byte[size];
        IntPtr read;
        if (addr == 0 || !ReadProcessMemory(Handle, new IntPtr(addr), b, new IntPtr(size), out read) || read.ToInt64() != size)
            return null;
        return b;
    }

    public long Ptr(long addr) { byte[] b = Bytes(addr, 8); return b == null ? 0 : BitConverter.ToInt64(b, 0); }
    public int I32(long addr) { byte[] b = Bytes(addr, 4); return b == null ? 0 : BitConverter.ToInt32(b, 0); }
    public float F32(long addr) { byte[] b = Bytes(addr, 4); return b == null ? 0 : BitConverter.ToSingle(b, 0); }
    public byte U8(long addr) { byte[] b = Bytes(addr, 1); return b == null ? (byte)0 : b[0]; }

    public string Ansi(long addr, int max)
    {
        byte[] b = Bytes(addr, max);
        if (b == null) return null;
        int n = Array.IndexOf(b, (byte)0);
        return Encoding.ASCII.GetString(b, 0, n < 0 ? max : n);
    }

    public string Wide(long addr, int maxChars)
    {
        byte[] b = Bytes(addr, maxChars * 2);
        if (b == null) return null;
        string s = Encoding.Unicode.GetString(b);
        int n = s.IndexOf('\0');
        return n < 0 ? s : s.Substring(0, n);
    }

    public bool Write(long addr, byte[] data)
    {
        IntPtr w;
        return WriteProcessMemory(Handle, new IntPtr(addr), data, new IntPtr(data.Length), out w);
    }
    public bool WriteI32(long addr, int v) { return Write(addr, BitConverter.GetBytes(v)); }
    public bool WriteF32(long addr, float v) { return Write(addr, BitConverter.GetBytes(v)); }
    public bool WritePtr(long addr, long v) { return Write(addr, BitConverter.GetBytes(v)); }

    // Allocates read/write/execute memory in the target (MEM_COMMIT|MEM_RESERVE, PAGE_EXECUTE_READWRITE).
    public long Alloc(int size) { return VirtualAllocEx(Handle, IntPtr.Zero, new IntPtr(size), 0x3000, 0x40).ToInt64(); }

    // Returns (start, size) of a named PE section of the main module, read from the remote headers.
    public bool Section(string name, out long start, out int size)
    {
        start = 0; size = 0;
        int peOff = I32(Base + 0x3C);
        int nSec = BitConverter.ToUInt16(Bytes(Base + peOff + 6, 2), 0);
        int optSize = BitConverter.ToUInt16(Bytes(Base + peOff + 20, 2), 0);
        long sec = Base + peOff + 24 + optSize;
        for (int i = 0; i < nSec; i++, sec += 40)
        {
            if (Ansi(sec, 8) == name)
            {
                size = I32(sec + 8);
                start = Base + I32(sec + 12);
                return true;
            }
        }
        return false;
    }
}
