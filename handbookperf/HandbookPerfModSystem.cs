using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace handbookperf;

public class HandbookPerfModSystem : ModSystem
{
    public static HandbookPerfConfig Config { get; private set; } = new();

    public override void StartPre(ICoreAPI api)
    {
        Config = LoadConfig(api);
    }

    private HandbookPerfConfig LoadConfig(ICoreAPI api)
    {
        HandbookPerfConfig config;
        try
        {
            config = api.LoadModConfig<HandbookPerfConfig>(HandbookPerfConfig.FileName);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("Could not read {0}, using defaults: {1}", HandbookPerfConfig.FileName, e.Message);
            return new HandbookPerfConfig();
        }

        // Written back so a new setting appears in the file with its default
        config ??= new HandbookPerfConfig();
        api.StoreModConfig(config, HandbookPerfConfig.FileName);
        return config;
    }

    // Both sides run the crafting grid's recipe lookup; only the client has a handbook
    public override void StartServerSide(ICoreServerAPI api)
    {
        // OptiTime is client-only, so a dedicated server never has it. A singleplayer
        // server shares the client's process and so its patches, and the client applies
        // them once OptiTime has made its own.
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
            if (Enabled(Config.CraftingGrid, "crafting grid fix")) CraftingGridFix.Apply(harmony, Mod, Config.CraftingGridOverOptiTime);

            // ARL patches in StartPre, but whether its patch is needed depends on the
            // collectibles, which are only all present by LevelFinalize.
            if (Enabled(Config.RemoveArlFilter, "ARL filter removal")) ArlFilterFix.Apply(api, Mod);
            if (Enabled(Config.EmptyHandbookSearch, "empty handbook search")) HandbookSearchFix.Apply(harmony, Mod);
        };
    }

    private bool Enabled(bool setting, string fix)
    {
        if (!setting) Mod.Logger.Notification("{0} off in {1}", fix, HandbookPerfConfig.FileName);
        return setting;
    }

    public override void Dispose()
    {
        // Patches outlive the world; the next join loads this mod afresh and patches again
        new Harmony(Mod.Info.ModID).UnpatchAll(Mod.Info.ModID);
    }
}
