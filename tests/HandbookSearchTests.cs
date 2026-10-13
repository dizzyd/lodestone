using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AttributeRenderingLibrary;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

/// <summary>
/// Lodestone's empty-search shortcut shows the pages vanilla's FilterItems would, and,
/// with Attribute Rendering Library's prefix on, the pages ARL's would, its exclusions
/// included. Needs ARL in the session's mods for the second.
/// </summary>
public class HandbookSearchTests
{
    const int Rounds = 3;
    const string ArlId = "attributerenderinglibrary";

    static readonly (string text, bool category)[] Searches =
        [("", false), ("", true), ("  ", false), ("c", false), ("copper", false), ("oak plank", false)];

    static MethodInfo filterItems = AccessTools.Method(typeof(GuiDialogHandbook), "FilterItems");
    static AccessTools.FieldRef<GuiDialogHandbook, List<IFlatListItem>> shownRef = AccessTools.FieldRefAccess<GuiDialogHandbook, List<IFlatListItem>>("shownHandbookPages");
    static AccessTools.FieldRef<GuiDialogHandbook, List<GuiHandbookPage>> allRef = AccessTools.FieldRefAccess<GuiDialogHandbook, List<GuiHandbookPage>>("allHandbookPages");
    static AccessTools.FieldRef<GuiDialogHandbook, string> searchRef = AccessTools.FieldRefAccess<GuiDialogHandbook, string>("currentSearchText");
    static AccessTools.FieldRef<GuiDialogHandbook, string> categoryRef = AccessTools.FieldRefAccess<GuiDialogHandbook, string>("currentCatgoryCode");
    static AccessTools.FieldRef<GuiDialogHandbook, bool> loadingRef = AccessTools.FieldRefAccess<GuiDialogHandbook, bool>("loadingPagesAsync");

    [VsTest(TimeoutMs = 600_000)]
    [RequiresClient]
    public async Task SameAsVanilla()
    {
        await OnClient();   // here, not in Dialog(): an await resumes on the thread it was made from
        var (dialog, category) = await Dialog();
        var ours = Harmony.GetPatchInfo(filterItems).Prefixes.First(p => p.owner == "lodestone");
        Log("FilterItems prefixes: " + string.Join(", ", Harmony.GetPatchInfo(filterItems).Prefixes.Select(p => $"{p.owner}:{p.PatchMethod.Name}")));
        Assert.False(Harmony.GetPatchInfo(filterItems).Prefixes.Any(p => p.owner == ArlId));   // ArlFilterFix took it off in this pack

        var (fast, tFast) = await Run(dialog, category);
        var harmony = new Harmony("lodestone");
        harmony.Unpatch(filterItems, HarmonyPatchType.Prefix, "lodestone");
        try
        {
            var (vanilla, tVanilla) = await Run(dialog, category);
            Report("vanilla", vanilla, tVanilla, fast, tFast);
        }
        finally
        {
            harmony.Patch(filterItems, prefix: new HarmonyMethod(ours.PatchMethod) { priority = ours.priority, before = ours.before });
            Reset(dialog);
        }
    }

    [VsTest(TimeoutMs = 600_000)]
    [RequiresClient]
    public async Task SameAsArlWithExclusions()
    {
        await OnClient();   // here, not in Dialog(): an await resumes on the thread it was made from
        var (dialog, category) = await Dialog();
        var arlPrefix = AccessTools.TypeByName("AttributeRenderingLibrary.HarmonyPatches.Handbook.GuiDialogHandbook_FilterItems_Patch")?.GetMethod("Prefix");
        if (arlPrefix == null) Skip("ARL is not installed");
        var ours = Harmony.GetPatchInfo(filterItems).Prefixes.First(p => p.owner == "lodestone");

        // Hide every plank page, as an item using ARL's excludeFromHandbookSearch would
        var planks = Capi.World.Collectibles.Where(c => c?.Code?.Path.StartsWith("plank-") == true).ToList();
        foreach (var c in planks) c.CollectibleBehaviors = c.CollectibleBehaviors.Append(new HideFromSearch(c)).ToArray();
        var arl = new Harmony(ArlId);
        var harmony = new Harmony("lodestone");
        arl.Patch(filterItems, prefix: new HarmonyMethod(arlPrefix));
        try
        {
            Log($"Hiding {planks.Count} plank collectibles; FilterItems prefixes: " + string.Join(", ", Harmony.GetPatchInfo(filterItems).Prefixes.Select(p => $"{p.owner}:{p.PatchMethod.Name}")));
            // ARL's alone first, so neither pass pays the other's warm-up
            harmony.Unpatch(filterItems, HarmonyPatchType.Prefix, "lodestone");
            await Run(dialog, category);
            var (arlOnly, tArl) = await Run(dialog, category);
            harmony.Patch(filterItems, prefix: new HarmonyMethod(ours.PatchMethod) { priority = ours.priority, before = ours.before });
            var (fast, tFast) = await Run(dialog, category);

            int hidden = fast[0].Split(',').Count(code => planks.Any(p => code.EndsWith(p.Code.ToShortString())));
            Log($"Plank pages shown with the empty search: {hidden}");
            Assert.Equal(0, hidden);
            Report("ARL's prefix", arlOnly, tArl, fast, tFast);
        }
        finally
        {
            arl.Unpatch(filterItems, HarmonyPatchType.Prefix, ArlId);
            harmony.Unpatch(filterItems, HarmonyPatchType.Prefix, "lodestone");
            harmony.Patch(filterItems, prefix: new HarmonyMethod(ours.PatchMethod) { priority = ours.priority, before = ours.before });
            foreach (var c in planks) c.CollectibleBehaviors = c.CollectibleBehaviors.Where(b => b is not HideFromSearch).ToArray();
            Reset(dialog);
        }
    }

    class HideFromSearch(CollectibleObject collObj) : CollectibleBehavior(collObj), IHandbookARL
    {
        public bool ExcludeFromHandbookSearch(ItemStack stack) => true;
    }

    static async Task<(GuiDialogHandbook, string)> Dialog()
    {
        var dialog = (GuiDialogHandbook)AccessTools.Field(typeof(ModSystemSurvivalHandbook), "dialog").GetValue(Capi.ModLoader.GetModSystem<ModSystemSurvivalHandbook>());
        while (loadingRef(dialog)) await Frames.Wait(10);
        var all = allRef(dialog);
        string category = all.GroupBy(p => p.CategoryCode).OrderByDescending(g => g.Count()).First().Key;
        Log($"{all.Count} pages; category search uses {category} ({all.Count(p => p.CategoryCode == category)} pages)");
        return (dialog, category);
    }

    static async Task<(string[], double[])> Run(GuiDialogHandbook dialog, string category)
    {
        var shown = new string[Searches.Length];
        var ms = new double[Searches.Length];
        for (int round = 0; round < Rounds; round++)
        {
            for (int s = 0; s < Searches.Length; s++)
            {
                searchRef(dialog) = Searches[s].text;
                categoryRef(dialog) = Searches[s].category ? category : null;
                long t = Stopwatch.GetTimestamp();
                dialog.FilterItems();
                ms[s] += Stopwatch.GetElapsedTime(t).TotalMilliseconds / Rounds;
                shown[s] = string.Join(",", shownRef(dialog).Select(p => ((GuiHandbookPage)p).PageCode));
                await Frames.Wait(1);
            }
        }
        return (shown, ms);
    }

    static void Report(string baseline, string[] expected, double[] tExpected, string[] actual, double[] tActual)
    {
        for (int s = 0; s < Searches.Length; s++)
        {
            string label = $"\"{Searches[s].text}\"" + (Searches[s].category ? " in category" : "");
            int count = expected[s].Length == 0 ? 0 : expected[s].Count(ch => ch == ',') + 1;
            Log($"{label,-18} {count,6} shown   {baseline} {tExpected[s],7:F1} ms   lodestone {tActual[s],7:F1} ms   {(actual[s] == expected[s] ? "same pages" : "DIFFERENT PAGES")}");
        }
        Assert.Equal(0, Enumerable.Range(0, Searches.Length).Count(s => actual[s] != expected[s]));
    }

    static void Reset(GuiDialogHandbook dialog)
    {
        searchRef(dialog) = "";
        categoryRef(dialog) = null;
        dialog.FilterItems();
    }
}
