using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

/// <summary>
/// Times HasTranslation and GetMatchingIfExists over the keys the handbook asks for,
/// with handbookperf's index and with vanilla's scan, alternating in one session.
/// </summary>
public class LangLookupBench
{
    const int Rounds = 3;

    static MethodInfo hasTranslation = AccessTools.Method(typeof(TranslationService), nameof(TranslationService.HasTranslation),
        [typeof(string), typeof(bool), typeof(bool)]);
    static MethodInfo getMatchingIfExists = AccessTools.Method(typeof(TranslationService), nameof(TranslationService.GetMatchingIfExists));

    // Enough to fill any placeholder, so no lookup logs a format error
    static readonly object[] Args = Enumerable.Repeat((object)"x", 10).ToArray();

    [VsTest(TimeoutMs = 1_200_000)]
    [RequiresClient]
    public async Task Benchmark()
    {
        await OnClient();
        var keys = new List<string>();
        foreach (var c in Capi.World.Collectibles.Where(c => c?.Code != null))
        {
            string kind = c.ItemClass == EnumItemClass.Block ? "block" : "item";
            foreach (string part in new[] { "-", "desc-", "handbooktext-" }) keys.Add($"{c.Code.Domain}:{kind}{part}{c.Code.Path}");
        }

        var service = Lang.AvailableLanguages[Lang.CurrentLocale] as TranslationService;
        Assert.NotNull(service);
        var entries = (Dictionary<string, string>)AccessTools.Field(typeof(TranslationService), "entryCache").GetValue(service);
        var wildcards = (Dictionary<string, string>)AccessTools.Field(typeof(TranslationService), "wildcardCache").GetValue(service);
        var regexes = (System.Collections.IDictionary)AccessTools.Field(typeof(TranslationService), "regexCache").GetValue(service);
        Log($"Lang table: {entries.Count} exact, {wildcards.Count} wildcard, {regexes.Count} regex keys");

        bool[] exact = keys.Select(k => entries.ContainsKey(k)).ToArray();
        bool[] resolves = keys.Select(k => Lang.GetMatchingIfExists(k, Args) != null).ToArray();
        int nExact = exact.Count(x => x), nWild = resolves.Count(x => x) - nExact, nMiss = keys.Count - nExact - nWild;
        Log($"{keys.Count} keys: {nExact} exact, {nWild} found by wildcard or regex, {nMiss} missing");

        var harmony = new Harmony("handbookperf");
        var apply = AccessTools.TypeByName("handbookperf.LangLookupFix").GetMethod("Apply", BindingFlags.Public | BindingFlags.Static);
        var mod = Capi.ModLoader.GetMod("handbookperf");

        // [config][method][category] total ms over all rounds
        var totals = new Dictionary<string, double[,]> { ["handbookperf"] = new double[2, 3], ["vanilla"] = new double[2, 3] };
        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                foreach (string config in new[] { "handbookperf", "vanilla" })
                {
                    harmony.Unpatch(hasTranslation, HarmonyPatchType.Prefix, "handbookperf");
                    harmony.Unpatch(getMatchingIfExists, HarmonyPatchType.Prefix, "handbookperf");
                    if (config == "handbookperf") apply.Invoke(null, [harmony, mod]);

                    // warm-up: JIT, and the index if it needs building
                    for (int i = 0; i < 200; i++) { Lang.HasTranslation(keys[i], true, false); Lang.GetMatchingIfExists(keys[i], Args); }

                    var t = totals[config];
                    for (int i = 0; i < keys.Count; i++)
                    {
                        int cat = exact[i] ? 0 : resolves[i] ? 1 : 2;
                        long s = Stopwatch.GetTimestamp();
                        Lang.HasTranslation(keys[i], true, false);
                        t[0, cat] += Stopwatch.GetElapsedTime(s).TotalMilliseconds;
                        s = Stopwatch.GetTimestamp();
                        Lang.GetMatchingIfExists(keys[i], Args);
                        t[1, cat] += Stopwatch.GetElapsedTime(s).TotalMilliseconds;
                        if (i % 5000 == 0) await Frames.Wait(1);
                    }
                }
            }
        }
        finally
        {
            harmony.Unpatch(hasTranslation, HarmonyPatchType.Prefix, "handbookperf");
            harmony.Unpatch(getMatchingIfExists, HarmonyPatchType.Prefix, "handbookperf");
            apply.Invoke(null, [harmony, mod]);
        }

        int[] counts = [nExact, nWild, nMiss];
        string[] cats = ["exact", "wildcard/regex hit", "missing"];
        string[] methods = ["HasTranslation", "GetMatchingIfExists"];
        Log("");
        Log($"Mean per lookup, us, {Rounds} rounds:");
        Log($"{"method",-21}{"keys",-20}{"count",8}{"vanilla",11}{"handbookperf",14}{"speed-up",10}");
        for (int m = 0; m < 2; m++)
            for (int c = 0; c < 3; c++)
            {
                if (counts[c] == 0) continue;
                double v = totals["vanilla"][m, c] * 1000 / Rounds / counts[c], h = totals["handbookperf"][m, c] * 1000 / Rounds / counts[c];
                Log($"{methods[m],-21}{cats[c],-20}{counts[c],8}{v,11:F2}{h,14:F2}{v / h,9:F1}x");
            }
        double vt = 0, ht = 0;
        for (int m = 0; m < 2; m++) for (int c = 0; c < 3; c++) { vt += totals["vanilla"][m, c]; ht += totals["handbookperf"][m, c]; }
        Log($"All {keys.Count} keys, both methods, per pass: vanilla {vt / Rounds:F0} ms, handbookperf {ht / Rounds:F0} ms ({vt / ht:F1}x)");
    }
}
