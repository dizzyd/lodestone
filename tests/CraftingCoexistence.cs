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
/// OptiTime, which Lodestone runs in place of), Lodestone leaves the lookup to it: its own
/// lookup prefix stays off, and every completed recipe grid gives the recipe it gives with
/// Lodestone's patches taken away. Skips when no such mod is installed.
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
        Assert.False(info.Prefixes.Any(p => p.owner == "lodestone" && p.PatchMethod.Name == "Lookup"));

        var world = Capi.World;
        var grid = (InventoryCraftingGrid)world.Player.InventoryManager.GetOwnInventory(GlobalConstants.craftingInvClassName);
        var grids = CraftingGridBench.Usable(world).Select(CraftingGridBench.Fill).ToList();

        string[] Picks()
        {
            var picks = new string[grids.Count];
            for (int i = 0; i < grids.Count; i++)
            {
                grid.MatchingRecipe = null;   // a mod may carry the last recipe over
                CraftingGridBench.SetGrid(grid, grids[i]);
                CraftingGridBench.Call(grid);
                picks[i] = grid.MatchingRecipe?.Name?.ToString() ?? "-";
            }
            return picks;
        }

        var with = Picks();
        await Frames.Wait(1);
        var ours = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Where(p => p.owner == "lodestone").ToList();
        var harmony = new Harmony("lodestone");
        harmony.Unpatch(find, HarmonyPatchType.All, "lodestone");
        string[] without;
        try
        {
            without = Picks();
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
        Log($"{grids.Count} completed grids, {with.Count(p => p != "-")} matched; {differ.Count} differ with Lodestone's patches");
        foreach (int i in differ.Take(10)) Log($"  {i}: with={with[i]} without={without[i]}");
        Assert.Equal(0, differ.Count);
    }
}
