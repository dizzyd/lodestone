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
/// </summary>
internal static class CraftingGridFix
{
    private static ILogger logger;
    private static bool deduplicating;

    [ThreadStatic] private static List<GridRecipe> currentList;
    [ThreadStatic] private static HashSet<GridRecipe> seen;

    public static string Mode => deduplicating ? "hashed" : "vanilla";

    public static void Apply(Harmony harmony, ILogger log)
    {
        logger = log;
        MethodInfo find = AccessTools.Method(typeof(InventoryCraftingGrid), "FindMatchingRecipe");
        var start = new HarmonyMethod(typeof(CraftingGridFix), nameof(Start));
        var end = new HarmonyMethod(typeof(CraftingGridFix), nameof(End));
        if (Environment.GetEnvironmentVariable("HANDBOOKPERF_NOCRAFTFIX") == "1")
        {
            harmony.Patch(find, prefix: start, postfix: end);
            log.Notification("Crafting grid fix off (HANDBOOKPERF_NOCRAFTFIX=1); timing only");
            return;
        }
        int calls = PatchProcessor.GetOriginalInstructions(find).Count(i => i.Calls(VanillaAdd));
        if (calls != 1)
        {
            harmony.Patch(find, prefix: start, postfix: end);
            log.Warning("Leaving the crafting grid recipe lookup alone: expected one AddIfNotPresent<GridRecipe> call, found {0}", calls);
            return;
        }
        harmony.Patch(find, prefix: start, postfix: end, transpiler: new HarmonyMethod(typeof(CraftingGridFix), nameof(Transpiler)));
        deduplicating = true;
        log.Notification("Deduplicating crafting grid recipe candidates with a hash set");
    }

    private static readonly MethodInfo VanillaAdd = AccessTools.Method(typeof(ListExtensions), nameof(ListExtensions.AddIfNotPresent))
        .MakeGenericMethod(typeof(GridRecipe));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo ours = AccessTools.Method(typeof(CraftingGridFix), nameof(AddIfNotPresent));
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(VanillaAdd))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = ours;
            }
            yield return instruction;
        }
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
