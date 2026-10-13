using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;

namespace lodestone;

/// <summary>
/// What InventoryCraftingGrid.FindMatchingRecipe needs to find vanilla's recipe without
/// building vanilla's candidate list, for one world's FastSearchRecipesByIngredient.
///
/// Vanilla's answer is the first recipe in its candidate list, shaped ones first, that
/// Matches the grid. The list holds, slot by slot and then in ingredient-key order, every
/// enabled grid recipe of every key a slot's stack satisfies. So any recipe that cannot
/// match may be left out, as long as the rest are tried in that order:
///
/// - A shaped recipe of vanilla's own classes matches only a grid filled exactly where
///   its pattern has ingredients, so it is looked up by the shape of the filled cells.
/// - A stack that satisfies an ingredient also satisfies that ingredient's key when the
///   key is the same test with no attributes. It need not be: the key keeps the stack it
///   was first resolved with, which can carry attributes another recipe's ingredient
///   gave it, and an item's class may override CollectibleObject.Satisfies. So only a
///   recipe whose exact keys are attribute-free, and whose exact ingredients keep
///   vanilla's Satisfies, is also dropped unless every filled slot's stack satisfies one
///   of its keys.
/// - Shapeless recipes merge stacks before matching, so they are tried as vanilla lists
///   them, without a filter.
///
/// Each candidate's place in vanilla's list is the first slot, key and position that
/// would have added it, and candidates are tried in that order.
/// </summary>
internal sealed class GridLookupIndex
{
    private readonly object source;
    private readonly int sourceCount;
    private readonly int recipeCount;

    private readonly IRecipeIngredientBase[] keys;

    // Which keys a stack could satisfy: by item for exact keys, by literal prefix for
    // wildcard keys, and every other key always
    private readonly Dictionary<long, int[]> exactById = [];
    private readonly Dictionary<(EnumItemClass, string), PrefixBucket> wildcards = [];
    private readonly int[] alwaysTest;

    // Per key, in the key's own recipe order
    private readonly Dictionary<int, GridRecipe[]>[] shapedByShape;
    private readonly GridRecipe[][] shapedOther;
    private readonly GridRecipe[][] shapeless;

    // Per recipe, every (key, position in that key's list) that lists it, by key
    private readonly Dictionary<GridRecipe, (int key, int pos)[]> keysOf = new(ReferenceEqualityComparer.Instance);

    // Shaped recipes whose ingredients are each satisfied only by stacks that satisfy their key
    private readonly HashSet<GridRecipe> keyBound = new(ReferenceEqualityComparer.Instance);

    public int KeyCount => keys.Length;

    private sealed class PrefixBucket
    {
        public readonly Dictionary<string, int[]> Positions = new(StringComparer.Ordinal);
        public Dictionary<string, int[]>.AlternateLookup<ReadOnlySpan<char>> Lookup;
        public int[] Lengths;
    }

    private GridLookupIndex(OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>> dict, int gridRecipes)
    {
        source = dict;
        sourceCount = dict.Count;
        recipeCount = gridRecipes;
        keys = new IRecipeIngredientBase[dict.Count];
        shapedByShape = new Dictionary<int, GridRecipe[]>[dict.Count];
        shapedOther = new GridRecipe[dict.Count][];
        shapeless = new GridRecipe[dict.Count][];

        var overrides = new Dictionary<Type, bool>();
        var byId = new Dictionary<long, List<int>>();
        var byPrefix = new Dictionary<(EnumItemClass, string), Dictionary<string, List<int>>>();
        var always = new List<int>();
        var keysOfLists = new Dictionary<GridRecipe, List<(int, int)>>(ReferenceEqualityComparer.Instance);
        var shapes = new Dictionary<GridRecipe, int>(ReferenceEqualityComparer.Instance);

        int k = 0;
        foreach (var (key, recipes) in dict)
        {
            keys[k] = key;
            IndexKey(key, k, overrides, byId, byPrefix, always);

            var byShape = new Dictionary<int, List<GridRecipe>>();
            var other = new List<GridRecipe>();
            var loose = new List<GridRecipe>();
            for (int pos = 0; pos < recipes.Count; pos++)
            {
                if (recipes[pos] is not GridRecipe recipe) continue;
                if (!keysOfLists.TryGetValue(recipe, out var at)) keysOfLists[recipe] = at = [];
                at.Add((k, pos));

                if (recipe.Shapeless)
                {
                    loose.Add(recipe);
                    continue;
                }
                if (!shapes.TryGetValue(recipe, out int shape)) shapes[recipe] = shape = Shape(recipe);
                if (shape == NeverMatches) continue;
                if (shape == Unknown) other.Add(recipe);
                else
                {
                    if (!byShape.TryGetValue(shape, out var list)) byShape[shape] = list = [];
                    list.Add(recipe);
                }
            }
            shapedByShape[k] = byShape.ToDictionary(e => e.Key, e => e.Value.ToArray());
            shapedOther[k] = other.ToArray();
            shapeless[k] = loose.ToArray();
            k++;
        }

        foreach (var (key, list) in byId) exactById[key] = list.ToArray();
        foreach (var (bucketKey, lists) in byPrefix)
        {
            var bucket = new PrefixBucket();
            foreach (var (prefix, list) in lists) bucket.Positions[prefix] = list.ToArray();
            bucket.Lookup = bucket.Positions.GetAlternateLookup<ReadOnlySpan<char>>();
            bucket.Lengths = bucket.Positions.Keys.Select(p => p.Length).Distinct().Order().ToArray();
            wildcards[bucketKey] = bucket;
        }
        alwaysTest = always.ToArray();
        foreach (var (recipe, at) in keysOfLists) keysOf[recipe] = at.ToArray();   // already in key order
        foreach (var (recipe, shape) in shapes)
        {
            if (shape >= 0 && KeyBound(recipe, keysOf[recipe], overrides)) keyBound.Add(recipe);
        }
    }

    // ---- which keys a stack satisfies ----

    /// <summary>
    /// FastSearchCraftingRecipeIngredient.SatisfiesAsIngredient needs, for an exact key,
    /// its stack's Collectible.Satisfies: the same class and id unless the collectible
    /// overrides it. For a wildcard key, WildcardUtil.Match: the same class, the same
    /// domain unless the key's is "*", and a path starting with the text before the
    /// first '*'. Every other key is always tried.
    /// </summary>
    private static void IndexKey(IRecipeIngredientBase key, int k, Dictionary<Type, bool> overrides,
        Dictionary<long, List<int>> byId, Dictionary<(EnumItemClass, string), Dictionary<string, List<int>>> byPrefix, List<int> always)
    {
        if (key.GetType() != typeof(FastSearchCraftingRecipeIngredient))
        {
            always.Add(k);
        }
        else if (key.MatchingType == EnumRecipeMatchType.Exact)
        {
            ItemStack stack = key.ResolvedItemStack;
            if (stack?.Collectible == null || OverridesSatisfies(stack.Collectible.GetType(), overrides)) always.Add(k);
            else Add(byId, ItemKey(stack.Class, stack.Collectible.Id), k);
        }
        else if (key.MatchingType == EnumRecipeMatchType.Wildcard && key.Code != null && !key.Code.Path.Contains('('))
        {
            int star = key.Code.Path.IndexOf('*');
            if (!byPrefix.TryGetValue((key.Type, key.Code.Domain), out var bucket)) byPrefix[(key.Type, key.Code.Domain)] = bucket = [];
            Add(bucket, star < 0 ? key.Code.Path : key.Code.Path[..star], k);
        }
        else
        {
            always.Add(k);
        }
    }

    /// <summary>The keys <paramref name="stack"/> satisfies, ascending, into <paramref name="satisfied"/>.</summary>
    public void KeysFor(ItemStack stack, List<int> satisfied)
    {
        satisfied.Clear();
        CollectibleObject collectible = stack.Collectible;
        if (exactById.TryGetValue(ItemKey(stack.Class, collectible.Id), out int[] exact)) Confirm(exact, stack, satisfied);
        if (collectible.Code is AssetLocation code)
        {
            Prefixes((stack.Class, code.Domain), code.Path, stack, satisfied);
            Prefixes((stack.Class, "*"), code.Path, stack, satisfied);
        }
        Confirm(alwaysTest, stack, satisfied);
        satisfied.Sort();
    }

    private void Prefixes((EnumItemClass, string) bucketKey, string path, ItemStack stack, List<int> satisfied)
    {
        if (!wildcards.TryGetValue(bucketKey, out PrefixBucket bucket)) return;
        foreach (int length in bucket.Lengths)
        {
            if (length > path.Length) break;
            if (bucket.Lookup.TryGetValue(path.AsSpan(0, length), out int[] found)) Confirm(found, stack, satisfied);
        }
    }

    private void Confirm(int[] candidates, ItemStack stack, List<int> satisfied)
    {
        foreach (int k in candidates)
        {
            if (keys[k].SatisfiesAsIngredient(stack, checkStackSize: false)) satisfied.Add(k);
        }
    }

    // ---- shapes ----

    private const int Unknown = -1;       // not vanilla's matching: always a candidate
    private const int NeverMatches = -2;  // vanilla's Matches is false in a 3x3 grid

    /// <summary>
    /// The cells a shaped recipe fills, moved to the top left of a 3x3 grid, as a 9-bit
    /// mask. GridRecipe.Matches tries the pattern at every offset and MatchesAtPosition
    /// wants a stack exactly where it has an ingredient, so a grid it matches has the
    /// same mask. Only for vanilla's own recipe and ingredient classes.
    /// </summary>
    private static int Shape(GridRecipe recipe)
    {
        if (recipe.GetType() != typeof(GridRecipe)) return Unknown;
        CraftingRecipeIngredient[] ingredients = recipe.ResolvedIngredients;
        if (ingredients == null) return NeverMatches;
        if (recipe.Width > 3 || recipe.Height > 3 || recipe.Width <= 0) return NeverMatches;

        int minRow = int.MaxValue, minCol = int.MaxValue;
        for (int i = 0; i < ingredients.Length; i++)
        {
            if (ingredients[i] == null) continue;
            if (ingredients[i].GetType() != typeof(CraftingRecipeIngredient)) return Unknown;
            minRow = Math.Min(minRow, i / recipe.Width);
            minCol = Math.Min(minCol, i % recipe.Width);
        }
        if (minRow == int.MaxValue) return Unknown;   // no ingredients: leave it to Matches

        int mask = 0;
        for (int i = 0; i < ingredients.Length; i++)
        {
            if (ingredients[i] != null) mask |= 1 << ((i / recipe.Width - minRow) * 3 + i % recipe.Width - minCol);
        }
        return mask;
    }

    // Shape has already checked every ingredient is vanilla's own class. A recipe is
    // listed under each of its ingredients' keys, so checking every key that lists it
    // covers its own.
    private bool KeyBound(GridRecipe recipe, (int key, int pos)[] at, Dictionary<Type, bool> overrides)
    {
        foreach (CraftingRecipeIngredient ingredient in recipe.ResolvedIngredients)
        {
            if (ingredient?.MatchingType != EnumRecipeMatchType.Exact) continue;
            if (ingredient.ResolvedItemStack?.Collectible is not CollectibleObject collectible) return false;
            if (OverridesSatisfies(collectible.GetType(), overrides)) return false;
        }
        foreach (var (k, _) in at)
        {
            if (keys[k].MatchingType == EnumRecipeMatchType.Exact && keys[k].ResolvedItemStack?.Attributes is { Count: > 0 }) return false;
        }
        return true;
    }

    /// <summary>The filled cells of a 3x3 grid, moved to the top left, as Shape makes them.</summary>
    public static int GridShape(ItemSlot[] slots)
    {
        int minRow = 3, minCol = 3;
        for (int i = 0; i < 9; i++)
        {
            if (Filled(slots[i])) { minRow = Math.Min(minRow, i / 3); minCol = Math.Min(minCol, i % 3); }
        }
        int mask = 0;
        for (int i = 0; i < 9; i++)
        {
            if (Filled(slots[i])) mask |= 1 << ((i / 3 - minRow) * 3 + i % 3 - minCol);
        }
        return mask;
    }

    // As vanilla's gather loop skips a slot
    public static bool Filled(ItemSlot slot) => slot.Itemstack is { StackSize: not 0 };

    // ---- candidates ----

    /// <summary>
    /// The shaped recipes that could match, in vanilla's list order. <paramref name="slotKeys"/>
    /// holds each slot's satisfied keys, null for an empty slot.
    /// </summary>
    public void ShapedCandidates(List<int>[] slotKeys, int gridShape, List<(GridRecipe recipe, int slot, int key, int pos)> into, HashSet<GridRecipe> seen)
    {
        into.Clear();
        seen.Clear();

        // Vanilla's list holds a recipe if any slot satisfies any of its keys
        for (int slot = 0; slot < slotKeys.Length; slot++)
        {
            if (slotKeys[slot] == null) continue;
            foreach (int k in slotKeys[slot])
            {
                if (shapedByShape[k].TryGetValue(gridShape, out GridRecipe[] recipes))
                {
                    foreach (GridRecipe recipe in recipes)
                    {
                        if (!recipe.Enabled || !seen.Add(recipe)) continue;
                        if (keyBound.Contains(recipe) && !FedByEverySlot(recipe, slotKeys)) continue;
                        if (Rank(recipe, slotKeys, out var rank)) into.Add((recipe, rank.slot, rank.key, rank.pos));
                    }
                }
                foreach (GridRecipe recipe in shapedOther[k])
                {
                    if (recipe.Enabled && seen.Add(recipe) && Rank(recipe, slotKeys, out var rank)) into.Add((recipe, rank.slot, rank.key, rank.pos));
                }
            }
        }
        into.Sort(static (a, b) => a.slot != b.slot ? a.slot.CompareTo(b.slot) : a.key != b.key ? a.key.CompareTo(b.key) : a.pos.CompareTo(b.pos));
    }

    /// <summary>The shapeless recipes in vanilla's list order.</summary>
    public IEnumerable<GridRecipe> ShapelessCandidates(List<int>[] slotKeys, HashSet<GridRecipe> seen)
    {
        seen.Clear();
        for (int slot = 0; slot < slotKeys.Length; slot++)
        {
            if (slotKeys[slot] == null) continue;
            foreach (int k in slotKeys[slot])
            {
                foreach (GridRecipe recipe in shapeless[k])
                {
                    if (recipe.Enabled && seen.Add(recipe)) yield return recipe;
                }
            }
        }
    }

    // Each filled cell must satisfy the ingredient there, and so that ingredient's key
    private bool FedByEverySlot(GridRecipe recipe, List<int>[] slotKeys)
    {
        (int key, int pos)[] at = keysOf[recipe];
        foreach (List<int> satisfied in slotKeys)
        {
            if (satisfied == null) continue;
            bool fed = false;
            foreach (var (k, _) in at)
            {
                if (satisfied.BinarySearch(k) >= 0) { fed = true; break; }
            }
            if (!fed) return false;
        }
        return true;
    }

    // Where vanilla's list would have put it: the first slot, then the first key, that adds it
    private bool Rank(GridRecipe recipe, List<int>[] slotKeys, out (int slot, int key, int pos) rank)
    {
        (int key, int pos)[] at = keysOf[recipe];
        for (int slot = 0; slot < slotKeys.Length; slot++)
        {
            if (slotKeys[slot] == null) continue;
            foreach (var (k, pos) in at)
            {
                if (slotKeys[slot].BinarySearch(k) >= 0) { rank = (slot, k, pos); return true; }
            }
        }
        rank = default;
        return false;
    }

    // ---- building ----

    private static readonly ConditionalWeakTable<object, GridLookupIndex> indexes = new();
    private static readonly object building = new();

    public static GridLookupIndex For(IWorldAccessor world)
    {
        var dict = world.FastSearchRecipesByIngredient;
        int gridRecipes = world.GridRecipes.Count;
        if (indexes.TryGetValue(dict, out var index) && index.Fresh(dict, gridRecipes)) return index;
        lock (building)
        {
            if (indexes.TryGetValue(dict, out index) && index.Fresh(dict, gridRecipes)) return index;
            index = new GridLookupIndex(dict, gridRecipes);
            indexes.AddOrUpdate(dict, index);
            return index;
        }
    }

    private bool Fresh(object dict, int gridRecipes) =>
        ReferenceEquals(source, dict) && sourceCount == ((System.Collections.ICollection)dict).Count && recipeCount == gridRecipes;

    private static bool OverridesSatisfies(Type type, Dictionary<Type, bool> known)
    {
        if (!known.TryGetValue(type, out bool overrides))
        {
            known[type] = overrides = type.GetMethod(nameof(CollectibleObject.Satisfies), [typeof(ItemStack), typeof(ItemStack)])?.DeclaringType
                != typeof(CollectibleObject);
        }
        return overrides;
    }

    private static long ItemKey(EnumItemClass itemClass, int id) => ((long)itemClass << 32) | (uint)id;

    private static void Add<TKey>(Dictionary<TKey, List<int>> index, TKey key, int value)
    {
        if (!index.TryGetValue(key, out List<int> list)) index[key] = list = [];
        list.Add(value);
    }
}
