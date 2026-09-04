using System.Diagnostics;
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

namespace JunimosWearHats;

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

    public override void Entry(IModHelper helper)
    {
        I18n.Init(helper.Translation);
        mon = Monitor;
        help = helper;

        help.Events.Display.RenderingStep += OnRenderingStep;

        DoPatches();
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

    #region work in rain and winter
    private void DoPatches()
    {
        Harmony harmony = new(ModId);
        harmony.Patch(
            original: AccessTools.DeclaredMethod(typeof(JunimoHut), nameof(JunimoHut.dayUpdate)),
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(JunimoHut_dayUpdate_Postfix))
        );
        harmony.Patch(
            original: AccessTools.DeclaredMethod(typeof(JunimoHut), nameof(JunimoHut.updateWhenFarmNotCurrentLocation)),
            transpiler: new HarmonyMethod(
                typeof(ModEntry),
                nameof(JunimoHut_updateWhenFarmNotCurrentLocation_Transpiler)
            )
        );
        harmony.Patch(
            original: AccessTools.DeclaredMethod(typeof(JunimoHut), nameof(JunimoHut.draw)),
            transpiler: new HarmonyMethod(typeof(ModEntry), nameof(JunimoHut_draw_Transpiler))
        );
    }

    private static void JunimoHut_dayUpdate_Postfix(JunimoHut __instance)
    {
        if (!Game1.IsWinter)
            return;
        Chest outputChest = __instance.GetOutputChest();
        if (GetHat(__instance, outputChest) is null)
            return;
        if (__instance.raisinDays.Value > 0)
        {
            __instance.raisinDays.Value--;
        }
        if (__instance.raisinDays.Value == 0)
        {
            if (outputChest.Items.CountId("(O)Raisins") > 0)
            {
                __instance.raisinDays.Value += 7;
                outputChest.Items.ReduceId("(O)Raisins", 1);
            }
        }
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
        if (e.Step == StardewValley.Mods.RenderSteps.World_Sorted)
        {
            foreach (Building building in Game1.currentLocation.buildings)
            {
                if (building is JunimoHut junimoHut)
                {
                    DrawHatsOverJunimos(e.SpriteBatch, junimoHut);
                }
            }
        }
    }

    private static void DrawHatsOverJunimos(SpriteBatch b, JunimoHut junimoHut)
    {
        if (GetHat(junimoHut) is not Hat junimoHat)
            return;
        foreach (JunimoHarvester juni in junimoHut.myJunimos)
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
            junimoHat.draw(
                b,
                location,
                1f,
                1f,
                juni.drawOnTop ? 0.992f : (juni.StandingPixel.Y + 3) / 10000f,
                direction
            );
#if DEBUG
            Utility.drawTinyDigits(frame, b, location, 4f, 1f, Color.White);
#endif
        }
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
