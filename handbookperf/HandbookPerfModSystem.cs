using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace handbookperf;

public class HandbookPerfModSystem : ModSystem
{
    // Both sides run the crafting grid's recipe lookup; only the client has a handbook
    public override void StartServerSide(ICoreServerAPI api)
    {
        // OptiTime is client-only, so a dedicated server never has it. A singleplayer
        // server shares the client's process and so its patches, and the client applies
        // them once OptiTime has made its own.
        if (api.Server.IsDedicated) CraftingGridFix.Apply(new Harmony(Mod.Info.ModID), Mod);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        // StartClientSide runs after every mod's Start, so ACA has patched by now, and
        // before LevelFinalize, which is when the handbook starts building its pages.
        var harmony = new Harmony(Mod.Info.ModID);
        AcaMatchingStackFix.Apply(api, Mod);
        LangLookupFix.Apply(harmony, Mod);
        BuildTiming.Apply(harmony, Mod.Logger);

        api.Event.LevelFinalize += () =>
        {
            // OptiTime patches the crafting grid in its StartClientSide, and if another mod
            // got there first it saves its recipe lookup as off in the player's config.
            CraftingGridFix.Apply(harmony, Mod);

            // ARL patches in StartPre, but whether its patch is needed depends on the
            // collectibles, which are only all present by LevelFinalize.
            ArlFilterFix.Apply(api, Mod);
            HandbookSearchFix.Apply(harmony, Mod);
        };
    }

    public override void Dispose()
    {
        // Patches outlive the world; the next join loads this mod afresh and patches again
        new Harmony(Mod.Info.ModID).UnpatchAll(Mod.Info.ModID);
    }
}
