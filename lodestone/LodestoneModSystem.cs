using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using ShiftCraftFix;

namespace lodestone;

public class LodestoneModSystem : ModSystem
{
    public static LodestoneConfig Config { get; private set; } = new();

    // For fixes that check another mod's version; the same mods load on both sides
    internal static IModLoader ModLoader { get; private set; }

    public override void StartPre(ICoreAPI api)
    {
        Config = LoadConfig(api);
        ModLoader = api.ModLoader;
    }

    private LodestoneConfig LoadConfig(ICoreAPI api)
    {
        LodestoneConfig config;
        try
        {
            config = api.LoadModConfig<LodestoneConfig>(LodestoneConfig.FileName);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("Could not read {0}, using defaults: {1}", LodestoneConfig.FileName, e.Message);
            return new LodestoneConfig();
        }

        // Written back so a new setting appears in the file with its default
        config ??= new LodestoneConfig();
        api.StoreModConfig(config, LodestoneConfig.FileName);
        return config;
    }

    // Both sides need the shift-craft fix: the client repeats the transfer to predict it,
    // and without the fix shows items the server never gave
    public override void Start(ICoreAPI api)
    {
        if (Enabled(Config.ShiftCraftFix, "shift-craft fix")) ShiftCraftPatch.Apply(api);
    }

    // Both sides run the crafting grid's recipe lookup; only the client has a handbook
    public override void StartServerSide(ICoreServerAPI api)
    {
        // OptiTime is client-only, so a dedicated server never has it. A singleplayer
        // server shares the client's process and so its patches, and the client applies
        // them once OptiTime has made its own.
        // The recipes are all loaded by the first tick. A singleplayer server's index is built
        // here too, since its client applies the patches only after the server is up.
        if (Config.CraftingGrid) api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => CraftingGridFix.BuildIndex(api.World, Mod.Logger));

        if (api.Server.IsDedicated && Enabled(Config.CraftingGrid, "crafting grid fix"))
        {
            CraftingGridFix.Apply(new Harmony(Mod.Info.ModID), Mod, takeOverFromOptiTime: false);
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        // StartClientSide runs after every mod's Start, so ACA has patched by now, and
        // before LevelFinalize, which is when the handbook starts building its pages.
        var harmony = new Harmony(Mod.Info.ModID);
        if (Enabled(Config.AcaMatcher, "ACA matcher")) AcaMatchingStackFix.Apply(api, Mod);
        if (Enabled(Config.LangIndex, "lang index")) LangLookupFix.Apply(harmony, Mod);
        if (Config.LogTimings) BuildTiming.Apply(harmony, Mod.Logger);

        api.Event.LevelFinalize += () =>
        {
            // OptiTime patches the crafting grid in its StartClientSide, and if another mod
            // got there first it saves its recipe lookup as off in the player's config.
            if (Enabled(Config.CraftingGrid, "crafting grid fix"))
            {
                CraftingGridFix.Apply(harmony, Mod, Config.CraftingGridOverOptiTime);
                if (CraftingGridFix.Indexing) CraftingGridFix.BuildIndex(api.World, Mod.Logger);
            }

            // ARL patches in StartPre, but whether its patch is needed depends on the
            // collectibles, which are only all present by LevelFinalize.
            if (Enabled(Config.RemoveArlFilter, "ARL filter removal")) ArlFilterFix.Apply(api, Mod);
            if (Enabled(Config.EmptyHandbookSearch, "empty handbook search")) HandbookSearchFix.Apply(harmony, Mod);
        };
    }

    private bool Enabled(bool setting, string fix)
    {
        if (!setting) Mod.Logger.Notification("{0} off in {1}", fix, LodestoneConfig.FileName);
        return setting;
    }

    public override void Dispose()
    {
        // Patches outlive the world; the next join loads this mod afresh and patches again
        new Harmony(Mod.Info.ModID).UnpatchAll(Mod.Info.ModID);
        ShiftCraftPatch.Remove();
    }
}
