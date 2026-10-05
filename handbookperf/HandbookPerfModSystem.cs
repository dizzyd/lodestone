using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace handbookperf;

public class HandbookPerfModSystem : ModSystem
{
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        // StartClientSide runs after every mod's Start, so ACA has patched by now, and
        // before LevelFinalize, which is when the handbook starts building its pages.
        var harmony = new Harmony(Mod.Info.ModID);
        AcaMatchingStackFix.Apply(api, Mod);
        LangLookupFix.Apply(harmony, Mod);
        BuildTiming.Apply(harmony, Mod.Logger);

        // ARL patches in StartPre, but whether its patch is needed depends on the
        // collectibles, which are only all present by LevelFinalize.
        api.Event.LevelFinalize += () => ArlFilterFix.Apply(api, Mod);
    }

    public override void Dispose()
    {
        // Patches outlive the world; the next join loads this mod afresh and patches again
        new Harmony(Mod.Info.ModID).UnpatchAll(Mod.Info.ModID);
    }
}
