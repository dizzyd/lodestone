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
/// Another mod's crafting grid lookup against vanilla's and Lodestone's, in a session
/// with that mod installed as it ships and Lodestone's CraftingGrid off in
/// ModConfig/lodestone.json. The other mod's patches on FindMatchingRecipe and
/// GridRecipe.Matches are recorded, lifted for the vanilla and Lodestone runs, and put
/// back. Each round fills a different set of recipes, so a cache of whole grids cannot
/// answer a new grid from an earlier round; repeating a grid is timed separately.
/// </summary>
public class RivalBench
{
    const int RecipesPerRound = 40;
    const int Rounds = 3;

    static MethodInfo find = AccessTools.Method(typeof(InventoryCraftingGrid), "FindMatchingRecipe");
    static MethodBase[] targets =
    [
        find,
        AccessTools.Method(typeof(GridRecipe), "Matches", [typeof(IPlayer), typeof(IWorldAccessor), typeof(ItemSlot[]), typeof(int)]),
    ];

    internal record Recorded(MethodBase Method, HarmonyPatchType Type, Patch Patch);

    /// <summary>Every patch on the crafting lookup's targets whose owner <paramref name="keep"/> accepts.</summary>
    internal static List<Recorded> Record(System.Func<Patch, bool> keep)
    {
        var recorded = new List<Recorded>();
        foreach (var m in targets)
        {
            var info = Harmony.GetPatchInfo(m);
            if (info == null) continue;
            recorded.AddRange(info.Prefixes.Select(p => new Recorded(m, HarmonyPatchType.Prefix, p)));
            recorded.AddRange(info.Postfixes.Select(p => new Recorded(m, HarmonyPatchType.Postfix, p)));
            recorded.AddRange(info.Transpilers.Select(p => new Recorded(m, HarmonyPatchType.Transpiler, p)));
            recorded.AddRange(info.Finalizers.Select(p => new Recorded(m, HarmonyPatchType.Finalizer, p)));
        }
        return recorded.Where(r => keep(r.Patch)).ToList();
    }

    [VsTest(TimeoutMs = 1_800_000)]
    [RequiresClient]
    public async Task Compare()
    {
        await OnClient();
        if (Harmony.GetPatchInfo(find)?.Owners.Contains("lodestone") == true) Skip("Needs Lodestone's CraftingGrid off in lodestone.json, and one other crafting mod");
        var world = Capi.World;
        var grid = (InventoryCraftingGrid)world.Player.InventoryManager.GetOwnInventory(GlobalConstants.craftingInvClassName);

        var recorded = Record(p => p.owner != "lodestone");
        if (recorded.Count == 0) Skip("No other mod patches the crafting grid lookup");
        string rival = string.Join(", ", recorded.Select(r => r.Patch.owner).Distinct());
        Log($"Rival: {rival}; mods: {string.Join(", ", Capi.ModLoader.Mods.Select(m => $"{m.Info.ModID} {m.Info.Version}").Where(s => !s.StartsWith("game") && !s.StartsWith("survival") && !s.StartsWith("creative")))}");
        foreach (var r in recorded) Log($"  {r.Patch.owner} {r.Type} {r.Method.DeclaringType.Name}.{r.Method.Name} <- {r.Patch.PatchMethod.DeclaringType.Name}.{r.Patch.PatchMethod.Name}");

        // Disjoint recipe sets: one to warm the JIT, one per round
        var usable = CraftingGridBench.Usable(world);
        int sets = Rounds + 1, stride = usable.Count / (RecipesPerRound * sets);
        List<ItemStack[]> States(int set) => usable.Where((_, i) => i % stride == 0).Where((_, i) => i % sets == set).Take(RecipesPerRound)
            .SelectMany(r => { var p = CraftingGridBench.Placements(r); return Enumerable.Range(1, p.Count).Select(n => { var s = new ItemStack[9]; foreach (var (slot, st) in p.Take(n)) s[slot] = st; return s; }); }).ToList();
        var warmup = States(0);
        var rounds = Enumerable.Range(1, Rounds).Select(States).ToList();
        Log($"{usable.Count} usable recipes; {RecipesPerRound} recipes per round, {rounds.Sum(r => r.Count)} grid states over {Rounds} rounds");

        var configs = new (string name, Action apply)[]
        {
            (rival, () => Restore(recorded)),
            ("vanilla", () => Lift(recorded)),
            ("lodestone", () => { Lift(recorded); ApplyLodestone(); }),
        };
        var cold = configs.ToDictionary(c => c.name, _ => new List<double>());
        var warm = configs.ToDictionary(c => c.name, _ => new List<double>());
        var picks = configs.ToDictionary(c => c.name, _ => new List<string>());
        try
        {
            foreach (var (name, apply) in configs)
            {
                Reset(recorded);
                apply();
                foreach (var s in warmup) Lookup(grid, s);
            }
            for (int round = 0; round < Rounds; round++)
            {
                foreach (var (name, apply) in configs)
                {
                    Reset(recorded);
                    apply();
                    foreach (var s in rounds[round])
                    {
                        cold[name].Add(Lookup(grid, s));
                        picks[name].Add(grid.MatchingRecipe?.Name?.ToString() ?? "-");
                    }
                    foreach (var s in rounds[round]) warm[name].Add(Lookup(grid, s));
                    await Frames.Wait(1);
                }
            }

            // Every usable recipe's completed grid, the rival against vanilla
            var grids = usable.Select(CraftingGridBench.Fill).ToList();
            var sweep = new Dictionary<string, string[]>();
            foreach (var (name, apply) in configs.Take(2))
            {
                Reset(recorded);
                apply();
                sweep[name] = new string[grids.Count];
                for (int i = 0; i < grids.Count; i++)
                {
                    Lookup(grid, grids[i]);
                    sweep[name][i] = grid.MatchingRecipe?.Name?.ToString() ?? "-";
                    if (i % 200 == 0) await Frames.Wait(1);
                }
            }
            var wrong = Enumerable.Range(0, grids.Count).Where(i => sweep[rival][i] != sweep["vanilla"][i]).ToList();
            Log($"Completed grids: {rival} picks a different recipe from vanilla in {wrong.Count} of {grids.Count}");
            foreach (int i in wrong.Take(8)) Log($"  {usable[i].Name}: vanilla={sweep["vanilla"][i]} {rival}={sweep[rival][i]}");
        }
        finally
        {
            Reset(recorded);
            Restore(recorded);
            CraftingGridBench.SetGrid(grid, new ItemStack[9]);
            CraftingGridBench.Call(grid);
        }

        Log("");
        Log($"ms per lookup over {cold.First().Value.Count} grid states:");
        Log($"{"config",-34}{"new mean",10}{"median",9}{"p90",9}{"max",9}   {"repeat mean",12}{"median",9}");
        foreach (var (name, _) in configs)
        {
            var c = cold[name].OrderBy(x => x).ToList(); var w = warm[name].OrderBy(x => x).ToList();
            Log($"{name,-34}{c.Average(),10:F3}{c[c.Count / 2],9:F3}{c[c.Count * 9 / 10],9:F3}{c[^1],9:F2}   {w.Average(),12:F3}{w[w.Count / 2],9:F3}");
        }
        foreach (var (name, _) in configs.Where(c => c.name != "vanilla"))
        {
            int differ = Enumerable.Range(0, picks[name].Count).Count(i => picks[name][i] != picks["vanilla"][i]);
            Log($"{name}: picks a different recipe from vanilla in {differ} of {picks[name].Count} grid states");
        }
    }

    static double Lookup(InventoryCraftingGrid grid, ItemStack[] stacks)
    {
        CraftingGridBench.SetGrid(grid, stacks);
        long t = Stopwatch.GetTimestamp();
        CraftingGridBench.Call(grid);
        return Stopwatch.GetElapsedTime(t).TotalMilliseconds;
    }

    // Everything the rival put on the targets, and Lodestone's if a run applied it
    static void Reset(List<Recorded> recorded)
    {
        Lift(recorded);
        foreach (var m in targets) new Harmony("lodestone").Unpatch(m, HarmonyPatchType.All, "lodestone");
    }

    internal static void Lift(List<Recorded> recorded)
    {
        foreach (var r in recorded) new Harmony(r.Patch.owner).Unpatch(r.Method, r.Patch.PatchMethod);
    }

    internal static void Restore(List<Recorded> recorded)
    {
        foreach (var r in recorded)
        {
            if (Harmony.GetPatchInfo(r.Method)?.Owners.Contains(r.Patch.owner) == true
                && AllPatches(r.Method).Any(p => p.PatchMethod == r.Patch.PatchMethod)) continue;
            var hm = new HarmonyMethod(r.Patch.PatchMethod) { priority = r.Patch.priority, before = r.Patch.before, after = r.Patch.after };
            var h = new Harmony(r.Patch.owner);
            switch (r.Type)
            {
                case HarmonyPatchType.Prefix: h.Patch(r.Method, prefix: hm); break;
                case HarmonyPatchType.Postfix: h.Patch(r.Method, postfix: hm); break;
                case HarmonyPatchType.Transpiler: h.Patch(r.Method, transpiler: hm); break;
                case HarmonyPatchType.Finalizer: h.Patch(r.Method, finalizer: hm); break;
            }
        }
    }

    static IEnumerable<Patch> AllPatches(MethodBase m)
    {
        var i = Harmony.GetPatchInfo(m);
        return i == null ? [] : i.Prefixes.Concat(i.Postfixes).Concat(i.Transpilers).Concat(i.Finalizers);
    }

    static void ApplyLodestone()
    {
        var mod = Capi.ModLoader.GetMod("lodestone");
        AccessTools.TypeByName("lodestone.CraftingGridFix").GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, [new Harmony(mod.Info.ModID), mod, true]);
    }
}
