using System;
using System.Reflection;
using Gameplay.Chat;
using HarmonyLib;
using UI.Chat;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewTerminus.Patches;

// Works around two vanilla chat bugs that between them can leave a player unable
// to type in chat for the rest of the session. Neither is caused by this mod, and
// VoidManager does not fix them (its chat features are postfix-only additions:
// mouse unlock, input history, tab-complete). Full write-up with sources:
// docs/chat-bug-research.md.
//
// BUG 1 — the text field is never released.
//   TextChatVE.GetMessage() blanks the field while it still holds focus:
//       string text = inputField.text;
//       inputField.value = "";        // cursorIndex/selectIndex still index the OLD string
//   and TextChatVE.HideInput() only toggles CSS classes — there is no Blur() and
//   no SelectNone() anywhere in the chat code. The UIElements panel keeps routing
//   keydowns into a hidden editor whose selection range now points past the end of
//   an empty string, so the next keystroke throws deep in the engine:
//       ArgumentOutOfRangeException (startIndex)
//         String.Insert → TextEditingUtilities.ReplaceSelection
//         → TextEditingUtilities.Insert(char) → KeyboardTextEditorEventHandler.OnKeyDown
//   Unity's tracker documents this as throwing on EVERY subsequent keypress once
//   the indices desync, which is the "chat eats my input" symptom.
//
//   The game already knows the right pattern — UIToolkitNavigationExtensions
//   .OnGainedFocus does SelectNone() then Blur() — the chat widget just skips it.
//   We apply exactly that, after the field is blanked.
//
// BUG 2 — "TextChatting" latches and chat never reopens.
//   TextChat.OpenChat early-returns unless that character state is false, and ONLY
//   RemoveInput() clears it. RemoveInput is itself guarded by `if (LocalPlayer.I)`,
//   so if the player reference is momentarily null (respawn / scene load) the
//   cleanup silently skips, the flag stays true, and Enter does nothing forever.
//   Nothing else in the game resets it.
//
//   We clear it on the next OpenChat, using the game's OWN RemoveInput — by then
//   LocalPlayer.I is alive (OpenChat dereferences it), so the cleanup that failed
//   earlier now succeeds.
//
// Everything here is defensive: this is vanilla UI the mod otherwise never touches,
// and a missing member must degrade to a logged warning, never a failed
// CreateAndPatchAll (which would take the whole mod down — see the Phase 8-A
// Photon regression).
internal static class ChatInputFix
{
    private static readonly FieldInfo _inputField =
        AccessTools.Field(typeof(TextChatVE), "inputField");
    private static readonly FieldInfo _chatOpened =
        AccessTools.Field(typeof(TextChat), "_chatOpened");
    private static readonly MethodInfo _removeInput =
        AccessTools.Method(typeof(TextChat), "RemoveInput");

    // Opsive.Shared.StateSystem.StateManager — reached by reflection because it
    // lives in a third-party assembly whose signature we can't verify from the
    // decompiled source we have.
    private static readonly MethodInfo _getState =
        AccessTools.Method(AccessTools.TypeByName("Opsive.Shared.StateSystem.StateManager"),
                           "GetState", new[] { typeof(GameObject), typeof(string) });

    private const string TextChattingState = "TextChatting";

    private static bool _loggedResolve;
    private static bool _loggedReleaseFailure;
    private static bool _loggedStateFailure;

    internal static bool Enabled => TerminusConfig.EnableChatInputFix?.Value ?? true;

    // One-shot so a playtest log says plainly whether this patch is live. If the
    // vanilla UI moves in a game update the members stop resolving and the fix goes
    // inert — that needs to be visible, not silent.
    private static void LogResolveOnce()
    {
        if (_loggedResolve) return;
        _loggedResolve = true;
        BepinPlugin.Log.LogDebug(
            $"[ChatFix] resolved: inputField={_inputField != null}, _chatOpened={_chatOpened != null}, " +
            $"RemoveInput={_removeInput != null}, StateManager.GetState={_getState != null}");

        if (_inputField == null || _chatOpened == null || _removeInput == null || _getState == null)
            BepinPlugin.Log.LogWarning(
                "[ChatFix] one or more vanilla chat members did not resolve — the chat input fix is " +
                "partially or fully inert. Vanilla chat UI likely changed in a game update.");
    }

    // Release keyboard focus and clear the selection range, so the editor can't be
    // left indexing a string that no longer exists.
    internal static void ReleaseField(TextChatVE view)
    {
        if (!Enabled || view == null) return;
        LogResolveOnce();
        if (_inputField == null) return;

        try
        {
            if (_inputField.GetValue(view) is not TextField field) return;
            field.SelectNone();
            field.Blur();
        }
        catch (Exception e)
        {
            // Runs on every sent message and every chat close — log once or this
            // floods the file (cf. the burden's "already off" spam, TODO 8-E).
            if (_loggedReleaseFailure) return;
            _loggedReleaseFailure = true;
            BepinPlugin.Log.LogWarning($"[ChatFix] could not release the chat field (suppressing further): {e}");
        }
    }

    // If the state flag is set but chat isn't actually open, the flag is stale —
    // clear it through the game's own cleanup so chat becomes usable again.
    internal static void ClearStaleTextChattingState(TextChat chat)
    {
        if (!Enabled || chat == null) return;
        LogResolveOnce();
        if (_chatOpened == null || _removeInput == null || _getState == null) return;

        try
        {
            // Genuinely open — leave it alone.
            if (_chatOpened.GetValue(chat) is bool open && open) return;

            var player = CG.Game.Player.LocalPlayer.Instance;
            if (player == null) return;

            if (_getState.Invoke(null, new object[] { player.gameObject, TextChattingState }) is not bool latched
                || !latched)
                return; // flag isn't set: this is an ordinary chat open

            _removeInput.Invoke(chat, null);
            BepinPlugin.Log.LogInfo(
                "[ChatFix] cleared a stale 'TextChatting' state — chat had latched shut and would " +
                "otherwise have stayed unusable for the rest of the session.");
        }
        catch (Exception e)
        {
            if (_loggedStateFailure) return;
            _loggedStateFailure = true;
            BepinPlugin.Log.LogWarning($"[ChatFix] stale-state check failed (suppressing further): {e}");
        }
    }
}

// --- BUG 1: release the field after the game blanks it on send ---

[HarmonyPatch(typeof(TextChatVE), nameof(TextChatVE.GetMessage))]
internal static class ChatFieldReleaseOnSendPatch
{
    static void Postfix(TextChatVE __instance) => ChatInputFix.ReleaseField(__instance);
}

// --- BUG 1: and when chat is closed without sending ---

[HarmonyPatch(typeof(TextChatVE), nameof(TextChatVE.HideInput))]
internal static class ChatFieldReleaseOnHidePatch
{
    static void Postfix(TextChatVE __instance) => ChatInputFix.ReleaseField(__instance);
}

// --- BUG 2: un-latch a stale "TextChatting" state before the open check runs ---

[HarmonyPatch(typeof(TextChat), "OpenChat")]
internal static class ChatStaleStateRecoveryPatch
{
    static void Prefix(TextChat __instance) => ChatInputFix.ClearStaleTextChattingState(__instance);
}
