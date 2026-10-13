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
        // Ahead of any prefix that replaces the method, so the timing covers whatever runs
        start.priority = Priority.First + 1;
        harmony.Patch(find, prefix: start, postfix: end);
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(find);

        int adds = original.Count(i => i.Calls(VanillaAdd));
        deduplicating = adds == 1;
        if (!deduplicating) log.Warning("Not deduplicating crafting grid recipe candidates: expected one AddIfNotPresent<GridRecipe> call, found {0}", adds);

        // One call per pass: shaped recipes, then shapeless
        int matches = original.Count(i => i.Calls(VanillaMatches));
        prechecking = matches == 2;
        if (!prechecking) log.Warning("Not prechecking crafting grid recipes: expected two GridRecipe.Matches calls, found {0}", matches);

        if (!deduplicating && !prechecking) return;

        // OptiTime replaces the method with a prefix that returns false, so the body these
        // transpilers rewrite would never run. Run a rewritten copy of vanilla's body from a
        // prefix ahead of OptiTime's instead; returning false then skips both. Its candidate
        // order differs from vanilla's, and it reuses the last grid's recipe before trying
        // shaped ones first, so it can pick a different recipe from a server without it.
        Patch optiTime = Harmony.GetPatchInfo(find)?.Prefixes.FirstOrDefault(p => p.PatchMethod.DeclaringType?.FullName == OptiTimeLookup);
        if (optiTime != null)
        {
            Harmony.ReversePatch(find, new HarmonyMethod(typeof(CraftingGridFix), nameof(FindMatchingRecipe)),
                AccessTools.Method(typeof(CraftingGridFix), nameof(ReverseTranspiler)));
            var takeOver = new HarmonyMethod(typeof(CraftingGridFix), nameof(TakeOver)) { priority = Priority.First, before = [optiTime.owner] };
            harmony.Patch(find, prefix: takeOver);
            log.Notification("Finding crafting grid recipes in place of OptiTime's recipe lookup, which can pick a different recipe from vanilla's ({0}). OptiTime's other optimizations are unaffected", Mode);
            return;
        }

        if (deduplicating) harmony.Patch(find, transpiler: new HarmonyMethod(typeof(CraftingGridFix), nameof(DedupTranspiler)));
        if (prechecking) harmony.Patch(find, transpiler: new HarmonyMethod(typeof(CraftingGridFix), nameof(MatchesTranspiler)));
        log.Notification("Finding crafting grid recipes faster ({0})", Mode);
    }

    // OptiTime's patch class, which is steadier than its Harmony id
    private const string OptiTimeLookup = "OptiTime.RecipeLookupCacheOptimization";

    // Vanilla's FindMatchingRecipe, as rewritten by ReverseTranspiler
    private static void FindMatchingRecipe(InventoryCraftingGrid instance) => throw new NotImplementedException("Replaced by a reverse patch");

    private static IEnumerable<CodeInstruction> ReverseTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        if (deduplicating) instructions = DedupTranspiler(instructions);
        if (prechecking) instructions = MatchesTranspiler(instructions);
        return instructions;
    }

    private static bool TakeOver(InventoryCraftingGrid __instance)
    {
        FindMatchingRecipe(__instance);
        return false;
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
