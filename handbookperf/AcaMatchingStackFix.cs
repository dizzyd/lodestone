using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
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
///
/// That still leaves the handbook's CreateCachedMealRecipeStacks (vanilla's, which runs
/// first and caches, and ACA's) calling it for every recipe x ingredient x handbook
/// stack: some 50 million calls, nearly all of which find nothing. While this copy is
/// the only patch on GetMatchingStack, the stack array those loops walk is swapped, per
/// ingredient, for the stacks the index says could match, in the same order, so the
/// calls that remain find the same stacks.
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

        if (indexed) NarrowMealRecipeStacks(harmony, mod);
    }

    // ---- ACA's handbook meal-recipe caching ----

    private static MethodInfo getMatchingStackMethod;
    private static readonly MethodInfo TryGetStacks = AccessTools.Method(typeof(ObjectCacheUtil), nameof(ObjectCacheUtil.TryGet))
        .MakeGenericMethod(typeof(ItemStack[]));

    private static void NarrowMealRecipeStacks(Harmony harmony, Mod mod)
    {
        getMatchingStackMethod = AccessTools.Method(typeof(CookingRecipeIngredient), nameof(CookingRecipeIngredient.GetMatchingStack));
        Narrow(harmony, mod, "the handbook's", AccessTools.Method(typeof(ModSystemSurvivalHandbook), "CreateCachedMealRecipeStacks"));
        Narrow(harmony, mod, "ACA's", AccessTools.TypeByName("ACulinaryArtillery.Util.HandbookInfoExtensions") is Type extensions
            ? AccessTools.Method(extensions, "CreateCachedMealRecipeStacks") : null);
    }

    private static void Narrow(Harmony harmony, Mod mod, string whose, MethodInfo create)
    {
        // The work is in the lambda it hands ObjectCacheUtil.GetOrCreate
        MethodInfo body = create == null ? null : PatchProcessor.GetOriginalInstructions(create)
            .Where(i => i.opcode == OpCodes.Ldftn).Select(i => i.operand).OfType<MethodInfo>()
            .FirstOrDefault(m => m.Name.Contains("CreateCachedMealRecipeStacks"));
        if (body == null)
        {
            mod.Logger.Warning("Not narrowing {0} meal-recipe matching: its CreateCachedMealRecipeStacks has changed", whose);
            return;
        }

        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(body);
        int loads = code.Count(IsStacksLoad);
        int calls = code.Count(i => i.Calls(getMatchingStackMethod));
        if (loads != 1 || calls != 1)
        {
            mod.Logger.Warning("Not narrowing {0} meal-recipe matching: expected one load of the handbook's stacks and one GetMatchingStack call, found {1} and {2}", whose, loads, calls);
            return;
        }
        harmony.Patch(body, transpiler: new HarmonyMethod(typeof(AcaMatchingStackFix), nameof(NarrowTranspiler)));
        mod.Logger.Notification("Matching each of {0} meal ingredients only against the handbook stacks that could satisfy it", whose);
    }

    // Vanilla asks ObjectCacheUtil for the stacks inside the loop; ACA's lambda captured them
    private static bool IsStacksLoad(CodeInstruction i) =>
        i.Calls(TryGetStacks) || i.opcode == OpCodes.Ldfld && i.operand is FieldInfo { Name: "allstacks" } f && f.FieldType == typeof(ItemStack[]);

    // After the stack array is loaded for the inner loop, load the ingredient the loop's
    // GetMatchingStack call is made on and hand both to Candidates
    private static IEnumerable<CodeInstruction> NarrowTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        int call = code.FindIndex(i => i.Calls(getMatchingStackMethod));
        CodeInstruction ingredient = call >= 2 && code[call - 2].IsLdloc() ? code[call - 2] : null;
        int load = code.FindIndex(IsStacksLoad);
        if (ingredient == null || load < 0) return code;

        code.InsertRange(load + 1,
        [
            new CodeInstruction(ingredient.opcode, ingredient.operand),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AcaMatchingStackFix), nameof(Candidates))),
        ]);
        return code;
    }

    /// <summary>
    /// The stacks in <paramref name="allstacks"/>, in order, that GetMatchingStack could
    /// match <paramref name="ingredient"/> with; every other one it answers null. All of
    /// them unless this mod's copy is the only patch on GetMatchingStack.
    /// </summary>
    private static ItemStack[] Candidates(ItemStack[] allstacks, CookingRecipeIngredient ingredient)
    {
        if (allstacks == null || ingredient?.ValidStacks == null || !OnlyOurCopy()) return allstacks;

        HandbookStacks stacks = HandbookStacks.For(allstacks);
        IngredientIndex index = IndexFor(ingredient, ingredient.ValidStacks);
        var positions = new List<int>(stacks.Always);
        foreach (long item in index.ByItem.Keys)
        {
            if (stacks.ByItem.TryGetValue(item, out int[] found)) positions.AddRange(found);
        }
        foreach (Step wildcard in index.Wildcards)
        {
            AssetLocation pattern = ingredient.ValidStacks[wildcard.Index].Code;
            for (int i = 0; i < stacks.Codes.Length; i++)
            {
                if (stacks.Codes[i] != null && MightMatch(pattern, stacks.Codes[i])) positions.Add(i);
            }
        }
        positions.Sort();

        var narrowed = new List<ItemStack>(positions.Count);
        for (int i = 0; i < positions.Count; i++)
        {
            if (i == 0 || positions[i] != positions[i - 1]) narrowed.Add(allstacks[positions[i]]);
        }
        return narrowed.ToArray();
    }

    private static bool OnlyOurCopy()
    {
        Patches patches = Harmony.GetPatchInfo(getMatchingStackMethod);
        return applied && indexed && patches != null
            && patches.Prefixes.Count == 1 && patches.Prefixes[0].PatchMethod.DeclaringType == typeof(AcaMatchingStackFix)
            && patches.Postfixes.Count == 0 && patches.Transpilers.Count == 0 && patches.Finalizers.Count == 0;
    }

    /// <summary>
    /// Whether CollectibleObject.WildCardMatch(<paramref name="pattern"/>) could be true for
    /// <paramref name="code"/>. WildcardUtil.Match wants the same domain, unless the
    /// pattern's is "*", and the text before the first '*' at the start of the path, in
    /// either ASCII case. '@' patterns are regexes, and fastMatch has its own case rules
    /// outside ASCII, so both of those are always tried.
    /// </summary>
    private static bool MightMatch(AssetLocation pattern, AssetLocation code)
    {
        if (pattern.Domain != "*" && pattern.Domain != code.Domain) return false;
        string path = pattern.Path;
        if (path.Length > 0 && path[0] == '@') return true;
        int star = path.IndexOf('*');
        ReadOnlySpan<char> prefix = path.AsSpan(0, star < 0 ? path.Length : star);
        foreach (char c in prefix) if (c >= 0x80) return true;
        return code.Path.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The handbook's stacks by item, and those GetMatchingStack would test against every valid stack.</summary>
    private sealed class HandbookStacks
    {
        public readonly Dictionary<long, int[]> ByItem = [];
        public readonly int[] Always;
        public readonly AssetLocation[] Codes;

        private HandbookStacks(ItemStack[] allstacks)
        {
            var byItem = new Dictionary<long, List<int>>();
            var always = new List<int>();
            Codes = new AssetLocation[allstacks.Length];
            for (int i = 0; i < allstacks.Length; i++)
            {
                ItemStack stack = allstacks[i];
                CollectibleObject collectible = stack?.Collectible;
                Codes[i] = collectible?.Code;
                if (stack == null) continue;   // GetMatchingStack(null) is null
                if (collectible == null || OverridesEquals(collectible)) always.Add(i);
                else
                {
                    long key = ItemKey(stack.Class, stack.Id);
                    if (!byItem.TryGetValue(key, out List<int> list)) byItem[key] = list = [];
                    list.Add(i);
                }
            }
            foreach (var (key, list) in byItem) ByItem[key] = list.ToArray();
            Always = always.ToArray();
        }

        private static readonly ConditionalWeakTable<ItemStack[], HandbookStacks> cache = new();

        public static HandbookStacks For(ItemStack[] allstacks) => cache.GetValue(allstacks, a => new HandbookStacks(a));
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
