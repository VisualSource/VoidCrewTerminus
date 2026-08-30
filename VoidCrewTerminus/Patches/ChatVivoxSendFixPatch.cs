using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Unity.Services.Vivox;

namespace VoidCrewTerminus.Patches;

// Vanilla bug: a room-join race empties VivoxAdapter.channelUsers[textChannel],
// so SendTextMessage's per-recipient directed-message loop reaches nobody while
// the sender still gets a local echo — chat looks sent but no one receives it.
// We send one SendChannelTextMessageAsync to the live "_chat" channel instead
// (no channelUsers dependency) and add the matching ChannelMessageReceived
// subscription the game lacks. Text path only — voice is untouched. Falls back
// to vanilla on any unresolved member or exception. Full write-up:
// docs/chat-bug-research.html ("Symptom 1 — the Vivox channel race").
internal static class ChatVivoxSendFix
{
    private static readonly FieldInfo _textChannel =
        AccessTools.Field(typeof(VivoxAdapter), "textChannel");

    // Two overloads exist; we want the (senderId, text) one the game uses for
    // its own local echo, not the VivoxMessage one.
    private static readonly MethodInfo _fireMessageReceived =
        AccessTools.Method(typeof(VivoxAdapter), "FireMessageReceivedEvent",
                           new[] { typeof(string), typeof(string) });

    private static bool _loggedResolve;
    private static bool _loggedSendFailure;
    private static bool _loggedReceiveFailure;

    private static Action<VivoxMessage> _channelHandler;

    internal static bool Enabled => TerminusConfig.VivoxTextSendFixEnabled;

    // One-shot: if a game update moves VivoxAdapter, these stop resolving and the
    // fix goes inert — that needs to be visible, not silent.
    private static void LogResolveOnce()
    {
        if (_loggedResolve) return;
        _loggedResolve = true;
        BepinPlugin.Log.LogDebug(
            $"[ChatVivoxFix] resolved: textChannel={_textChannel != null}, " +
            $"FireMessageReceivedEvent(string,string)={_fireMessageReceived != null}");

        if (_textChannel == null || _fireMessageReceived == null)
            BepinPlugin.Log.LogWarning(
                "[ChatVivoxFix] one or more vanilla Vivox members did not resolve — the text-send " +
                "fix is inert and multiplayer chat falls back to vanilla behaviour. VivoxAdapter " +
                "likely changed in a game update.");
    }

    // Returns true only if we fully handled the send; the caller then skips the
    // original. Any doubt (disabled, unresolved, no channel, exception) returns
    // false so vanilla runs unchanged.
    internal static bool TrySend(VivoxAdapter adapter, string message)
    {
        if (!Enabled || adapter == null) return false;
        LogResolveOnce();
        if (_textChannel == null || _fireMessageReceived == null) return false;

        try
        {
            string channel = _textChannel.GetValue(adapter) as string;
            if (string.IsNullOrEmpty(channel))
                channel = FindActiveChatChannel();
            if (string.IsNullOrEmpty(channel))
                return false; // no channel name to recover — let vanilla try (and fail as before)

            VivoxService.Instance.SendChannelTextMessageAsync(channel, message)
                .ContinueWith(LogSendFault, TaskContinuationOptions.OnlyOnFaulted);

            // The same local echo the vanilla method does, so the sender still sees their line.
            _fireMessageReceived.Invoke(adapter, new object[] { VivoxService.Instance.SignedInPlayerId, message });
            return true;
        }
        catch (Exception e)
        {
            if (!_loggedSendFailure)
            {
                _loggedSendFailure = true;
                BepinPlugin.Log.LogWarning(
                    $"[ChatVivoxFix] channel send failed, falling back to vanilla (suppressing further): {e}");
            }
            return false;
        }
    }

    private static string FindActiveChatChannel()
    {
        try
        {
            foreach (var name in VivoxService.Instance.ActiveChannels.Keys)
                if (!string.IsNullOrEmpty(name) && name.EndsWith("_chat", StringComparison.Ordinal))
                    return name;
        }
        catch
        {
            // ActiveChannels can throw mid-teardown; treated as "no channel".
        }
        return null;
    }

    private static void LogSendFault(Task t)
    {
        if (_loggedSendFailure) return;
        _loggedSendFailure = true;
        BepinPlugin.Log.LogWarning(
            "[ChatVivoxFix] SendChannelTextMessageAsync faulted (suppressing further). If this repeats, " +
            $"the '_chat' channel may lack text capability: {t.Exception?.GetBaseException()}");
    }

    internal static void AttachReceiveHandler(VivoxAdapter adapter)
    {
        if (adapter == null) return;
        LogResolveOnce();
        if (_fireMessageReceived == null) return;

        try
        {
            DetachReceiveHandler(); // never stack handlers across adapter re-creation

            _channelHandler = msg =>
            {
                try
                {
                    // Our own message is echoed explicitly in TrySend; ignore the self copy.
                    if (msg == null || msg.FromSelf) return;
                    _fireMessageReceived.Invoke(adapter, new object[] { msg.SenderPlayerId, msg.MessageText });
                }
                catch (Exception e)
                {
                    if (_loggedReceiveFailure) return;
                    _loggedReceiveFailure = true;
                    BepinPlugin.Log.LogWarning(
                        $"[ChatVivoxFix] incoming channel message not delivered (suppressing further): {e}");
                }
            };
            VivoxService.Instance.ChannelMessageReceived += _channelHandler;
        }
        catch (Exception e)
        {
            _channelHandler = null;
            BepinPlugin.Log.LogWarning(
                $"[ChatVivoxFix] could not attach the channel-message receiver — remote messages may not appear: {e}");
        }
    }

    internal static void DetachReceiveHandler()
    {
        if (_channelHandler == null) return;
        try { VivoxService.Instance.ChannelMessageReceived -= _channelHandler; }
        catch
        {
            // Service already torn down — nothing to detach from.
        }
        _channelHandler = null;
    }
}

// Route outgoing chat through the group text channel instead of the per-recipient
// directed-message loop that depends on a participant list the room-join race breaks.
[HarmonyPatch(typeof(VivoxAdapter), nameof(VivoxAdapter.SendTextMessage))]
internal static class VivoxChannelSendPatch
{
    static bool Prefix(VivoxAdapter __instance, string message)
        => !ChatVivoxSendFix.TrySend(__instance, message);
}

// Outgoing messages are now channel messages, so listen on ChannelMessageReceived —
// the game only subscribes DirectedMessageReceived.
[HarmonyPatch(typeof(VivoxAdapter), MethodType.Constructor, new Type[] { })]
internal static class VivoxChannelReceivePatch
{
    static void Postfix(VivoxAdapter __instance) => ChatVivoxSendFix.AttachReceiveHandler(__instance);
}

[HarmonyPatch(typeof(VivoxAdapter), nameof(VivoxAdapter.Dispose))]
internal static class VivoxChannelReceiveCleanupPatch
{
    static void Postfix() => ChatVivoxSendFix.DetachReceiveHandler();
}
