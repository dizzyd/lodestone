using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

/// <summary>
/// The scene for lodestone/modicon.png: loose magnetite, the stone a lodestone is, on a
/// andesite ledge just below eye height with a wall of rich magnetite ore behind it. Not part of
/// the test suite; scripts/make-icon.sh runs it in a square client window and adds the
/// name with ImageMagick.
/// </summary>
public class IconShot
{
    private const string Floor = "game:rock-andesite";
    private const string Loose = "game:looseores-magnetite-andesite-free";
    private const string Ore = "game:ore-rich-magnetite-andesite";

    private static string Out(string name) => System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("VSTK_SHOT_DIR") ?? "/tmp", name);

    [VsTest(TimeoutMs = 300000)]
    [RequiresClient]
    [PlotSize(32, 24)]
    public async Task ModIcon()
    {
        BlockPos c = P(16, 1, 16);
        await World.SetCalendarTo(500 * 24 + 14);   // mid-afternoon: a mostly dark thumbnail reads as a dark square
        await World.LoadArea(c.AddCopy(-12, -1, -12), c.AddCopy(12, 10, 12));
        for (int dx = -12; dx <= 12; dx++)
        {
            for (int dz = -12; dz <= 12; dz++)
            {
                for (int dy = 0; dy <= 10; dy++) World.SetBlock("game:air", c.AddCopy(dx, dy, dz));
                World.SetBlock(Floor, c.AddCopy(dx, -1, dz));
            }
        }

        // A ledge one block high, so the eye is a little above the lumps, and a wall of ore
        // right behind them, wide and tall enough to fill the frame behind the lumps and hide
        // the flat test world. Loose ore is an eighth of a block tall: on the floor it would be
        // seen from above as gravel.
        for (int dx = -8; dx <= 8; dx++)
        {
            for (int dz = -1; dz <= 0; dz++) World.SetBlock(Floor, c.AddCopy(dx, 0, dz));
            for (int dy = 0; dy <= 9; dy++) World.SetBlock(Ore, c.AddCopy(dx, dy, 1));
        }
        foreach (var (dx, dz) in new[] { (0, 0), (-1, 0), (1, 0), (0, -1), (-1, -1), (2, 0), (1, -1) })
        {
            World.SetBlock(Loose, c.AddCopy(dx, 1, dz));
        }

        await Ticks(20);
        await Player.Teleport(c.AddCopy(-1, 0, -3));
        await Ticks(12);
        await Interact.LookAt(c.AddCopy(0, 1, -1));
        await Frames.Wait(90);
        await HideHud();
        await HideGuis();
        Log("modicon -> " + await Shot.Take(Out("icon-source.png")));
    }

    // As fornax's ModDbShots: an empty hand, no clouds or haze, and the HUD dialogs closed
    private static async Task HideHud()
    {
        var hand = Player.Me?.InventoryManager?.ActiveHotbarSlot;
        if (hand != null) { hand.Itemstack = null; hand.MarkDirty(); }
        await Ticks(4);

        await OnClient();
        Capi.Settings.Int["cloudRenderMode"] = 0;
        Capi.Settings.Bool["immersiveFpMode"] = true;
        Capi.Ambient.CurrentModifiers["lodestoneshot"] = new AmbientModifier
        {
            FogDensity = new WeightedFloat(0f, 1f),
            FogMin = new WeightedFloat(0f, 1f),
        }.EnsurePopulated();
        var open = new List<GuiDialog>();
        foreach (var d in Capi.Gui.OpenedGuis) open.Add(d);
        foreach (var d in open)
        {
            string n = d.GetType().Name;
            if (n.StartsWith("Hud") || n == "GuiDialogWorldMap") d.TryClose();
        }
        await OnServer();
        await Frames.Wait(10);
    }

    // F4 takes the crosshair with it; it toggles, so check first
    private static async Task HideGuis()
    {
        await OnClient();
        bool hidden = Capi.HideGuis;
        await OnServer();
        if (!hidden) await Input.Hotkey("togglehud");
        await Frames.Wait(20);
    }
}
