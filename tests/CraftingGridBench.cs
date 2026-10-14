using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Times InventoryCraftingGrid.FindMatchingRecipe on the client under six patch sets,
/// swapped in place in one session: vanilla, Lodestone's dedup alone, with its
/// slot count check, with its indexed lookup, OptiTime's lookup, and Lodestone patched
/// after OptiTime, as it is in game, where it runs in OptiTime's place. Each sample
/// recipe is placed into the grid one ingredient at a time, one lookup per placement,
/// as a player fills a grid. Also records which recipe each patch set picks for every
/// grid state.
/// </summary>
public class CraftingGridBench
{
    const string Find = "FindMatchingRecipe";
    const int Recipes = 100;
    const int Rounds = 3;

    static MethodInfo find = AccessTools.Method(typeof(InventoryCraftingGrid), Find);
    static MethodInfo matches = AccessTools.Method(typeof(GridRecipe), "Matches", [typeof(IPlayer), typeof(ItemSlot[]), typeof(int)])
        ?? AccessTools.Method(typeof(GridRecipe), "Matches", [typeof(IPlayer), typeof(IWorldAccessor), typeof(ItemSlot[]), typeof(int)]);

    const string OptiTimeId = "com.zaldaryon.optitime";   // OptiTime's own
    static Type ot = AccessTools.TypeByName("OptiTime.RecipeLookupCacheOptimization");
    static Type hb = AccessTools.TypeByName("lodestone.CraftingGridFix");

    [VsTest(TimeoutMs = 1_800_000)]
    [RequiresClient]
    public async Task Benchmark()
    {
        await OnClient();
        Assert.NotNull(hb);
        var world = Capi.World;
        var grid = (InventoryCraftingGrid)world.Player.InventoryManager.GetOwnInventory(GlobalConstants.craftingInvClassName);
        int width = (int)Math.Sqrt(grid.Count - 1);
        Assert.Equal(3, width);

        Log($"GridRecipes: {world.GridRecipes.Count}; ingredient keys: {world.FastSearchRecipesByIngredient.Count}");
        Log("By domain: " + string.Join(", ", world.GridRecipes.GroupBy(r => r.Name?.Domain).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        Log($"Patches at start on FindMatchingRecipe: {Owners(find)}; on Matches: {Owners(matches)}");
        others = RivalBench.Record(p => p.owner != "lodestone" && p.owner != OptiTimeId);
        Log($"lodestone mode: {hb.GetProperty("Mode").GetValue(null)}");

        // Recipes a 3x3 grid can hold whose every ingredient is one exact stack
        var usable = Usable(world);
        int stride = Math.Max(1, usable.Count / Recipes);
        var sample = usable.Where((_, i) => i % stride == 0).Take(Recipes).ToList();
        int planky = sample.Count(r => r.ResolvedIngredients.Any(i => i?.ResolvedItemStack?.Collectible.Code.Path.StartsWith("plank") == true));
        Log($"Usable recipes {usable.Count}; sampled {sample.Count} (every {stride}th), {planky} with planks, {sample.Count(r => r.Shapeless)} shapeless");
        Log("Sampled by domain: " + string.Join(", ", sample.GroupBy(r => r.Name?.Domain).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));

        // Grid states: each recipe's ingredients placed one at a time, in slot order
        var states = new List<(GridRecipe recipe, ItemStack[] slots)>();
        foreach (var recipe in sample)
        {
            var placed = Placements(recipe);
            for (int n = 1; n <= placed.Count; n++)
            {
                var s = new ItemStack[width * width];
                foreach (var (slot, stack) in placed.Take(n)) s[slot] = stack;
                states.Add((recipe, s));
            }
        }
        Log($"Grid states: {states.Count}");

        var all = new (string name, Action apply)[]
        {
            ("vanilla", () => { }),
            ("dedup only", () => { ApplyLodestone(); Unpatch("MatchesTranspiler"); UnpatchPrefix("Lookup"); }),
            ("dedup + count", () => { ApplyLodestone(); UnpatchPrefix("Lookup"); }),
            ("lodestone", ApplyLodestone),
            ("optitime", ApplyOptiTime),
            ("lodestone over optitime", () => { ApplyOptiTime(); ApplyLodestone(); }),
        };
        var configs = (ot == null ? all.Where(c => !c.name.Contains("optitime")) : all).ToList();
        if (ot == null) Log("OptiTime is not installed; its configurations are left out");

        // Any other mod that patches the lookup, alone and with Lodestone patched after it, as in game
        if (others.Count > 0)
        {
            string label = string.Join("+", others.Select(r => r.Patch.owner).Distinct());
            Log($"Also patched by {label}: " + string.Join(", ", others.Select(r => $"{r.Type} {r.Method.Name} <- {r.Patch.PatchMethod.Name}")));
            configs.Add((label, () => RivalBench.Restore(others)));
            configs.Add((label + " + lodestone", () => { RivalBench.Restore(others); ApplyLodestone(); }));
        }

        var cold = configs.ToDictionary(c => c.name, _ => new List<double>());
        var warm = configs.ToDictionary(c => c.name, _ => new List<double>());
        var picks = configs.ToDictionary(c => c.name, _ => new string[states.Count]);
        var stateTimes = configs.ToDictionary(c => c.name, _ => new double[states.Count]);

        for (int round = 0; round < Rounds; round++)
        {
            foreach (var (name, apply) in configs)
            {
                ClearPatches();
                apply();

                // JIT warm-up off the measured states, then OptiTime's index (built once per
                // recipe set) is kept and only its per-grid caches are dropped
                SetGrid(grid, states[0].slots);
                Call(grid);
                if (name == "optitime") ot.GetMethod("ResetCaches", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);

                for (int pass = 0; pass < 2; pass++)
                {
                    var times = pass == 0 ? cold[name] : warm[name];
                    for (int i = 0; i < states.Count; i++)
                    {
                        SetGrid(grid, states[i].slots);
                        long t = Stopwatch.GetTimestamp();
                        Call(grid);
                        double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                        times.Add(ms);
                        if (pass == 0) stateTimes[name][i] += ms / Rounds;
                        if (round == 0 && pass == 0) picks[name][i] = grid.MatchingRecipe?.Name?.ToString() ?? "-";
                        if (i % 25 == 0) await Frames.Wait(1);
                    }
                }
            }
        }

        ClearPatches();
        SetGrid(grid, new ItemStack[width * width]);
        Call(grid);

        if (ot != null)
        {
            // OptiTime's index build, timed alone
            ApplyOptiTime();
            ot.GetMethod("Cleanup").Invoke(null, null);
            SetGrid(grid, states[0].slots);
            long b = Stopwatch.GetTimestamp();
            Call(grid);
            Log($"OptiTime first lookup incl. index build: {Stopwatch.GetElapsedTime(b).TotalMilliseconds:F1} ms");
            Restore();
            SetGrid(grid, new ItemStack[width * width]);
            Call(grid);
        }
        else
        {
            Restore();
            SetGrid(grid, new ItemStack[width * width]);
            Call(grid);
        }

        Log("");
        Log($"Per lookup, ms, {states.Count} grid states x {Rounds} rounds:");
        Log($"{"config",-24}{"cold mean",10}{"median",9}{"p90",9}{"max",9}{"total",9}   {"warm mean",10}{"median",9}");
        foreach (var (name, _) in configs)
        {
            var c = cold[name]; var w = warm[name];
            Log($"{name,-24}{c.Average(),10:F3}{Pct(c, 50),9:F3}{Pct(c, 90),9:F3}{c.Max(),9:F2}{c.Sum() / Rounds,9:F0}   {w.Average(),10:F3}{Pct(w, 50),9:F3}");
        }

        Log("");
        Log("Slowest grid states under vanilla (cold, mean over rounds), ms:");
        foreach (int i in Enumerable.Range(0, states.Count).OrderByDescending(i => stateTimes["vanilla"][i]).Take(8))
        {
            Log($"  {states[i].recipe.Name,-60} {states[i].slots.Count(s => s != null)} filled  "
                + string.Join("  ", configs.Select(c => $"{c.name}={stateTimes[c.name][i]:F2}")));
        }

        Log("");
        foreach (var (name, _) in configs.Skip(1))
        {
            var diff = Enumerable.Range(0, states.Count).Where(i => picks[name][i] != picks["vanilla"][i]).ToList();
            Log($"{name}: picks a different recipe from vanilla in {diff.Count} of {states.Count} grid states");
            foreach (int i in diff.Take(10))
                Log($"  {states[i].recipe.Name} ({states[i].slots.Count(s => s != null)} filled): vanilla={picks["vanilla"][i]} {name}={picks[name][i]}");
        }
    }

    static void UnpatchPrefix(string prefix) =>
        new Harmony("lodestone").Unpatch(find, hb.GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static));

    static void Unpatch(string transpiler) =>
        new Harmony("lodestone").Unpatch(find, hb.GetMethod(transpiler, BindingFlags.NonPublic | BindingFlags.Static));

    /// <summary>
    /// Every usable recipe's completed grid, looked up under vanilla and under lodestone:
    /// the same recipe must come back for each.
    /// </summary>
    [VsTest(TimeoutMs = 1_800_000)]
    [RequiresClient]
    public async Task SameRecipeAsVanilla()
    {
        await OnClient();
        if (others.Count == 0) others = RivalBench.Record(p => p.owner != "lodestone" && p.owner != OptiTimeId);
        var world = Capi.World;
        var grid = (InventoryCraftingGrid)world.Player.InventoryManager.GetOwnInventory(GlobalConstants.craftingInvClassName);
        var grids = Usable(world).Select(r => Fill(r)).ToList();

        var picks = new string[grids.Count];
        ClearPatches();
        for (int i = 0; i < grids.Count; i++)
        {
            SetGrid(grid, grids[i]);
            Call(grid);
            picks[i] = grid.MatchingRecipe?.Name?.ToString() ?? "-";
            if (i % 50 == 0) await Frames.Wait(1);
        }

        ApplyLodestone();
        Log($"lodestone mode: {hb.GetProperty("Mode").GetValue(null)}");
        var differ = new List<string>();
        int matched = 0;
        for (int i = 0; i < grids.Count; i++)
        {
            SetGrid(grid, grids[i]);
            Call(grid);
            string pick = grid.MatchingRecipe?.Name?.ToString() ?? "-";
            if (pick != "-") matched++;
            if (pick != picks[i]) differ.Add($"{i}: vanilla={picks[i]} lodestone={pick}");
            if (i % 50 == 0) await Frames.Wait(1);
        }
        Restore();
        SetGrid(grid, new ItemStack[9]);
        Call(grid);

        Log($"{grids.Count} completed grids, {matched} matched a recipe, {differ.Count} differ from vanilla");
        foreach (string d in differ.Take(20)) Log("  " + d);
        Assert.Equal(0, differ.Count);
    }

    internal static List<GridRecipe> Usable(IWorldAccessor world) => world.GridRecipes.Where(r => r.Enabled && r.Width <= 3 && r.Height <= 3 && r.ResolvedIngredients != null
        && r.ResolvedIngredients.Any(i => i != null)
        && r.ResolvedIngredients.All(i => i == null || (i.MatchingType == EnumRecipeMatchType.Exact && i.ResolvedItemStack != null))).ToList();

    // The recipe's ingredients laid out in a 3x3 grid; shapeless ones fill from the first slot
    internal static ItemStack[] Fill(GridRecipe recipe) => Placements(recipe).Aggregate(new ItemStack[9], (g, p) => { g[p.slot] = p.stack; return g; });

    internal static List<(int slot, ItemStack stack)> Placements(GridRecipe recipe)
    {
        var placed = new List<(int, ItemStack)>();
        for (int i = 0; i < recipe.ResolvedIngredients.Length; i++)
        {
            var ing = recipe.ResolvedIngredients[i];
            if (ing == null) continue;
            int slot = recipe.Shapeless ? placed.Count : (i / recipe.Width) * 3 + i % recipe.Width;
            var stack = ing.ResolvedItemStack.Clone();
            stack.StackSize = Math.Max(1, ing.Quantity);
            placed.Add((slot, stack));
        }
        return placed;
    }

    // The patches as the game made them: OptiTime's in StartClientSide, Lodestone's at LevelFinalize
    // Other mods' patches on the lookup, recorded before the benchmark clears them
    static List<RivalBench.Recorded> others = [];

    static void Restore()
    {
        ClearPatches();
        RivalBench.Restore(others);
        if (Capi.ModLoader.IsModEnabled("optitime")) ApplyOptiTime();
        ApplyLodestone();
    }

    static void ClearPatches()
    {
        foreach (var m in new MethodBase[] { find, matches })
        {
            var info = Harmony.GetPatchInfo(m);
            if (info == null) continue;
            foreach (string owner in info.Owners.ToList()) new Harmony(owner).Unpatch(m, HarmonyPatchType.All, owner);
        }
    }

    // Lodestone's own Apply, as the mod runs it
    static void ApplyLodestone()
    {
        var mod = Capi.ModLoader.GetMod("lodestone");
        hb.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static).Invoke(null, [new Harmony(mod.Info.ModID), mod, true]);
    }

    // The two patches OptiTime makes in StartClientSide when nothing else is there
    static void ApplyOptiTime()
    {
        ApplyOptiTimeMatches();
        new Harmony(OptiTimeId).Patch(find, prefix: new HarmonyMethod(ot, "FindMatchingRecipe_Prefix"));
    }

    static void ApplyOptiTimeMatches() => new Harmony(OptiTimeId).Patch(matches, prefix: new HarmonyMethod(ot, "MatchesWithWorld_Prefix"));

    internal static void SetGrid(InventoryCraftingGrid grid, ItemStack[] stacks)
    {
        for (int i = 0; i < stacks.Length; i++) grid[i].Itemstack = stacks[i]?.Clone();
    }

    internal static void Call(InventoryCraftingGrid grid) => find.Invoke(grid, null);

    static string Owners(MethodBase m) => string.Join(",", Harmony.GetPatchInfo(m)?.Owners ?? []) is { Length: > 0 } s ? s : "none";

    static double Pct(List<double> xs, int p)
    {
        var sorted = xs.OrderBy(x => x).ToList();
        return sorted[Math.Min(sorted.Count - 1, sorted.Count * p / 100)];
    }
}
