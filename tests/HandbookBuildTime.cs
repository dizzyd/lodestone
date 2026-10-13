using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

/// <summary>
/// How long the survival handbook's first page build takes after joining, with or
/// without handbookperf: nothing else runs while it waits. The build starts at
/// LevelFinalize, which vanilla marks in the debug log, and ends when the dialog's
/// loadingPagesAsync goes false. The log has one-second resolution.
/// </summary>
public class HandbookBuildTime
{
    [VsTest(TimeoutMs = 900_000)]
    [RequiresClient]
    public async Task FirstBuild()
    {
        await OnClient();
        object dialog = AccessTools.Field(typeof(ModSystemSurvivalHandbook), "dialog").GetValue(Capi.ModLoader.GetModSystem<ModSystemSurvivalHandbook>());
        var loading = AccessTools.Field(typeof(GuiDialogHandbook), "loadingPagesAsync");
        Assert.NotNull(dialog);

        bool wasLoading = (bool)loading.GetValue(dialog);
        DateTime deadline = DateTime.Now.AddMinutes(14);
        while ((bool)loading.GetValue(dialog) && DateTime.Now < deadline) await Frames.Wait(5);
        DateTime end = DateTime.Now;
        Assert.False((bool)loading.GetValue(dialog));

        string debugLog = Path.Combine(GamePaths.Logs, "client-debug.log");
        string startLine = File.ReadLines(debugLog).LastOrDefault(l => l.Contains("Starting initialising handbook"));
        Assert.NotNull(startLine);
        DateTime start = DateTime.ParseExact(startLine[..19], "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

        int pages = ((System.Collections.ICollection)AccessTools.Field(typeof(GuiDialogHandbook), "allHandbookPages").GetValue(dialog)).Count;
        bool handbookperf = Capi.ModLoader.IsModEnabled("handbookperf");
        Log($"handbookperf {(handbookperf ? "on" : "off")}; {Capi.ModLoader.Mods.Count()} mods; {pages} pages");
        Log(wasLoading
            ? $"First page build: {(end - start).TotalSeconds:F0} s (started {start:HH:mm:ss}, done {end:HH:mm:ss})"
            : $"First page build finished before the test started, within {(end - start).TotalSeconds:F0} s");
    }
}
