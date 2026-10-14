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

namespace lodestone;

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
///
/// Improved Handbook Recipe Helper rewrites that loop to collect every matching recipe, shaped
/// ones then shapeless, and let the player cycle between them through its Inject. The
/// indexed lookup gives the same list in the same order, so for the versions read it
/// collects the list itself and hands it to Inject.
///
/// Both of those keep vanilla's loop. For the player's 3x3 grid a prefix replaces it:
/// GridLookupIndex gives the recipes that could match, in vanilla's list order, so the
/// first that Matches is vanilla's recipe. Anything else, or a slot holding an empty
/// stack, which vanilla's loop and Matches see differently, falls back to the loop.
/// </summary>
internal static class CraftingGridFix
{
    private static ILogger logger;
    private static bool deduplicating;
    private static bool prechecking;
    private static bool indexing;
    private static bool optiTimeThere;

    // Improved Handbook Recipe Helper's choice among every match, when the lookup collects for it
    private static System.Func<GridRecipe, List<GridRecipe>, ICoreAPI, IPlayer, GridRecipe> injectChoice;

    [ThreadStatic] private static List<GridRecipe> currentList;
    [ThreadStatic] private static HashSet<GridRecipe> seen;

    // The indexed lookup's scratch, and how many recipes it tried on the last call
    [ThreadStatic] private static List<int>[] slotKeys;
    [ThreadStatic] private static List<(GridRecipe recipe, int slot, int key, int pos)> shaped;
    [ThreadStatic] private static HashSet<GridRecipe> considered;
    [ThreadStatic] private static int indexedTries;

    public static string Mode
    {
        get
        {
            string[] on = [indexing ? (injectChoice != null ? "indexed for Improved Handbook Recipe Helper" : "indexed") : null,
                deduplicating ? "hashed" : null, prechecking ? "counted" : null];
            return on.Any(m => m != null) ? string.Join(", ", on.Where(m => m != null)) : "vanilla";
        }
    }

    public static void Apply(Harmony harmony, Mod mod, bool takeOverFromOptiTime)
    {
        MethodInfo find = AccessTools.Method(typeof(InventoryCraftingGrid), "FindMatchingRecipe");
        if (Harmony.GetPatchInfo(find)?.Owners.Contains(harmony.Id) == true) return;
        ILogger log = logger = mod.Logger;
        Patch optiTime = Harmony.GetPatchInfo(find)?.Prefixes.FirstOrDefault(p => p.PatchMethod.DeclaringType?.FullName == OptiTimeLookup);
        if (optiTime != null && !takeOverFromOptiTime)
        {
            log.Notification("Leaving the crafting grid recipe lookup to OptiTime (CraftingGridOverOptiTime is off)");
            return;
        }

        var start = new HarmonyMethod(typeof(CraftingGridFix), nameof(Start));
        var end = new HarmonyMethod(typeof(CraftingGridFix), nameof(End));
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

        optiTimeThere = optiTime != null;

        // OptiTime replaces the method with a prefix that returns false, so the body these
        // transpilers rewrite would never run. Run a rewritten copy of vanilla's body from a
        // prefix ahead of OptiTime's instead; returning false then skips both. Its candidate
        // order differs from vanilla's, and it reuses the last grid's recipe before trying
        // shaped ones first, so it can pick a different recipe from a server without it.
        if (optiTime != null)
        {
            Harmony.ReversePatch(find, new HarmonyMethod(typeof(CraftingGridFix), nameof(FindMatchingRecipe)),
                AccessTools.Method(typeof(CraftingGridFix), nameof(ReverseTranspiler)));
        }
        else
        {
            if (deduplicating) harmony.Patch(find, transpiler: new HarmonyMethod(typeof(CraftingGridFix), nameof(DedupTranspiler)));
            if (prechecking) harmony.Patch(find, transpiler: new HarmonyMethod(typeof(CraftingGridFix), nameof(MatchesTranspiler)));
        }

        // The indexed lookup skips vanilla's body, and every prefix after it, so it stays off
        // where another mod rewrites the method: a transpiler on the body, or a prefix that
        // can skip it. OptiTime's is the one it runs in place of.
        Patches info = Harmony.GetPatchInfo(find);
        string[] rewriters = (info == null ? [] : info.Transpilers.Concat(info.Prefixes.Where(p => p.PatchMethod.ReturnType == typeof(bool))))
            .Where(p => p.owner != harmony.Id && p.PatchMethod.DeclaringType?.FullName != OptiTimeLookup)
            .Select(p => p.owner).Distinct().ToArray();
        injectChoice = optiTime == null && rewriters is [RecipeCyclingId] ? RecipeCycling(info, log) : null;
        if (injectChoice != null) rewriters = [];
        indexing = rewriters.Length == 0 && FoundMatch != null && GridSizeRef != null;
        if (rewriters.Length == 0)
        {
            var lookup = new HarmonyMethod(typeof(CraftingGridFix), nameof(Lookup)) { priority = Priority.First, before = optiTime == null ? [] : [optiTime.owner] };
            harmony.Patch(find, prefix: lookup);
        }
        if (rewriters.Length > 0) log.Notification("Leaving the crafting grid recipe lookup to {0}, which already rewrites it ({1})", string.Join(", ", rewriters), Mode);
        else if (injectChoice != null) log.Notification("Finding crafting grid recipes faster, collecting every match for Improved Handbook Recipe Helper's recipe cycling ({0})", Mode);
        else if (optiTime != null) log.Notification("Finding crafting grid recipes in place of OptiTime's recipe lookup, which can pick a different recipe from vanilla's ({0}). OptiTime's other optimizations are unaffected", Mode);
        else log.Notification("Finding crafting grid recipes faster ({0})", Mode);
    }

    // ---- the indexed lookup ----

    private static readonly AccessTools.FieldRef<InventoryCraftingGrid, int> GridSizeRef = AccessTools.FieldRefAccess<InventoryCraftingGrid, int>("GridSize");
    private static readonly AccessTools.FieldRef<InventoryCraftingGrid, int> GridSizeSqRef = AccessTools.FieldRefAccess<InventoryCraftingGrid, int>("GridSizeSq");
    private static readonly AccessTools.FieldRef<InventoryCraftingGrid, ItemSlot[]> SlotsRef = AccessTools.FieldRefAccess<InventoryCraftingGrid, ItemSlot[]>("slots");
    private static readonly AccessTools.FieldRef<InventoryCraftingGrid, ItemSlotCraftingOutput> OutputRef = AccessTools.FieldRefAccess<InventoryCraftingGrid, ItemSlotCraftingOutput>("outputSlot");
    private static readonly AccessTools.FieldRef<InventoryBasePlayer, string> PlayerUidRef = AccessTools.FieldRefAccess<InventoryBasePlayer, string>("playerUID");
    private static readonly Action<InventoryCraftingGrid, GridRecipe> FoundMatch =
        AccessTools.MethodDelegate<Action<InventoryCraftingGrid, GridRecipe>>(AccessTools.Method(typeof(InventoryCraftingGrid), "FoundMatch"));

    // Ahead of everything else on the method: false when it found vanilla's recipe itself
    private static bool Lookup(InventoryCraftingGrid __instance)
    {
        if (indexing && Indexed(__instance)) return false;
        if (!optiTimeThere) return true;   // vanilla's body, with the transpilers
        FindMatchingRecipe(__instance);    // a copy of it, as OptiTime's prefix would skip it
        return false;
    }

    /// <summary>FindMatchingRecipe for a 3x3 grid, through GridLookupIndex; false to leave it to vanilla's loop.</summary>
    private static bool Indexed(InventoryCraftingGrid grid)
    {
        IWorldAccessor world = grid.Api?.World;
        ItemSlot[] slots = SlotsRef(grid);
        if (world?.FastSearchRecipesByIngredient == null || GridSizeRef(grid) != 3 || slots is not { Length: 9 }) return false;
        foreach (ItemSlot slot in slots)
        {
            if (slot.Itemstack is { StackSize: 0 }) return false;
        }

        GridLookupIndex index = GridLookupIndex.For(world);
        if (slotKeys == null)
        {
            slotKeys = new List<int>[9];
            shaped = [];
            considered = new HashSet<GridRecipe>(ReferenceEqualityComparer.Instance);
        }
        List<int>[] keys = new List<int>[9];
        for (int i = 0; i < 9; i++)
        {
            if (!GridLookupIndex.Filled(slots[i])) continue;
            index.KeysFor(slots[i].Itemstack, slotKeys[i] ??= []);
            keys[i] = slotKeys[i];
        }

        // As vanilla does before it searches; Improved Handbook Recipe Helper reads the recipe first
        GridRecipe previous = grid.MatchingRecipe;
        grid.MatchingRecipe = null;
        OutputRef(grid).Itemstack = null;
        IPlayer player = world.PlayerByUid(PlayerUidRef(grid));

        int tries = 0;
        index.ShapedCandidates(keys, GridLookupIndex.GridShape(slots), shaped, considered);
        if (injectChoice != null)
        {
            var matches = new List<GridRecipe>();
            foreach (var candidate in shaped)
            {
                tries++;
                if (candidate.recipe.Matches(player, world, slots, 3)) matches.Add(candidate.recipe);
            }
            foreach (GridRecipe recipe in index.ShapelessCandidates(keys, considered))
            {
                tries++;
                if (recipe.Matches(player, world, slots, 3)) matches.Add(recipe);
            }
            if (matches.Count > 0) FoundMatch(grid, injectChoice(previous, matches, grid.Api, grid.Player));
            grid.dirtySlots.Add(GridSizeSqRef(grid));
            indexedTries = tries;
            return true;
        }
        foreach (var candidate in shaped)
        {
            tries++;
            if (candidate.recipe.Matches(player, world, slots, 3)) return Found(grid, candidate.recipe, tries);
        }
        foreach (GridRecipe recipe in index.ShapelessCandidates(keys, considered))
        {
            tries++;
            if (recipe.Matches(player, world, slots, 3)) return Found(grid, recipe, tries);
        }
        grid.dirtySlots.Add(GridSizeSqRef(grid));
        indexedTries = tries;
        return true;
    }

    private static bool Found(InventoryCraftingGrid grid, GridRecipe recipe, int tries)
    {
        FoundMatch(grid, recipe);
        indexedTries = tries;
        return true;
    }

    // OptiTime's patch class, which is steadier than its Harmony id
    private const string OptiTimeLookup = "OptiTime.RecipeLookupCacheOptimization";

    // ---- Improved Handbook Recipe Helper ----

    private const string RecipeCyclingId = "improvedhandbookrecipes";
    private const string RecipeCyclingModId = "improvedhandbookrecipesfork";
    private const string RecipeCyclingChecked = "1227.0.";
    private const string RecipeCyclingPatch = "ImprovedHandbookRecipes.Patch_InventoryCraftingGrid_FindMatchingRecipe";

    /// <summary>
    /// Improved Handbook Recipe Helper's Inject, when its only rewrite of the lookup is the
    /// transpiler read for these versions: one that turns each FoundMatch into collecting
    /// the match, then calls FoundMatch with Inject's choice when there were any. Else null,
    /// and the lookup is left to it.
    /// </summary>
    private static System.Func<GridRecipe, List<GridRecipe>, ICoreAPI, IPlayer, GridRecipe> RecipeCycling(Patches info, ILogger log)
    {
        string version = LodestoneModSystem.ModLoader?.GetMod(RecipeCyclingModId)?.Info.Version;
        bool onlyItsTranspiler = info.Transpilers.Count(p => p.owner == RecipeCyclingId) == 1
            && info.Transpilers.Any(p => p.owner == RecipeCyclingId && p.PatchMethod.DeclaringType?.FullName == RecipeCyclingPatch)
            && !info.Prefixes.Any(p => p.owner == RecipeCyclingId && p.PatchMethod.ReturnType == typeof(bool));
        MethodInfo inject = AccessTools.Method(AccessTools.TypeByName(RecipeCyclingPatch), "Inject",
            [typeof(GridRecipe), typeof(List<GridRecipe>), typeof(ICoreAPI), typeof(IPlayer)]);
        if (version == null || !version.StartsWith(RecipeCyclingChecked) || !onlyItsTranspiler || inject?.ReturnType != typeof(GridRecipe))
        {
            log.Notification("Not collecting crafting grid matches for Improved Handbook Recipe Helper {0}: only {1}x has been checked", version, RecipeCyclingChecked);
            return null;
        }
        return AccessTools.MethodDelegate<System.Func<GridRecipe, List<GridRecipe>, ICoreAPI, IPlayer, GridRecipe>>(inject);
    }

    // Vanilla's FindMatchingRecipe, as rewritten by ReverseTranspiler
    private static void FindMatchingRecipe(InventoryCraftingGrid instance) => throw new NotImplementedException("Replaced by a reverse patch");

    private static IEnumerable<CodeInstruction> ReverseTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        if (deduplicating) instructions = DedupTranspiler(instructions);
        if (prechecking) instructions = MatchesTranspiler(instructions);
        return instructions;
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

    private static void Start(out long __state)
    {
        indexedTries = -1;   // set only by a lookup the index answers
        __state = Stopwatch.GetTimestamp();
    }

    private static void End(long __state)
    {
        double ms = Stopwatch.GetElapsedTime(__state).TotalMilliseconds;
        string detail = indexedTries >= 0 ? $", trying {indexedTries} recipes"
            : deduplicating && currentList != null ? $" over {seen.Count} candidate recipes" : "";
        currentList = null;   // don't pin the list between calls
        if (ms >= 5 && LodestoneModSystem.Config.LogTimings)
        {
            logger.Notification("Crafting grid recipe lookup took {0:F1} ms{1} ({2})", ms, detail, Mode);
        }
    }
}
