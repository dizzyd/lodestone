using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

/// <summary>
/// The lang index answers HasTranslation and GetMatchingIfExists as vanilla does, and
/// stays on both methods with OptiTime's thread-safety transpiler on HasTranslation.
/// </summary>
public class LangLookupTests
{
    static MethodInfo hasTranslation = AccessTools.Method(typeof(TranslationService), nameof(TranslationService.HasTranslation),
        [typeof(string), typeof(bool), typeof(bool)]);
    static MethodInfo getMatchingIfExists = AccessTools.Method(typeof(TranslationService), nameof(TranslationService.GetMatchingIfExists));

    [VsTest]
    [RequiresClient]
    public async Task BothMethodsIndexedAlongsideOptiTime()
    {
        await OnClient();
        foreach (var m in new[] { hasTranslation, getMatchingIfExists })
        {
            var info = Harmony.GetPatchInfo(m);
            Log($"{m.Name}: prefixes {Describe(info?.Prefixes)}; transpilers {Describe(info?.Transpilers)}");
            Assert.True(info?.Prefixes.Any(p => p.owner == "handbookperf") == true);
        }
        if (Capi.ModLoader.IsModEnabled("optitime"))
            Assert.True(Harmony.GetPatchInfo(hasTranslation).Transpilers.Any(p => p.PatchMethod.DeclaringType?.FullName == "OptiTime.TranslationServicePatch"));
    }

    // The keys the handbook asks for: every collectible's name and description. Strings
    // with placeholders log a format error for want of args, in both passes alike.
    [VsTest(TimeoutMs = 600_000)]
    [RequiresClient]
    public async Task SameAnswersAsVanilla()
    {
        await OnClient();
        var keys = new List<string>();
        foreach (var c in Capi.World.Collectibles.Where(c => c?.Code != null))
        {
            string kind = c.ItemClass == EnumItemClass.Block ? "block" : "item";
            string code = c.Code.Domain + ":" + kind + "-" + c.Code.Path;
            keys.Add(code);
            keys.Add(c.Code.Domain + ":" + kind + "desc-" + c.Code.Path);
            keys.Add(c.Code.Domain + ":" + kind + "handbooktext-" + c.Code.Path);
        }
        Log($"{keys.Count} keys from {Capi.World.Collectibles.Count} collectibles");

        var ours = Answers(keys);
        await Frames.Wait(1);

        var harmony = new Harmony("handbookperf");
        harmony.Unpatch(hasTranslation, HarmonyPatchType.Prefix, "handbookperf");
        harmony.Unpatch(getMatchingIfExists, HarmonyPatchType.Prefix, "handbookperf");
        List<(bool, string)> vanilla;
        try
        {
            vanilla = Answers(keys);
        }
        finally
        {
            var apply = AccessTools.TypeByName("handbookperf.LangLookupFix").GetMethod("Apply", BindingFlags.Public | BindingFlags.Static);
            apply.Invoke(null, [harmony, Capi.ModLoader.GetMod("handbookperf")]);
        }

        // Applied again with OptiTime's transpiler already there, as when OptiTime's
        // StartClientSide runs first: both methods must still be indexed
        foreach (var m in new[] { hasTranslation, getMatchingIfExists })
            Assert.True(Harmony.GetPatchInfo(m).Prefixes.Any(p => p.owner == "handbookperf"));

        int found = ours.Count(a => a.Item2 != null);
        var differ = Enumerable.Range(0, keys.Count).Where(i => ours[i] != vanilla[i]).ToList();
        Log($"{found} of {keys.Count} keys resolve; {differ.Count} answers differ from vanilla");
        foreach (int i in differ.Take(10)) Log($"  {keys[i]}: ours={ours[i]} vanilla={vanilla[i]}");
        Assert.Equal(0, differ.Count);
    }

    static List<(bool, string)> Answers(List<string> keys) =>
        keys.Select(k => (Lang.HasTranslation(k, true, false), Lang.GetMatchingIfExists(k))).ToList();

    static string Describe(IEnumerable<Patch> patches) =>
        patches == null || !patches.Any() ? "none" : string.Join(", ", patches.Select(p => $"{p.owner}:{p.PatchMethod.Name}"));
}
