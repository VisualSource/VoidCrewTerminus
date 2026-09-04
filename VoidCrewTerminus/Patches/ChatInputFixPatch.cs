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
// VoidManager doesn't fix them. Full write-up: docs/chat-bug-research.md.
//
// BUG 1 — the text field is never released. TextChatVE.GetMessage() blanks the
// field (inputField.value = "") while it still holds focus, so cursorIndex/
// selectIndex keep pointing into the now-empty string, and TextChatVE.HideInput()
// never calls Blur()/SelectNone() either. The next keystroke throws
// ArgumentOutOfRangeException deep in TextEditingUtilities.ReplaceSelection —
// Unity's own "chat eats my input" failure mode. The game already has the right
// pattern (UIToolkitNavigationExtensions.OnGainedFocus: SelectNone() then
// Blur()); we apply it after the field is blanked.
//
// BUG 2 — "TextChatting" state latches and chat never reopens. TextChat.OpenChat
// early-returns unless that state is false, and only RemoveInput() clears it.
// RemoveInput is guarded by `if (LocalPlayer.I)`, so if the player reference is
// momentarily null (respawn / scene load) the cleanup silently skips and nothing
// else ever resets the flag. We clear it on the next OpenChat via the game's own
// RemoveInput, once LocalPlayer.I is guaranteed alive.
//
// Defensive throughout: this is vanilla UI the mod otherwise never touches, so a
// missing member must degrade to a logged warning, never a failed
// CreateAndPatchAll (which would take the whole mod down).
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

    internal static bool Enabled => TerminusConfig.ChatInputFixEnabled;

    // One-shot: if a game update moves the vanilla UI, these members stop
    // resolving and the fix goes inert — that needs to be visible, not silent.
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
            // Runs on every sent message and every chat close — log once or this floods the file.
            if (_loggedReleaseFailure) return;
            _loggedReleaseFailure = true;
            BepinPlugin.Log.LogWarning($"[ChatFix] could not release the chat field (suppressing further): {e}");
        }
    }

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

// BUG 1: release the field after the game blanks it on send.
[HarmonyPatch(typeof(TextChatVE), nameof(TextChatVE.GetMessage))]
internal static class ChatFieldReleaseOnSendPatch
{
    static void Postfix(TextChatVE __instance) => ChatInputFix.ReleaseField(__instance);
}

// BUG 1: and when chat is closed without sending.
[HarmonyPatch(typeof(TextChatVE), nameof(TextChatVE.HideInput))]
internal static class ChatFieldReleaseOnHidePatch
{
    static void Postfix(TextChatVE __instance) => ChatInputFix.ReleaseField(__instance);
}

// BUG 2: un-latch a stale "TextChatting" state before the open check runs.
[HarmonyPatch(typeof(TextChat), "OpenChat")]
internal static class ChatStaleStateRecoveryPatch
{
    static void Prefix(TextChat __instance) => ChatInputFix.ClearStaleTextChattingState(__instance);
}
