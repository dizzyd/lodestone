using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

/// <summary>
/// The handbook's and ACA's CreateCachedMealRecipeStacks with Lodestone's narrowed
/// stack array find the same stacks, in the same order, as with the full array, and how
/// long each takes. Needs ACA in the session's mods, as the narrowing works on its matcher.
/// </summary>
public class MealMatchingTests
{
    const int Sampled = 120;

    [VsTest(TimeoutMs = 1_200_000)]
    [RequiresClient]
    public async Task NarrowedFindsTheSameStacks()
    {
        await OnClient();
        var aca = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ACulinaryArtillery");
        if (aca == null) Skip("ACA is not installed");
        var acaCreate = aca.GetType("ACulinaryArtillery.Util.HandbookInfoExtensions").GetMethod("CreateCachedMealRecipeStacks");
        var vanillaCreate = AccessTools.Method(typeof(ModSystemSurvivalHandbook), "CreateCachedMealRecipeStacks");
        var handbook = Capi.ModLoader.GetModSystem<ModSystemSurvivalHandbook>();
        var creates = new (string name, MethodInfo create, System.Func<CookingRecipe, System.Collections.IDictionary> run)[]
        {
            // Vanilla's returns nothing; the result is what it cached
            ("handbook", vanillaCreate, r => { vanillaCreate.Invoke(handbook, [r]); return ObjectCacheUtil.TryGet<System.Collections.IDictionary>(Capi, "valstacksbying-" + r.Code); }),
            ("ACA", acaCreate, r => (System.Collections.IDictionary)acaCreate.Invoke(null, [Capi, r])),
        };

        var allstacks = ObjectCacheUtil.TryGet<ItemStack[]>(Capi, "handbookallstacks");
        var recipes = new List<(string kind, CookingRecipe recipe)>();
        var mixing = aca.GetType("Vintagestory.GameContent.ApiAdditions")?.GetMethod("GetMixingRecipes")?.Invoke(null, [Capi]) as List<CookingRecipe>;
        Log($"ACA mixing recipes: {(mixing == null ? "lookup failed" : $"{mixing.Count}, {mixing.Count(r => r.CooksInto == null)} without CooksInto")}");
        recipes.AddRange((mixing ?? []).Where(r => r.CooksInto == null).Select(r => ("mixing", r)));
        recipes.AddRange(Capi.GetCookingRecipes().Select(r => ("cooking", r)));
        recipes.AddRange(BlockPie.GetHandbookRecipes(Capi, allstacks).Select(r => ("pie", r)));
        int stride = Math.Max(1, recipes.Count / Sampled);
        var sample = recipes.Where((_, i) => i % stride == 0).ToList();
        Log($"{allstacks.Length} handbook stacks; recipes {string.Join(", ", recipes.GroupBy(r => r.kind).Select(g => $"{g.Key} {g.Count()}"))}; sampled {sample.Count} ({string.Join(", ", sample.GroupBy(r => r.kind).Select(g => $"{g.Key} {g.Count()}"))})");

        foreach (var (name, create, run) in creates)
        {
            var body = Lambda(create);
            Log($"{name}: transpilers on its lambda: {string.Join(", ", Harmony.GetPatchInfo(body)?.Transpilers.Select(p => p.owner) ?? [])}");
            Assert.True(Harmony.GetPatchInfo(body)?.Transpilers.Any(p => p.owner == "lodestone") == true);

            // Full, narrowed, full again: the two full passes show whether a pass alone changes anything
            var harmony = new Harmony("lodestone");
            var transpiler = Harmony.GetPatchInfo(body).Transpilers.First(p => p.owner == "lodestone").PatchMethod;
            harmony.Unpatch(body, HarmonyPatchType.Transpiler, "lodestone");
            List<string> full, again, narrowed;
            double tFull, tNarrowed;
            try
            {
                (full, tFull) = await Run(run, sample);
                harmony.Patch(body, transpiler: new HarmonyMethod(transpiler));
                (narrowed, tNarrowed) = await Run(run, sample);
                harmony.Unpatch(body, HarmonyPatchType.Transpiler, "lodestone");
                (again, _) = await Run(run, sample);
            }
            finally
            {
                harmony.Patch(body, transpiler: new HarmonyMethod(transpiler));
            }

            int unstable = Enumerable.Range(0, sample.Count).Count(i => again[i] != full[i]);
            Log($"{name}: two full passes differ for {unstable} of {sample.Count} recipes");
            var differ = Enumerable.Range(0, sample.Count).Where(i => narrowed[i] != full[i]).ToList();
            Log($"{name}: full stack array {tFull:F0} ms, narrowed {tNarrowed:F0} ms ({tFull / tNarrowed:F0}x); scaled to all {recipes.Count} recipes {tFull * recipes.Count / sample.Count / 1000:F1} s vs {tNarrowed * recipes.Count / sample.Count / 1000:F2} s");
            Log($"{name}: matched stacks per ingredient differ for {differ.Count} of {sample.Count} recipes");
            foreach (int i in differ.Take(3))
            {
                Log($"  {sample[i].recipe.Code}");
                Log($"    narrowed {Trim(narrowed[i])}");
                Log($"    full     {Trim(full[i])}");
            }
            Assert.Equal(0, differ.Count);
        }
    }

    // A handbook stack gains a transitionstate when anything first asks how fresh it is,
    // so two passes can see different ones; matching ignores it
    static string Attributes(ItemStack stack)
    {
        var attributes = stack.Attributes.Clone();
        attributes.RemoveAttribute("transitionstate");
        return attributes.Count > 0 ? attributes.ToJsonToken() : "";
    }

    static string Trim(string s) => s.Length > 700 ? s[..700] + "..." : s;

    static MethodInfo Lambda(MethodInfo create) => PatchProcessor.GetOriginalInstructions(create).Where(i => i.opcode == OpCodes.Ldftn)
        .Select(i => i.operand).OfType<MethodInfo>().First(m => m.Name.Contains("CreateCachedMealRecipeStacks"));

    // Each recipe's cache entry is dropped first, so ACA computes it afresh
    static async Task<(List<string>, double)> Run(System.Func<CookingRecipe, System.Collections.IDictionary> create, List<(string kind, CookingRecipe recipe)> recipes)
    {
        var results = new List<string>();
        double ms = 0;
        foreach (var (_, recipe) in recipes)
        {
            ObjectCacheUtil.Delete(Capi, "valstacksbying-" + recipe.Code);
            long t = Stopwatch.GetTimestamp();
            var result = create(recipe);
            ms += Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            results.Add(Describe(result));
            await Frames.Wait(1);
        }
        return (results, ms);
    }

    static string Describe(System.Collections.IDictionary byIngredient)
    {
        var parts = new List<string>();
        foreach (System.Collections.DictionaryEntry e in byIngredient)
        {
            var stacks = ((IEnumerable<ItemStack>)e.Value).Select(s => s == null ? "none" : $"{s.Collectible.Code}x{s.StackSize}{Attributes(s)}");
            parts.Add($"{((CookingRecipeIngredient)e.Key).Code}=[{string.Join(" ", stacks)}]");
        }
        return string.Join("; ", parts);
    }
}
