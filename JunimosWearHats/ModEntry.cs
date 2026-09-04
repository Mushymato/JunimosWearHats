using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace JunimosWearHats;

public sealed class ModConfig
{
    public bool Enable_WorkInRainAndWinter { get; set; } = true;
    public bool Enable_HarvestAllAtEndOfDay { get; set; } = true;
    public bool Enable_RaisinsIncreaseRadius { get; set; } = true;

    public void Reset()
    {
        Enable_WorkInRainAndWinter = true;
        Enable_HarvestAllAtEndOfDay = true;
        Enable_RaisinsIncreaseRadius = true;
    }
}

/// <summary>The API which lets other mods add a config UI through Generic Mod Config Menu.</summary>
public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddBoolOption(
        IManifest mod,
        Func<bool> getValue,
        Action<bool> setValue,
        Func<string> name,
        Func<string>? tooltip = null,
        string? fieldId = null
    );
}

public sealed class ModEntry : Mod
{
#if DEBUG
    private const LogLevel DEFAULT_LOG_LEVEL = LogLevel.Debug;
#else
    private const LogLevel DEFAULT_LOG_LEVEL = LogLevel.Trace;
#endif

    public const string ModId = "mushymato.JunimosWearHats";
    private static IMonitor mon = null!;
    internal static IModHelper help = null!;
    private static ModConfig config = null!;

    public override void Entry(IModHelper helper)
    {
        I18n.Init(helper.Translation);
        mon = Monitor;
        help = helper;
        config = help.ReadConfig<ModConfig>();

        help.Events.GameLoop.GameLaunched += OnGameLaunched;
        help.Events.Display.RenderingStep += OnRenderingStep;
        help.Events.GameLoop.DayEnding += OnDayEnding;
        help.Events.GameLoop.DayStarted += OnDayStarted;

        ToggleWorkPatches();
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        if (
            Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu")
            is IGenericModConfigMenuApi gmcm
        )
        {
            gmcm.Register(ModManifest, config.Reset, () => help.WriteConfig(config));
            gmcm.AddBoolOption(
                ModManifest,
                () => config.Enable_WorkInRainAndWinter,
                (value) => config.Enable_WorkInRainAndWinter = value,
                I18n.Config_EnableWorkInRainAndWinter_Name,
                I18n.Config_EnableWorkInRainAndWinter_Desc
            );
            gmcm.AddBoolOption(
                ModManifest,
                () => config.Enable_HarvestAllAtEndOfDay,
                (value) => config.Enable_HarvestAllAtEndOfDay = value,
                I18n.Config_EnableWorkInRainAndWinter_Name,
                I18n.Config_EnableWorkInRainAndWinter_Desc
            );
            gmcm.AddBoolOption(
                ModManifest,
                () => config.Enable_RaisinsIncreaseRadius,
                (value) => config.Enable_RaisinsIncreaseRadius = value,
                I18n.Config_EnableWorkInRainAndWinter_Name,
                I18n.Config_EnableWorkInRainAndWinter_Desc
            );
        }
    }

    private static Hat? GetHat(JunimoHut junimoHut, Chest? outputChest = null)
    {
        outputChest ??= junimoHut.GetOutputChest();
        foreach (Item item in outputChest.Items)
        {
            if (item is Hat hat)
            {
                return hat;
            }
        }
        return null;
    }

    #region ensure harvest happens always
    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;
        if (!config.Enable_HarvestAllAtEndOfDay)
            return;
        Utility.ForEachBuilding<JunimoHut>(
            (hut) =>
            {
                GameLocation parentLocation = hut.GetParentLocation();
                if (parentLocation.IsRainingHere() || parentLocation.IsWinterHere())
                {
                    if (!config.Enable_WorkInRainAndWinter)
                        return true;
                    if (!HutHasHat(hut))
                        return true;
                }
                JunimoHarvester harvester =
                    hut.myJunimos.FirstOrDefault()
                    ?? new JunimoHarvester(
                        parentLocation,
                        new Vector2(hut.tileX.Value + 1, hut.tileY.Value + 1) * 64f + new Vector2(0f, 32f),
                        hut,
                        0,
                        null
                    );
                for (
                    int i = hut.tileX.Value + 1 - hut.cropHarvestRadius;
                    i < hut.tileX.Value + 2 + hut.cropHarvestRadius;
                    i++
                )
                {
                    for (
                        int j = hut.tileY.Value - hut.cropHarvestRadius + 1;
                        j < hut.tileY.Value + 2 + hut.cropHarvestRadius;
                        j++
                    )
                    {
                        if (parentLocation.terrainFeatures.TryGetValue(new Vector2(i, j), out var value))
                        {
                            if (parentLocation.isCropAtTile(i, j) && value is HoeDirt dirt && dirt.readyForHarvest())
                            {
                                if (dirt.crop.harvest(i, j, dirt, harvester))
                                {
                                    dirt.destroyCrop(false);
                                }
                            }
                            if (value is Bush bush && bush.readyForHarvest())
                            {
                                harvester.tryToAddItemToHut(ItemRegistry.Create(bush.GetShakeOffItem()));
                                bush.tileSheetOffset.Value = 0;
                                bush.setUpSourceRect();
                            }
                        }
                    }
                }
                return true;
            }
        );
    }
    #endregion

    #region work in rain and winter
    private static readonly Harmony harmony = new(ModId);
    private static readonly MethodInfo? JunimoHut_updateWhenFarmNotCurrentLocation = AccessTools.DeclaredMethod(
        typeof(JunimoHut),
        nameof(JunimoHut.updateWhenFarmNotCurrentLocation)
    );
    private static readonly HarmonyMethod HM_JunimoHut_updateWhenFarmNotCurrentLocation_Transpiler = new(
        typeof(ModEntry),
        nameof(JunimoHut_updateWhenFarmNotCurrentLocation_Transpiler)
    );
    private static readonly MethodInfo? JunimoHut_draw = AccessTools.DeclaredMethod(
        typeof(JunimoHut),
        nameof(JunimoHut.draw)
    );
    private static readonly HarmonyMethod HM_JunimoHut_draw_Transpiler = new(
        typeof(ModEntry),
        nameof(JunimoHut_draw_Transpiler)
    );

    private static void ToggleWorkPatches()
    {
        if (JunimoHut_updateWhenFarmNotCurrentLocation != null && JunimoHut_draw != null)
        {
            if (config.Enable_WorkInRainAndWinter)
            {
                try
                {
                    harmony.Patch(
                        original: JunimoHut_updateWhenFarmNotCurrentLocation,
                        transpiler: HM_JunimoHut_updateWhenFarmNotCurrentLocation_Transpiler
                    );
                    harmony.Patch(original: JunimoHut_draw, transpiler: HM_JunimoHut_draw_Transpiler);
                    Log("WorkInRainAndWinter: Enabled", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Log($"Failed to patch WorkInRainAndWinter, disabling this feature:\n{ex}", LogLevel.Error);
                    config.Enable_WorkInRainAndWinter = false;
                    help.WriteConfig(config);
                    ToggleWorkPatches();
                }
            }
            else
            {
                harmony.Unpatch(JunimoHut_updateWhenFarmNotCurrentLocation, HarmonyPatchType.Transpiler);
                harmony.Unpatch(JunimoHut_draw, HarmonyPatchType.Transpiler);
                Log("WorkInRainAndWinter: Disabled", LogLevel.Info);
            }
        }
        else
        {
            Log("WorkInRainAndWinter: Disabled", LogLevel.Info);
            return;
        }
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        Utility.ForEachBuilding<JunimoHut>(hut =>
        {
            if (Game1.IsWinter && config.Enable_WorkInRainAndWinter)
            {
                Chest outputChest = hut.GetOutputChest();
                if (GetHat(hut, outputChest) is not null)
                {
                    if (hut.raisinDays.Value > 0)
                    {
                        hut.raisinDays.Value--;
                    }
                    if (hut.raisinDays.Value == 0)
                    {
                        if (outputChest.Items.CountId("(O)Raisins") > 0)
                        {
                            hut.raisinDays.Value += 7;
                            outputChest.Items.ReduceId("(O)Raisins", 1);
                        }
                    }
                }
            }
            if (config.Enable_RaisinsIncreaseRadius)
            {
                hut.cropHarvestRadius = hut.raisinDays.Value > 0 ? 10 : 8;
            }
            return true;
        });
    }

    private static IEnumerable<CodeInstruction> JunimoHut_updateWhenFarmNotCurrentLocation_Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator
    )
    {
        try
        {
            CodeMatcher matcher = new(instructions, generator);

            matcher
                .MatchStartForward([
                    new(inst => inst.IsLdloc()),
                    new(
                        OpCodes.Callvirt,
                        AccessTools.DeclaredMethod(typeof(GameLocation), nameof(GameLocation.IsWinterHere))
                    ),
                    new(OpCodes.Brtrue),
                    new(inst => inst.IsLdloc()),
                    new(
                        OpCodes.Callvirt,
                        AccessTools.DeclaredMethod(typeof(GameLocation), nameof(GameLocation.IsRainingHere))
                    ),
                    new(OpCodes.Brtrue),
                ])
                .ThrowIfNotMatch("Failed to match 'parentLocation.IsWinterHere() || parentLocation.IsRainingHere()'")
                .CreateLabelWithOffsets(6, out Label lbl)
                .Insert([
                    new(OpCodes.Ldarg_0),
                    new(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ModEntry), nameof(HutHasHat))),
                    new(OpCodes.Brtrue, lbl),
                ]);

            return matcher.Instructions();
        }
        catch (Exception err)
        {
            Log($"Error in JunimoHut_updateWhenFarmNotCurrentLocation_Transpiler:\n{err}", LogLevel.Error);
            return instructions;
        }
    }

    private static IEnumerable<CodeInstruction> JunimoHut_draw_Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator
    )
    {
        try
        {
            CodeMatcher matcher = new(instructions, generator);

            LocalBuilder hasHat = generator.DeclareLocal(typeof(bool));

            matcher
                .MatchStartForward([
                    new(OpCodes.Ldarg_0),
                    new(OpCodes.Ldarg_1),
                    new(OpCodes.Ldc_I4_M1),
                    new(OpCodes.Ldc_I4_M1),
                    new(OpCodes.Callvirt, AccessTools.DeclaredMethod(typeof(Building), nameof(Building.drawShadow))),
                ])
                .ThrowIfNotMatch("Failed to match 'drawShadow'")
                .Advance(1)
                .Insert([
                    new(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ModEntry), nameof(HutHasHatAndRainOrWinter))),
                    new(OpCodes.Stloc, hasHat.LocalIndex),
                    new(OpCodes.Ldarg_0),
                ]);

            matcher
                .MatchStartForward([
                    new(OpCodes.Call, AccessTools.DeclaredPropertyGetter(typeof(Game1), nameof(Game1.IsWinter))),
                    new(OpCodes.Brtrue),
                ])
                .ThrowIfNotMatch("Failed to match 'Game1.IsWinter()'")
                .RemoveInstructions(2);

            matcher
                .MatchEndForward([
                    new(inst => inst.IsLdloc()),
                    new(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(Item), nameof(Item.Category))),
                    new(OpCodes.Ldc_I4_S, (sbyte)-2),
                    new(OpCodes.Beq_S),
                ])
                .ThrowIfNotMatch("Failed to match 'item.Category != -2'");
            Label lbl1 = (Label)matcher.Operand;
            CodeInstruction ldlocItem = matcher.InstructionAt(-3).Clone();
            matcher
                .Advance(1)
                .InsertAndAdvance([
                    ldlocItem,
                    new(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(Item), nameof(Item.Category))),
                    new(OpCodes.Ldc_I4_S, (sbyte)StardewValley.Object.hatCategory),
                    new(OpCodes.Beq_S, lbl1),
                ]);

            matcher
                .MatchEndForward([
                    new(OpCodes.Ldarg_0),
                    new(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Building), nameof(Building.GetParentLocation))),
                    new(
                        OpCodes.Callvirt,
                        AccessTools.DeclaredMethod(typeof(GameLocation), nameof(GameLocation.IsWinterHere))
                    ),
                    new(OpCodes.Brtrue),
                ])
                .ThrowIfNotMatch("Failed to match 'GetParentLocation().IsWinterHere()'")
                .CreateLabelWithOffsets(1, out Label lbl2)
                .MatchStartBackwards([
                    new(OpCodes.Ldsfld, AccessTools.DeclaredField(typeof(Game1), nameof(Game1.timeOfDay))),
                    new(OpCodes.Ldc_I4, 2000),
                    new(OpCodes.Blt),
                ])
                .ThrowIfNotMatch("Failed to match 'Game1.timeOfDay >= 2000'")
                .Insert([new(OpCodes.Ldloc, hasHat.LocalIndex), new(OpCodes.Brtrue, lbl2)]);

            return matcher.Instructions();
        }
        catch (Exception err)
        {
            Log($"Error in JunimoHut_draw_Transpiler:\n{err}", LogLevel.Error);
            return instructions;
        }
    }

    private static bool HutHasHat(JunimoHut __instance) => GetHat(__instance) != null;

    private static bool HutHasHatAndRainOrWinter(JunimoHut __instance)
    {
        GameLocation parentLocation = __instance.GetParentLocation();
        return (parentLocation.IsWinterHere() || parentLocation.IsRainingHere()) && HutHasHat(__instance);
    }
    #endregion

    #region hat draw
    private void OnRenderingStep(object? sender, RenderingStepEventArgs e)
    {
        if (Game1.currentLocation == null)
            return;
        if (e.Step != StardewValley.Mods.RenderSteps.World_Sorted)
            return;
        foreach (Building building in Game1.currentLocation.buildings)
        {
            if (building is not JunimoHut junimoHut)
                continue;
            DrawHatsOverJunimos(e.SpriteBatch, Game1.currentLocation, junimoHut);
        }
    }

    private static void DrawHatsOverJunimos(SpriteBatch b, GameLocation location, JunimoHut junimoHut)
    {
        if (GetHat(junimoHut) is not Hat junimoHat)
            return;
        foreach (JunimoHarvester juni in junimoHut.myJunimos)
        {
            if (!Utility.isOnScreen(juni.TilePoint, 64))
                continue;
            DrawHatOnJuni(b, junimoHat, juni);
        }
    }

    private static void DrawHatOnJuni(SpriteBatch b, Hat junimoHat, JunimoHarvester juni)
    {
        Vector2 location = juni.getLocalPosition(Game1.viewport);
        location.X += 1;
        location.Y += juni.yJumpOffset - 8f;
        int direction = 2;
        int frame = juni.Sprite.currentFrame;
        if ((frame >= 0 && frame < 8) || (frame >= 8 && frame < 16) || (frame >= 28 && frame < 32))
        {
            direction = 2;
        }
        else if ((frame >= 16 && frame < 24) || (frame >= 24 && frame < 28))
        {
            direction = juni.flip ? 3 : 1;
        }
        else if ((frame >= 32 && frame < 40) || (frame >= 40 && frame < 44))
        {
            direction = 0;
        }
        float yOffset = (
            frame switch
            {
                0 => 0,
                1 => 1,
                2 => 1,
                3 => 0,
                4 => -1,
                5 => -2,
                6 => -2,
                7 => -1,
                8 => 0,
                9 => -1,
                10 => 0,
                11 => 1,
                12 => 0,
                13 => -1,
                14 => 0,
                15 => 1,
                16 => 0,
                17 => 1,
                18 => 2,
                19 => 0,
                20 => -1,
                21 => -2,
                22 => -2,
                23 => -1,
                24 => 0,
                25 => 1,
                26 => 2,
                27 => 1,
                28 => 0,
                29 => 1,
                30 => 0,
                31 => -1,
                32 => 0,
                33 => 1,
                34 => 2,
                35 => 0,
                36 => -1,
                37 => -2,
                38 => -2,
                39 => -1,
                40 => 0,
                41 => -1,
                42 => 0,
                43 => 1,
                44 => 0,
                45 => -0.5f,
                46 => 0,
                47 => -0.5f,
                _ => 0,
            }
        );
        location.Y += yOffset * juni.Scale * 4f;
        junimoHat.draw(b, location, 1f, 1f, juni.drawOnTop ? 0.992f : (juni.StandingPixel.Y + 3) / 10000f, direction);
#if DEBUG
        Utility.drawTinyDigits(frame, b, location, 4f, 1f, Color.White);
#endif
    }
    #endregion

    /// <summary>SMAPI static monitor Log wrapper</summary>
    /// <param name="msg"></param>
    /// <param name="level"></param>
    internal static void Log(string msg, LogLevel level = DEFAULT_LOG_LEVEL)
    {
        mon.Log(msg, level);
    }

    /// <summary>SMAPI static monitor LogOnce wrapper</summary>
    /// <param name="msg"></param>
    /// <param name="level"></param>
    internal static void LogOnce(string msg, LogLevel level = DEFAULT_LOG_LEVEL)
    {
        mon.LogOnce(msg, level);
    }

    /// <summary>SMAPI static monitor Log wrapper, debug only</summary>
    /// <param name="msg"></param>
    /// <param name="level"></param>
    [Conditional("DEBUG")]
    internal static void LogDebug(string msg, LogLevel level = DEFAULT_LOG_LEVEL)
    {
        mon.Log(msg, level);
    }
}
