using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

/// <summary>
/// What Attribute Rendering Library's GuiDialogHandbook.FilterItems prefix costs per
/// search, against vanilla's FilterItems (what ArlFilterFix leaves when it removes the
/// prefix) and against ARL's prefix with its reflective CountMatches calls made direct.
/// Needs ARL in the session's mods.
/// </summary>
public class ArlFilterBench
{
    const int Rounds = 3;
    const string ArlId = "attributerenderinglibrary";
    const string BenchId = "handbookperf.arlbench";

    static readonly string[] Searches = ["", "c", "co", "cop", "copp", "coppe", "copper", "oak plank", "bread", "zzzz"];

    static MethodInfo filterItems = AccessTools.Method(typeof(GuiDialogHandbook), "FilterItems");
    static System.Func<GuiDialogHandbook, string, Regex, int> vanillaCount;

    [VsTest(TimeoutMs = 1_200_000)]
    [RequiresClient]
    public async Task Benchmark()
    {
        await OnClient();
        var patchType = AccessTools.TypeByName("AttributeRenderingLibrary.HarmonyPatches.Handbook.GuiDialogHandbook_FilterItems_Patch");
        if (patchType == null) Skip("ARL is not installed");
        MethodInfo arlPrefix = patchType.GetMethod("Prefix");
        vanillaCount = AccessTools.MethodDelegate<System.Func<GuiDialogHandbook, string, Regex, int>>(AccessTools.Method(typeof(GuiDialogHandbook), "CountMatches"));

        var dialog = (GuiDialogHandbook)AccessTools.Field(typeof(ModSystemSurvivalHandbook), "dialog").GetValue(Capi.ModLoader.GetModSystem<ModSystemSurvivalHandbook>());
        var loading = AccessTools.Field(typeof(GuiDialogHandbook), "loadingPagesAsync");
        while ((bool)loading.GetValue(dialog)) await Frames.Wait(10);
        var searchText = AccessTools.Field(typeof(GuiDialogHandbook), "currentSearchText");
        var shown = (List<IFlatListItem>)AccessTools.Field(typeof(GuiDialogHandbook), "shownHandbookPages").GetValue(dialog);
        var all = (List<GuiHandbookPage>)AccessTools.Field(typeof(GuiDialogHandbook), "allHandbookPages").GetValue(dialog);

        var before = Harmony.GetPatchInfo(filterItems);
        Log($"{all.Count} pages; FilterItems prefixes at start: {string.Join(", ", before?.Prefixes.Select(p => p.owner) ?? [])}");

        // handbookperf's empty-search listing runs ahead of both and would answer the empty search
        Patch ours = before?.Prefixes.FirstOrDefault(p => p.owner == "handbookperf");
        if (ours != null) new Harmony("handbookperf").Unpatch(filterItems, HarmonyPatchType.Prefix, "handbookperf");

        var configs = new (string name, Action apply)[]
        {
            ("ARL's prefix", () => new Harmony(ArlId).Patch(filterItems, prefix: new HarmonyMethod(arlPrefix))),
            ("vanilla (prefix removed)", () => { }),
            ("ARL's, direct CountMatches", () =>
            {
                new Harmony(BenchId).Patch(arlPrefix, transpiler: new HarmonyMethod(typeof(ArlFilterBench), nameof(DirectCount)));
                new Harmony(ArlId).Patch(filterItems, prefix: new HarmonyMethod(arlPrefix));
            }),
        };

        var times = configs.ToDictionary(c => c.name, _ => new double[Searches.Length]);
        var results = configs.ToDictionary(c => c.name, _ => new string[Searches.Length]);
        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                foreach (var (name, apply) in configs)
                {
                    Reset(arlPrefix);
                    apply();
                    for (int s = 0; s < Searches.Length; s++)
                    {
                        searchText.SetValue(dialog, Searches[s]);
                        long t = Stopwatch.GetTimestamp();
                        dialog.FilterItems();
                        times[name][s] += Stopwatch.GetElapsedTime(t).TotalMilliseconds / Rounds;
                        if (round == 0) results[name][s] = string.Join(",", shown.Select(p => ((GuiHandbookPage)p).PageCode));
                        await Frames.Wait(1);
                    }
                }
            }
        }
        finally
        {
            // Back to what the session had: ARL's prefix only if it was there
            Reset(arlPrefix);
            if (before?.Prefixes.Any(p => p.owner == ArlId) == true) new Harmony(ArlId).Patch(filterItems, prefix: new HarmonyMethod(arlPrefix));
            if (ours != null) new Harmony("handbookperf").Patch(filterItems, prefix: new HarmonyMethod(ours.PatchMethod) { priority = ours.priority, before = ours.before });
            searchText.SetValue(dialog, "");
            dialog.FilterItems();
        }

        Log("");
        Log($"ms per FilterItems call, mean of {Rounds} rounds:");
        Log($"{"search",-12}{"shown",7}" + string.Concat(configs.Select(c => $"{c.name,28}")));
        for (int s = 0; s < Searches.Length; s++)
        {
            int count = results[configs[0].name][s].Length == 0 ? 0 : results[configs[0].name][s].Count(ch => ch == ',') + 1;
            Log($"{"\"" + Searches[s] + "\"",-12}{count,7}" + string.Concat(configs.Select(c => $"{times[c.name][s],28:F1}")));
        }
        Log($"{"typing copper",-19}" + string.Concat(configs.Select(c => $"{times[c.name].Skip(1).Take(6).Sum(),28:F0}")) + "   (total ms, six keystrokes)");

        foreach (var (name, _) in configs.Skip(1))
        {
            int differ = Enumerable.Range(0, Searches.Length).Count(s => results[name][s] != results[configs[0].name][s]);
            Log($"{name}: shows different pages from ARL's prefix for {differ} of {Searches.Length} searches");
            Assert.Equal(0, differ);
        }
    }

    static void Reset(MethodInfo arlPrefix)
    {
        new Harmony(ArlId).Unpatch(filterItems, HarmonyPatchType.Prefix, ArlId);
        new Harmony(BenchId).Unpatch(arlPrefix, HarmonyPatchType.Transpiler, BenchId);
    }

    // ARL's prefix reaches the dialog's private CountMatches by reflection on every call
    static IEnumerable<CodeInstruction> DirectCount(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo arlCount = AccessTools.Method(AccessTools.TypeByName("AttributeRenderingLibrary.HarmonyPatches.Handbook.GuiDialogHandbook_FilterItems_Patch"), "CountMatches");
        MethodInfo direct = AccessTools.Method(typeof(ArlFilterBench), nameof(CountMatches));
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(arlCount)) instruction.operand = direct;
            yield return instruction;
        }
    }

    public static int CountMatches(GuiDialogHandbook dialog, string text, Regex regex) => vanillaCount(dialog, text, regex);
}
