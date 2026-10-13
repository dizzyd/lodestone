using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.Common;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

/// <summary>
/// Each fix's patches are there exactly when ModConfig/handbookperf.json turns it on.
/// Run once per config to check: the file is read as the session left it.
/// </summary>
public class ConfigTests
{
    [VsTest]
    [RequiresClient]
    public async Task PatchesFollowTheConfig()
    {
        await OnClient();
        string path = Path.Combine(Capi.DataBasePath, "ModConfig", "handbookperf.json");
        Assert.True(File.Exists(path));
        var config = JObject.Parse(File.ReadAllText(path));
        Log("Config: " + string.Join(", ", config.Properties().Select(p => $"{p.Name}={p.Value}")));
        bool On(string name) => (bool)config[name];

        bool optiTime = Capi.ModLoader.IsModEnabled("optitime");
        bool aca = Capi.ModLoader.IsModEnabled("aculinaryartillery");
        bool arl = Capi.ModLoader.IsModEnabled("attributerenderinglibrary");

        Check("LangIndex", On("LangIndex"),
            Ours(AccessTools.Method(typeof(TranslationService), nameof(TranslationService.HasTranslation), [typeof(string), typeof(bool), typeof(bool)])));
        if (aca) Check("AcaMatcher", On("AcaMatcher"),
            Ours(AccessTools.Method(typeof(CookingRecipeIngredient), nameof(CookingRecipeIngredient.GetMatchingStack))));
        Check("CraftingGrid", On("CraftingGrid") && (!optiTime || On("CraftingGridOverOptiTime")),
            Ours(AccessTools.Method(typeof(InventoryCraftingGrid), "FindMatchingRecipe")));
        Check("EmptyHandbookSearch", On("EmptyHandbookSearch"),
            Ours(AccessTools.Method(typeof(GuiDialogHandbook), "FilterItems")));
        if (arl) Check("RemoveArlFilter", On("RemoveArlFilter"),
            Harmony.GetPatchInfo(AccessTools.Method(typeof(GuiDialogHandbook), "FilterItems"))?.Prefixes.All(p => p.owner != "attributerenderinglibrary") ?? true);
        Check("LogTimings", On("LogTimings"),
            Ours(AccessTools.Method(typeof(GuiDialogHandbook), "LoadPages_Async")));
    }

    static bool Ours(MethodBase method) => Harmony.GetPatchInfo(method)?.Owners.Contains("handbookperf") == true;

    static void Check(string setting, bool expected, bool patched)
    {
        Log($"{setting}: expected {(expected ? "on" : "off")}, {(patched ? "on" : "off")}");
        Assert.Equal(expected, patched);
    }
}
