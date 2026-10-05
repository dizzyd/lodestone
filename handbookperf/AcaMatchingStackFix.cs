using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace handbookperf;

/// <summary>
/// A Culinary Artillery replaces CookingRecipeIngredient.GetMatchingStack with a prefix
/// that tests an input stack against every valid stack of the ingredient, building a
/// fresh ignored-attributes array on each call. The handbook's meal-recipe caching
/// calls it for every recipe x ingredient x handbook stack, which in a large pack costs
/// the page-loading thread over a minute: two thirds collections, one third comparing
/// stacks that cannot match.
///
/// The replacement keeps ACA's semantics and order but builds the array once and
/// indexes each ingredient's valid stacks by item, so an input is only compared against
/// stacks of the same item and the wildcards. Collectibles that override Equals take
/// the original full scan.
/// </summary>
internal static class AcaMatchingStackFix
{
    private const string AcaHarmonyId = "com.jakecool19.efrecipes.cookingoverhaul";

    // HANDBOOKPERF_NOINDEX=1 keeps the cached array but scans every valid stack, for A/B timing
    private static readonly bool indexed = Environment.GetEnvironmentVariable("HANDBOOKPERF_NOINDEX") != "1";
    private static bool applied;

    public static string Mode => !applied ? "ACA's own matcher" : indexed ? "indexed" : "full scan";

    public static void Apply(ICoreClientAPI capi, Mod mod)
    {
        MethodInfo getMatchingStack = AccessTools.Method(typeof(CookingRecipeIngredient), nameof(CookingRecipeIngredient.GetMatchingStack));
        var acaPrefixes = Harmony.GetPatchInfo(getMatchingStack)?.Prefixes.Count(p => p.owner == AcaHarmonyId) ?? 0;
        applied = false;
        if (acaPrefixes == 0) return;

        var harmony = new Harmony(mod.Info.ModID);
        harmony.Unpatch(getMatchingStack, HarmonyPatchType.Prefix, AcaHarmonyId);
        // A copy left by an earlier world join may be another build of this mod, so
        // replace it rather than keep it.
        harmony.Unpatch(getMatchingStack, HarmonyPatchType.Prefix, mod.Info.ModID);
        harmony.Patch(getMatchingStack, prefix: new HarmonyMethod(typeof(AcaMatchingStackFix), nameof(Prefix)));
        applied = true;
        mod.Logger.Notification("Replaced {0}'s CookingRecipeIngredient.GetMatchingStack prefix ({1}) with a {2} copy",
            AcaHarmonyId, acaPrefixes, indexed ? "indexed" : "full-scan");
    }

    public static bool Prefix(ItemStack inputStack, ref CookingRecipeStack __result, CookingRecipeIngredient __instance)
    {
        __result = null;
        if (inputStack == null) return false;

        CollectibleObject collectible = inputStack.Collectible;
        __result = !indexed || collectible == null || OverridesEquals(collectible)
            ? FullScan(inputStack, __instance)
            : IndexedScan(inputStack, collectible, __instance);
        return false;
    }

    // ACA's prefix, unchanged bar the cached array
    private static CookingRecipeStack FullScan(ItemStack inputStack, CookingRecipeIngredient ingredient)
    {
        string[] ignoredAttributes = IgnoredStackAttributes();
        foreach (CookingRecipeStack valid in ingredient.ValidStacks)
        {
            bool isWildCard = valid.Code.Path.Contains('*');
            bool found =
                (isWildCard && inputStack.Collectible.WildCardMatch(valid.Code))
                || (!isWildCard && inputStack.Equals(ingredient.world, valid.ResolvedItemstack, ignoredAttributes))
                || (valid.CookedStack?.ResolvedItemstack is ItemStack cooked && inputStack.Equals(ingredient.world, cooked, ignoredAttributes));

            if (found) return valid;
        }
        return null;
    }

    private static CookingRecipeStack IndexedScan(ItemStack inputStack, CollectibleObject collectible, CookingRecipeIngredient ingredient)
    {
        CookingRecipeStack[] validStacks = ingredient.ValidStacks;
        IngredientIndex index = IndexFor(ingredient, validStacks);
        index.ByItem.TryGetValue(ItemKey(inputStack.Class, inputStack.Id), out Step[] steps);
        steps ??= [];
        Step[] wildcards = index.Wildcards;
        string[] ignoredAttributes = IgnoredStackAttributes();

        // Both lists are in valid-stack order, so merging them visits candidates in the
        // order the full scan would, and the first hit is the same one.
        int s = 0, w = 0;
        while (s < steps.Length || w < wildcards.Length)
        {
            Step step = w < wildcards.Length && (s >= steps.Length || wildcards[w].Index <= steps[s].Index)
                ? wildcards[w++]
                : steps[s++];

            CookingRecipeStack valid = validStacks[step.Index];
            bool found = step.Kind switch
            {
                StepKind.Wildcard => collectible.WildCardMatch(valid.Code),
                StepKind.Direct => inputStack.Equals(ingredient.world, valid.ResolvedItemstack, ignoredAttributes),
                _ => valid.CookedStack?.ResolvedItemstack is ItemStack cooked && inputStack.Equals(ingredient.world, cooked, ignoredAttributes),
            };
            if (found) return valid;
        }
        return null;
    }

    // ---- ingredient index ----

    private enum StepKind : byte { Wildcard, Direct, Cooked }

    private readonly record struct Step(int Index, StepKind Kind);

    /// <summary>For one ValidStacks array: the steps worth taking per item, and the wildcards.</summary>
    private sealed class IngredientIndex(CookingRecipeStack[] validStacks)
    {
        public readonly CookingRecipeStack[] ValidStacks = validStacks;
        public readonly Dictionary<long, Step[]> ByItem = [];
        public Step[] Wildcards = [];
    }

    // Resolve() replaces ValidStacks with a new array, which rebuilds the index
    private static readonly ConditionalWeakTable<CookingRecipeIngredient, IngredientIndex> indexes = new();

    private static IngredientIndex IndexFor(CookingRecipeIngredient ingredient, CookingRecipeStack[] validStacks)
    {
        if (indexes.TryGetValue(ingredient, out IngredientIndex index) && ReferenceEquals(index.ValidStacks, validStacks)) return index;

        index = BuildIndex(validStacks);
        indexes.AddOrUpdate(ingredient, index);
        return index;
    }

    private static IngredientIndex BuildIndex(CookingRecipeStack[] validStacks)
    {
        var index = new IngredientIndex(validStacks);
        var byItem = new Dictionary<long, List<Step>>();
        var wildcards = new List<Step>();

        void Add(ItemStack stack, Step step)
        {
            if (stack == null) return;   // the full scan's Equals(world, null) is false too
            long key = ItemKey(stack.Class, stack.Id);
            if (!byItem.TryGetValue(key, out List<Step> steps)) byItem[key] = steps = [];
            steps.Add(step);
        }

        for (int i = 0; i < validStacks.Length; i++)
        {
            CookingRecipeStack valid = validStacks[i];
            if (valid.Code.Path.Contains('*')) wildcards.Add(new Step(i, StepKind.Wildcard));
            else Add(valid.ResolvedItemstack, new Step(i, StepKind.Direct));
            Add(valid.CookedStack?.ResolvedItemstack, new Step(i, StepKind.Cooked));
        }

        foreach (var (key, steps) in byItem) index.ByItem[key] = steps.ToArray();
        index.Wildcards = wildcards.ToArray();
        return index;
    }

    // CollectibleObject.Equals matches only on equal Class and Id, which is what makes
    // the index sound; anything that overrides it may differ, so it gets the full scan.
    private static readonly ConcurrentDictionary<Type, bool> overridesEquals = new();
    private static readonly Type[] equalsSignature = [typeof(ItemStack), typeof(ItemStack), typeof(string[])];

    private static bool OverridesEquals(CollectibleObject collectible) =>
        overridesEquals.GetOrAdd(collectible.GetType(), type =>
            type.GetMethod(nameof(CollectibleObject.Equals), equalsSignature)?.DeclaringType != typeof(CollectibleObject));

    private static long ItemKey(EnumItemClass itemClass, int id) => ((long)itemClass << 32) | (uint)id;

    // ---- ignored attributes ----

    private sealed class IgnoredAttributes(string[] source)
    {
        public readonly string[] Source = source;
        public readonly string[] Merged = [.. source, "madeWith", "expandedSats", "timeFrozen"];
    }

    // Rebuilt if anything ever swaps GlobalConstants.IgnoredStackAttributes for a new array
    private static IgnoredAttributes ignored = new(GlobalConstants.IgnoredStackAttributes);

    private static string[] IgnoredStackAttributes()
    {
        var current = ignored;
        string[] source = GlobalConstants.IgnoredStackAttributes;
        if (!ReferenceEquals(current.Source, source)) ignored = current = new IgnoredAttributes(source);
        return current.Merged;
    }
}
