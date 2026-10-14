using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Common;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

/// <summary>
/// With another mod rewriting InventoryCraftingGrid.FindMatchingRecipe (other than
/// OptiTime, which Lodestone runs in place of), every completed recipe grid gives the
/// recipe it gives with Lodestone's patches taken away. Lodestone either leaves the lookup
/// to that mod, its own lookup prefix off, or, for Improved Handbook Recipe Helper, collects
/// every match itself: then the match counts, and the order "Next" cycles through, must
/// be the mod's own too. Skips when no such mod is installed.
/// </summary>
public class CraftingCoexistence
{
    static MethodInfo find = AccessTools.Method(typeof(InventoryCraftingGrid), "FindMatchingRecipe");

    [VsTest(TimeoutMs = 900_000)]
    [RequiresClient]
    public async Task OtherModsKeepTheirLookup()
    {
        await OnClient();
        var info = Harmony.GetPatchInfo(find);
        var rewriters = info?.Transpilers.Concat(info.Prefixes.Where(p => p.PatchMethod.ReturnType == typeof(bool)))
            .Where(p => p.owner != "lodestone" && p.PatchMethod.DeclaringType?.FullName != "OptiTime.RecipeLookupCacheOptimization")
            .Select(p => p.owner).Distinct().ToList() ?? [];
        if (rewriters.Count == 0) Skip("No other mod rewrites FindMatchingRecipe");
        Log("Rewritten by: " + string.Join(", ", rewriters));
        Log("Prefixes: " + string.Join(", ", info.Prefixes.Select(p => $"{p.owner}:{p.PatchMethod.Name}")) + "; transpilers: " + string.Join(", ", info.Transpilers.Select(p => $"{p.owner}:{p.PatchMethod.Name}")));
        string mode = (string)AccessTools.TypeByName("lodestone.CraftingGridFix").GetProperty("Mode").GetValue(null);
        bool collecting = mode.Contains("Improved Handbook Recipe Helper");
        Log($"Lodestone's crafting mode: {mode}");
        Assert.Equal(collecting, info.Prefixes.Any(p => p.owner == "lodestone" && p.PatchMethod.Name == "Lookup"));

        // Improved Handbook Recipe Helper's own record of how many recipes matched, and its Next button
        var cycling = AccessTools.TypeByName("ImprovedHandbookRecipes.Patch_InventoryCraftingGrid_FindMatchingRecipe");
        var matchCount = cycling?.GetMethod("GetLastMatchCount");
        var selectorType = AccessTools.TypeByName("ImprovedHandbookRecipes.RecipeSelector");
        object selector = selectorType == null ? null : Capi.ModLoader.GetModSystem(selectorType.FullName);
        var nextPressed = selectorType == null ? null : (System.Collections.Generic.HashSet<string>)AccessTools.Field(selectorType, "_nextPressedPlayers").GetValue(selector);
        var clearSelection = selectorType?.GetMethod("ClearExplicitSelection");

        var world = Capi.World;
        var grid = (InventoryCraftingGrid)world.Player.InventoryManager.GetOwnInventory(GlobalConstants.craftingInvClassName);
        var grids = CraftingGridBench.Usable(world).Select(CraftingGridBench.Fill).ToList();

        var player = Capi.World.Player;
        // Recipes can share a name, so each is told apart by its place in the game's list
        var position = new System.Collections.Generic.Dictionary<GridRecipe, int>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        for (int i = 0; i < world.GridRecipes.Count; i++) position[world.GridRecipes[i]] = i;
        string Lookup(ItemStack[] stacks, bool fresh)
        {
            if (fresh)
            {
                grid.MatchingRecipe = null;   // a mod may carry the last recipe over
                clearSelection?.Invoke(selector, [player]);
            }
            CraftingGridBench.SetGrid(grid, stacks);
            CraftingGridBench.Call(grid);
            string count = matchCount == null ? "" : $" |{matchCount.Invoke(null, [player])}";
            return grid.MatchingRecipe == null ? "-" + count : $"{grid.MatchingRecipe.Name}#{position[grid.MatchingRecipe]}{count}";
        }

        string[] Picks()
        {
            var picks = new string[grids.Count];
            for (int i = 0; i < grids.Count; i++) picks[i] = Lookup(grids[i], fresh: true);
            return picks;
        }

        // For every grid more than one recipe matches, the recipes Next steps through
        string[] Cycles(System.Collections.Generic.List<int> several)
        {
            var cycles = new string[several.Count];
            for (int n = 0; n < several.Count; n++)
            {
                var seen = new System.Collections.Generic.List<string> { Lookup(grids[several[n]], fresh: true) };
                for (int press = 0; press < 4; press++)
                {
                    nextPressed.Add(player.PlayerUID);
                    seen.Add(Lookup(grids[several[n]], fresh: false));
                }
                nextPressed.Remove(player.PlayerUID);
                cycles[n] = string.Join(" > ", seen);
            }
            return cycles;
        }

        var with = Picks();
        var several = matchCount == null ? new System.Collections.Generic.List<int>()
            : Enumerable.Range(0, grids.Count).Where(i => int.Parse(with[i][(with[i].LastIndexOf('|') + 1)..]) > 1).ToList();
        var withCycles = nextPressed == null ? [] : Cycles(several);
        Log($"{several.Count} completed grids match more than one recipe");
        await Frames.Wait(1);
        var ours = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Where(p => p.owner == "lodestone").ToList();
        var harmony = new Harmony("lodestone");
        harmony.Unpatch(find, HarmonyPatchType.All, "lodestone");
        string[] without, withoutCycles;
        try
        {
            without = Picks();
            withoutCycles = nextPressed == null ? [] : Cycles(several);
        }
        finally
        {
            foreach (var p in ours)
            {
                var hm = new HarmonyMethod(p.PatchMethod) { priority = p.priority, before = p.before, after = p.after };
                if (info.Prefixes.Contains(p)) harmony.Patch(find, prefix: hm);
                else if (info.Postfixes.Contains(p)) harmony.Patch(find, postfix: hm);
                else harmony.Patch(find, transpiler: hm);
            }
            CraftingGridBench.SetGrid(grid, new ItemStack[9]);
            CraftingGridBench.Call(grid);
        }

        var differ = Enumerable.Range(0, grids.Count).Where(i => with[i] != without[i]).ToList();
        Log($"{grids.Count} completed grids, {with.Count(p => !p.StartsWith("-"))} matched; {differ.Count} differ with Lodestone's patches");
        foreach (int i in differ.Take(10)) Log($"  {i}: with={with[i]} without={without[i]}");
        Assert.Equal(0, differ.Count);

        var cyclesDiffer = Enumerable.Range(0, withCycles.Length).Where(n => withCycles[n] != withoutCycles[n]).ToList();
        Log($"Pressing Next four times on each: {cyclesDiffer.Count} of {withCycles.Length} differ with Lodestone's patches");
        int moving = withCycles.Count(c => c.Split(" > ").Select(x => x[..x.LastIndexOf('|')]).Distinct().Count() > 1);
        Log($"Next moves to another recipe on {moving} of them");
        foreach (string cycle in withCycles.Where(c => c.Split(" > ").Distinct().Count() > 1).Take(2)) Log($"  e.g. {cycle}");
        foreach (int n in cyclesDiffer.Take(5)) Log($"  {several[n]}: with {withCycles[n]}  without {withoutCycles[n]}");
        Assert.Equal(0, cyclesDiffer.Count);
    }
}
