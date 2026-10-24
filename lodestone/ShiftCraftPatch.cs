// Shift-Craft Fix - https://github.com/dizzyd/shiftcraftfix
// Copyright (c) 2026 DizzyD. Released under the MIT License; the full text is at the end of this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Common;

namespace ShiftCraftFix
{
    /// <summary>
    /// Stops a shift-click duplicating items or leaving ghosts on the client, as pies do in 1.22.7.
    ///
    /// 1.22.7's PlayerInventoryManager.TryTransferAway stops moving a stack as soon as the
    /// source slot's stack no longer Satisfies the one it started with. Satisfies ignores a
    /// stack's own perish state but not that of the stacks it carries - a pie's
    /// ingredients - so a pie that has only been re-stamped counts as a different stack:
    /// - A crafting output rebuilds its stack after every craft, so the check fails partway
    ///   through a craft whose output has been handed out but whose ingredients have not been
    ///   consumed.
    /// - Anywhere else, merging into a partial stack refreshes the source's perish state. The
    ///   server re-stamps it if its copy is stale and stops; the client, whose copy is
    ///   re-stamped every time the slot is drawn, does not, and shows items the server never
    ///   moved.
    /// This keeps the check but also accepts a stack that matches as the merge itself compares
    /// stacks: ignoring GlobalConstants.IgnoredStackAttributes - perish state, temperature, tool
    /// mode and render variant - at every depth, not just on the outer stack.
    ///
    /// This file is written to be copied into other mods. A mod that carries it calls
    /// <see cref="Apply"/> from its ModSystem's Start and <see cref="Remove"/> from Dispose.
    /// Every copy - this mod's, and any other mod's - finds the others through
    /// <see cref="HarmonyId"/>, and only the copy with the highest <see cref="Revision"/>
    /// patches, so load order does not matter and the fix is never applied twice. When
    /// copying, keep HarmonyId as it is; raise Revision only if you change what the patch does.
    /// </summary>
    public static class ShiftCraftPatch
    {
        public const string HarmonyId = "shiftcraftfix.trytransferaway";
        public const int Revision = 2;

        static readonly MethodInfo Target = AccessTools.Method(typeof(PlayerInventoryManager), "TryTransferAway",
            new[] { typeof(ItemSlot), typeof(ItemStackMoveOperation).MakeByRefType(), typeof(bool), typeof(StringBuilder), typeof(bool) });
        static readonly MethodInfo Satisfies = AccessTools.Method(typeof(ItemStack), nameof(ItemStack.Satisfies));

        /// <summary>Apply calls not yet matched by a Remove: one per side, two in singleplayer.</summary>
        static int users;

        /// <summary>Whether some copy of the fix is patched in, this one or another mod's.</summary>
        public static bool Active => Holder() != null;

        /// <summary>The copy that is patched in, as "assembly rN", or null when none is.</summary>
        public static string ActiveCopy => Holder() is Type t ? $"{t.Assembly.GetName().Name} r{RevisionOf(t)}" : null;

        public static void Apply(ICoreAPI api)
        {
            // Copies in other mods have their own statics, so the lock they share is a game type.
            // Start runs on two threads in singleplayer, the server's and the client's.
            lock (typeof(PlayerInventoryManager))
            {
                users++;
                var holder = Holder();
                if (holder == typeof(ShiftCraftPatch)) return;
                if (holder != null && RevisionOf(holder) >= Revision)
                {
                    api.Logger.Notification("[ShiftCraftFix] Already applied by {0}", ActiveCopy);
                    return;
                }

                // Patch only the method this was written against: exactly one identity check.
                // A game that has fixed it, or moved it, is left alone.
                int checks = Target == null ? 0 : PatchProcessor.GetOriginalInstructions(Target).Count(i => i.Calls(Satisfies));
                if (checks != 1)
                {
                    api.Logger.Notification("[ShiftCraftFix] Not needed on this game version; nothing patched");
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                if (holder != null) harmony.Unpatch(Target, HarmonyPatchType.All, HarmonyId);
                harmony.Patch(Target, transpiler: new HarmonyMethod(typeof(ShiftCraftPatch), nameof(Transpiler)));
                api.Logger.Notification("[ShiftCraftFix] Applied {0}{1}", ActiveCopy,
                    holder != null ? $", replacing r{RevisionOf(holder)} from {holder.Assembly.GetName().Name}" : "");
            }
        }

        /// <summary>Unpatches once every side that applied this copy has removed it, if this copy is the one patched in.</summary>
        public static void Remove()
        {
            lock (typeof(PlayerInventoryManager))
            {
                if (users == 0 || --users > 0) return;
                if (Holder() == typeof(ShiftCraftPatch)) new Harmony(HarmonyId).Unpatch(Target, HarmonyPatchType.All, HarmonyId);
            }
        }

        /// <summary>The class whose transpiler is patched in under HarmonyId, in whichever mod's assembly it lives.</summary>
        static Type Holder() => Target == null ? null
            : Harmony.GetPatchInfo(Target)?.Transpilers.FirstOrDefault(p => p.owner == HarmonyId)?.PatchMethod.DeclaringType;

        static int RevisionOf(Type copy) => copy.GetField(nameof(Revision))?.GetValue(null) as int? ?? 0;

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) =>
            code.Select(ins => ins.Calls(Satisfies)
                ? new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ShiftCraftPatch), nameof(StillTheSame))).MoveLabelsFrom(ins).MoveBlocksFrom(ins)
                : ins);

        /// <summary>The same stack, or one that differs only in GlobalConstants.IgnoredStackAttributes, at any depth.</summary>
        static bool StillTheSame(ItemStack now, ItemStack before) =>
            now.Satisfies(before) || now.Collectible.Equals(now, before, GlobalConstants.IgnoredStackAttributes);
    }
}

/*
MIT License

Copyright (c) 2026 DizzyD

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
