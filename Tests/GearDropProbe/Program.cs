using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Model = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model;
using Material = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Material;

// Read-only diagnostic. No remote threads, native calls in the game, or process memory writes.
unsafe class Program
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    static nint handle;
    static byte[] Read(nint address, int size)
    {
        var bytes = new byte[size];
        if (!ReadProcessMemory(handle, address, bytes, (nuint)size, out var read) || read != (nuint)size)
            throw new InvalidOperationException($"Could not read 0x{address:X} ({size} bytes)");
        return bytes;
    }
    static T Read<T>(nint address) where T : unmanaged => MemoryMarshal.Read<T>(Read(address, sizeof(T)));
    static string Path(nint resource)
    {
        if (resource == 0) return "<null>";
        var address = resource + Marshal.OffsetOf<ResourceHandle>("FileName");
        var length = Read<ulong>(address + 16); var capacity = Read<ulong>(address + 24);
        if (length > 4096) return "<invalid length>";
        return Encoding.UTF8.GetString(Read(capacity < 16 ? address : Read<nint>(address), (int)length));
    }
    static void Main(string[] args)
    {
        if (args.Contains("--metadata"))
        {
            foreach (var type in new[] { typeof(Human), typeof(FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer) })
            {
                Console.WriteLine(type.FullName);
                foreach (var method in type.GetMethods().Where(m => m.Name == "Instance"))
                    foreach (var attr in method.GetCustomAttributesData()) Console.WriteLine(attr);
                foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)) Console.WriteLine($"{field.Name}: {field.FieldType} offset={Marshal.OffsetOf(type, field.Name)}");
            }
            return;
        }
        using var process = Process.GetProcessesByName("ffxiv_dx11").Single();
        handle = OpenProcess(0x10, false, process.Id);
        try
        {
            nint manager = 0;
            if (args.Contains("--watch"))
            {
                var module = process.MainModule!;
                var data = Read(module.BaseAddress, module.ModuleMemorySize);
                var matches = new List<int>();
                for (var i = 0; i < data.Length - 9; i++)
                    if (data[i] == 0x48 && data[i + 1] == 0x8D && data[i + 2] == 0x35 && data[i + 7] == 0x81 && data[i + 8] == 0xFA)
                        matches.Add(i);
                if (matches.Count != 1) throw new InvalidOperationException($"Ambiguous manager signature: {matches.Count}");
                var offset = matches[0];
                manager = module.BaseAddress + offset + 7 + BitConverter.ToInt32(data, offset + 3);
            }
            var seen = new HashSet<string>();
            var timer = Stopwatch.StartNew();
            do
            {
                var addresses = manager == 0
                    ? args.Select(arg => (nint)Convert.ToInt64(arg.Replace("0x", ""), 16)).ToArray()
                    : new[] { 0 }.Concat(Enumerable.Range(400, 98)).Select(i => Read<nint>(manager + 32 + i * sizeof(nint))).Where(a => a != 0).ToArray();
                foreach (var address in addresses)
                try
                {
                var obj = Read<GameObject>(address);
                if (obj.DrawObject == null) continue;
                var cb = Read<CharacterBase>((nint)obj.DrawObject);
                var report = new StringBuilder();
                foreach (var slot in new[] { 1, 3 })
                {
                    if (cb.Models == null || slot >= cb.SlotCount) continue;
                    var mp = Read<nint>((nint)cb.Models + slot * sizeof(nint));
                    if (mp == 0) continue;
                    var model = Read<Model>(mp);
                    report.AppendLine($"actor=0x{address:X}, index={obj.ObjectIndex}, slot={slot}, model={Path((nint)model.ModelResourceHandle)}");
                    for (var i = 0; i < model.MaterialCount; i++)
                    {
                        var mat = Read<nint>((nint)model.Materials + i * sizeof(nint));
                        report.AppendLine($"  material[{i}]={(mat == 0 ? "<null>" : Path((nint)Read<Material>(mat).MaterialResourceHandle))}");
                    }
                }
                if (report.Length > 0 && seen.Add(report.ToString())) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}\n{report}");
                }
                catch (InvalidOperationException) { /* Actor/redraw disappeared between external reads. */ }
                if (manager != 0) Thread.Sleep(100);
            }
            while (manager != 0 && timer.Elapsed.TotalMinutes < 5);
        }
        finally { CloseHandle(handle); }
    }
}
