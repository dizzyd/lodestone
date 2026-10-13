using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.Common;

namespace handbookperf;

/// <summary>
/// Every change to a crafting grid slot makes InventoryCraftingGrid.FindMatchingRecipe
/// gather, for each filled slot, every grid recipe that slot's stack is an ingredient of,
/// deduplicating with List.AddIfNotPresent, a linear scan. Planks, sticks and tools are
/// ingredients of thousands of recipes in a large pack, so the list grows into the
/// thousands and each add scans all of it: tens to hundreds of milliseconds on the main
/// thread per slot change, worse with every slot filled.
///
/// This swaps that one call for a HashSet check. Recipes do not override Equals, so the
/// reference comparison is what vanilla does, and the list fills in the same order, so
/// the same recipe is found.
///
/// The list then holds every recipe that uses anything in the grid, and each is tried
/// with GridRecipe.Matches at every offset: one plank makes that a thousand furniture
/// recipes. A shaped recipe matches only when the grid is filled exactly where its
/// pattern has ingredients, so one whose ingredient count differs from the grid's
/// filled slot count is skipped without the call.
/// </summary>
internal static class CraftingGridFix
{
    private static ILogger logger;
    private static bool deduplicating;
    private static bool prechecking;

    [ThreadStatic] private static List<GridRecipe> currentList;
    [ThreadStatic] private static HashSet<GridRecipe> seen;

    public static string Mode
    {
        get
        {
            string[] on = [deduplicating ? "hashed" : null, prechecking ? "counted" : null];
            return on.Any(m => m != null) ? string.Join(", ", on.Where(m => m != null)) : "vanilla";
        }
    }

    public static void Apply(Harmony harmony, Mod mod)
    {
        MethodInfo find = AccessTools.Method(typeof(InventoryCraftingGrid), "FindMatchingRecipe");
        if (Harmony.GetPatchInfo(find)?.Owners.Contains(harmony.Id) == true) return;
        ILogger log = logger = mod.Logger;
        var start = new HarmonyMethod(typeof(CraftingGridFix), nameof(Start));
        var end = new HarmonyMethod(typeof(CraftingGridFix), nameof(End));
        if (Environment.GetEnvironmentVariable("HANDBOOKPERF_NOCRAFTFIX") == "1")
        {
            harmony.Patch(find, prefix: start, postfix: end);
            log.Notification("Crafting grid fix off (HANDBOOKPERF_NOCRAFTFIX=1); timing only");
            return;
        }
        harmony.Patch(find, prefix: start, postfix: end);
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(find);

        int adds = original.Count(i => i.Calls(VanillaAdd));
        if (adds == 1)
        {
            harmony.Patch(find, transpiler: new HarmonyMethod(typeof(CraftingGridFix), nameof(DedupTranspiler)));
            deduplicating = true;
            log.Notification("Deduplicating crafting grid recipe candidates with a hash set");
        }
        else log.Warning("Not deduplicating crafting grid recipe candidates: expected one AddIfNotPresent<GridRecipe> call, found {0}", adds);


        // One call per pass: shaped recipes, then shapeless
        int matches = original.Count(i => i.Calls(VanillaMatches));
        if (matches == 2)
        {
            harmony.Patch(find, transpiler: new HarmonyMethod(typeof(CraftingGridFix), nameof(MatchesTranspiler)));
            prechecking = true;
            log.Notification("Skipping shaped crafting grid recipes whose ingredient count differs from the filled slots");
        }
        else log.Warning("Not prechecking crafting grid recipes: expected two GridRecipe.Matches calls, found {0}", matches);
    }

    private static readonly MethodInfo VanillaAdd = AccessTools.Method(typeof(ListExtensions), nameof(ListExtensions.AddIfNotPresent))
        .MakeGenericMethod(typeof(GridRecipe));

    private static readonly MethodInfo VanillaMatches = AccessTools.Method(typeof(GridRecipe), nameof(GridRecipe.Matches),
        [typeof(IPlayer), typeof(IWorldAccessor), typeof(ItemSlot[]), typeof(int)]);

    private static IEnumerable<CodeInstruction> DedupTranspiler(IEnumerable<CodeInstruction> instructions) =>
        Swap(instructions, VanillaAdd, AccessTools.Method(typeof(CraftingGridFix), nameof(AddIfNotPresent)));

    private static IEnumerable<CodeInstruction> MatchesTranspiler(IEnumerable<CodeInstruction> instructions) =>
        Swap(instructions, VanillaMatches, AccessTools.Method(typeof(CraftingGridFix), nameof(Matches)));

    private static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions, MethodInfo from, MethodInfo to)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(from))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = to;
            }
            yield return instruction;
        }
    }

    // GridRecipe.Matches, called as a static with the recipe first so the stack is unchanged
    private static bool Matches(GridRecipe recipe, IPlayer forPlayer, IWorldAccessor world, ItemSlot[] slots, int gridWidth)
    {
        // MatchesAtPosition wants a stack in every slot the pattern has an ingredient for and
        // none elsewhere. A subclass may match differently, and shapeless recipes merge stacks.
        if (!recipe.Shapeless && recipe.GetType() == typeof(GridRecipe) && recipe.ResolvedIngredients is { } ingredients)
        {
            int required = 0, filled = 0;
            foreach (CraftingRecipeIngredient ingredient in ingredients) if (ingredient != null) required++;
            foreach (ItemSlot slot in slots) if (slot.Itemstack != null) filled++;
            if (required != filled) return false;
        }
        return recipe.Matches(forPlayer, world, slots, gridWidth);
    }

    private static void AddIfNotPresent(List<GridRecipe> list, GridRecipe recipe)
    {
        if (recipe == null)
        {
            list.AddIfNotPresent(recipe);
            return;
        }
        // Each call builds a fresh list; holding the last one keeps its reference unique
        if (!ReferenceEquals(list, currentList))
        {
            currentList = list;
            (seen ??= new HashSet<GridRecipe>(ReferenceEqualityComparer.Instance)).Clear();
        }
        if (seen.Add(recipe)) list.Add(recipe);
    }

    private static void Start(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void End(long __state)
    {
        double ms = Stopwatch.GetElapsedTime(__state).TotalMilliseconds;
        int count = !deduplicating ? -1 : currentList != null ? seen.Count : 0;
        currentList = null;   // don't pin the list between calls
        if (ms >= 5)
        {
            logger.Notification("Crafting grid recipe lookup took {0:F1} ms{1} ({2})",
                ms, count >= 0 ? $" over {count} candidate recipes" : "", Mode);
        }
    }
}
