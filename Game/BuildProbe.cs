using System.IO;
using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Memory;

namespace R2PrismRuntime.Game;

/// <summary>
/// Build-level diagnostics that need no FNamePool: header, module layout and every signature
/// with the bytes around each hit. This is what shows whether an untested build (Game Pass)
/// compiled the same code; the pointer chain report builds on it.
/// </summary>
internal static class BuildProbe
{
    public static void Header(ProcessMemory mem, GameTarget? target, DiagReport r, string title)
    {
        r.Line($"=== Prismforge {AppInfo.Version}: {title} ===");
        r.Line($"Time: {DateTime.Now}");
        r.Line($"Target: {target?.ModuleName ?? "?"} ({target?.Store ?? "?"}, {(target?.Tested == true ? "tested" : "UNTESTED")})");
        r.Line($"Store from path: {GameBuild.StoreFromPath(mem.ModulePath)}");
        r.Line($"Image path: {mem.ModulePath}");
        r.Line($"PID: {mem.ProcessId}");
        r.Line($"Module base: {Log.Hex(mem.ModuleBase)}");
        r.Line($"Build: {mem.Fingerprint}");
        r.Line($"Environment: {AppInfo.EnvironmentLine()}");
    }

    public static void Signatures(ProcessMemory mem, DiagReport r)
    {
        r.Line();
        r.Line("=== Signatures (Steam build: each should have exactly 1 hit unless noted) ===");
        Check(mem, r, "FNamePool", GameOffsets.AobFNamePool, null, "hit-7 = 7-byte RIP-relative lea/mov");
        Check(mem, r, "GEngine", GameOffsets.AobGEngine, null, "hit-7 = mov rcx,[rip+x] (48 8B 0D ..)");
        for (int i = 0; i < ObjectArray.Signatures.Length; i++)
        {
            var (p, m) = ObjectArray.Signatures[i];
            Check(mem, r, $"GUObjectArray #{i + 1}", p, m, "either variant may match; data-scan fallback exists");
        }
        var hook = Check(mem, r, "Stat hook", StatHook.Aob, null,
                         $"hit+0x{StatHook.SiteOffset:X} = {Hex(StatHook.Original)} (mov [r12+08],eax)");
        foreach (ulong h in hook.Take(2))
            r.Line($"    site bytes at hit+0x{StatHook.SiteOffset:X}: {Hex(mem.ReadBytes(h + StatHook.SiteOffset, 5))}" +
                   (mem.ReadBytes(h + StatHook.SiteOffset, 5).SequenceEqual(StatHook.Original) ? "  (matches)" : "  <-- DIFFERS"));
    }

    private static List<ulong> Check(ProcessMemory mem, DiagReport r, string name, byte[] pattern, bool[]? mask, string expect)
    {
        var hits = mem.AobScanAll(pattern, mask, maxHits: 8);
        r.Line($"  {name,-16} {hits.Count} hit(s)   [{ProcessMemory.FormatPattern(pattern, mask)}]");
        r.Line($"    expect: {expect}");
        foreach (ulong h in hits.Take(4))
        {
            r.Line($"    {Log.Hex(h)} (module+0x{h - mem.ModuleBase:X})");
            r.Line($"      hit-16: {Hex(mem.ReadBytes(h - 16, 16))}");
            r.Line($"      hit+0 : {Hex(mem.ReadBytes(h, Math.Min(48, pattern.Length + 24)))}");
        }
        return hits;
    }

    /// Region list of the game module, merged by identical protection (keeps the report short).
    public static void ModuleLayout(ProcessMemory mem, DiagReport r)
    {
        r.Line();
        r.Line("=== Module regions ===");
        var regions = mem.ModuleRegions();
        int shown = 0;
        for (int i = 0; i < regions.Count && shown < 40; shown++)
        {
            var (b, size, state, prot, type) = regions[i];
            int j = i + 1;
            while (j < regions.Count && regions[j].State == state && regions[j].Protect == prot) size += regions[j++].Size;
            r.Line($"  module+0x{b - mem.ModuleBase:X8}  size 0x{size:X8}  state 0x{state:X}  protect 0x{prot:X}  type 0x{type:X}  ({j - i} region(s))");
            i = j;
        }
        r.Line($"  {regions.Count} regions total; failed reads so far: {mem.FailedReads}");
    }

    /// Written when attaching fails before the pointer chain can run (usually: FNamePool not found).
    public static string WriteAttachReport(ProcessMemory mem, GameTarget? target, string why)
    {
        var r = new DiagReport(keep: true);
        Header(mem, target, r, "attach report");
        r.Line($"Reason: {why}");
        ModuleLayout(mem, r);
        Signatures(mem, r);
        return Save(r, "attach");
    }

    public static string Save(DiagReport r, string kind)
    {
        Directory.CreateDirectory(Log.LogDirectory);
        string path = Path.Combine(Log.LogDirectory, $"Prismforge_Diag_{DateTime.Now:yyyyMMdd_HHmmss}_{kind}.txt");
        File.WriteAllText(path, r.ToString());
        Log.Info($"Diagnostic written: {Path.GetFileName(path)} (Logs folder)");
        return path;
    }

    private static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));
}
