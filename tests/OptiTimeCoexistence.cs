using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Common;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

/// <summary>
/// With OptiTime installed, Lodestone patches the crafting grid after OptiTime has,
/// so OptiTime's conflict check finds nothing and does not save its recipe lookup as
/// off, and Lodestone's lookup then runs in place of OptiTime's. Needs OptiTime in
/// the session's mods.
/// </summary>
public class OptiTimeCoexistence
{
    static MethodInfo find = AccessTools.Method(typeof(InventoryCraftingGrid), "FindMatchingRecipe");

    [VsTest]
    [RequiresClient]
    public async Task OptiTimeKeepsItsRecipeLookupOn()
    {
        await OnClient();
        if (!Capi.ModLoader.IsModEnabled("optitime")) Skip("OptiTime is not installed");

        string path = Path.Combine(Capi.DataBasePath, "ModConfig", "OptiTime.json");
        Assert.True(File.Exists(path));
        var config = JObject.Parse(File.ReadAllText(path));
        Assert.Equal(true, (bool)config["RecipeLookupOptimizations"]);
    }

    [VsTest]
    [RequiresClient]
    public async Task LodestoneRunsAheadOfOptiTime()
    {
        await OnClient();
        if (!Capi.ModLoader.IsModEnabled("optitime")) Skip("OptiTime is not installed");

        var prefixes = Harmony.GetPatchInfo(find).Prefixes.OrderByDescending(p => p.priority).ToList();
        Log("Prefixes, in order: " + string.Join(", ", prefixes.Select(p => $"{p.owner}:{p.PatchMethod.Name}")));
        Assert.True(prefixes.Any(p => p.PatchMethod.DeclaringType?.FullName == "OptiTime.RecipeLookupCacheOptimization"));
        int lookup = prefixes.FindIndex(p => p.owner == "lodestone" && p.PatchMethod.Name == "Lookup");
        int optiTime = prefixes.FindIndex(p => p.PatchMethod.DeclaringType?.FullName == "OptiTime.RecipeLookupCacheOptimization");
        Assert.True(lookup >= 0 && lookup < optiTime);

        // OptiTime also stands down its handbook optimization when this is patched
        var info = AccessTools.Method(AccessTools.TypeByName("Vintagestory.GameContent.CollectibleBehaviorHandbookTextAndExtraInfo"), "GetHandbookInfo");
        Log("GetHandbookInfo patched by: " + string.Join(", ", Harmony.GetPatchInfo(info)?.Owners ?? []));
    }

    // OptiTime reuses the grid's last recipe before trying shaped ones: saw and log make
    // planks (shapeless), and a second log under them then stays planks, where vanilla
    // tries the shaped bowstave first
    [VsTest]
    [RequiresClient]
    public async Task SawAndTwoLogsMakeABowstave()
    {
        await OnClient();
        var world = Capi.World;
        var grid = (InventoryCraftingGrid)world.Player.InventoryManager.GetOwnInventory(GlobalConstants.craftingInvClassName);
        var saw = new ItemStack(world.GetItem(new AssetLocation("saw-copper")));
        var log = new ItemStack(world.GetBlock(new AssetLocation("log-placed-oak-ud")));

        grid[0].Itemstack = saw;
        grid[3].Itemstack = log;
        find.Invoke(grid, null);
        Log($"saw, log: {grid.MatchingRecipe?.Name} -> {grid[9].Itemstack?.Collectible.Code}");
        Assert.Equal("plank-oak", grid[9].Itemstack?.Collectible.Code.Path);

        grid[6].Itemstack = log.Clone();
        find.Invoke(grid, null);
        Log($"saw, log, log: {grid.MatchingRecipe?.Name} -> {grid[9].Itemstack?.Collectible.Code}");
        Assert.Equal("bowstave-long-raw", grid[9].Itemstack?.Collectible.Code.Path);

        for (int i = 0; i < 9; i++) grid[i].Itemstack = null;
        find.Invoke(grid, null);
    }
}
