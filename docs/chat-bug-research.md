# Void Crew chat bugs — research notes

**Date:** 2026-07-19 · **Game:** Void Crew 1.3.0 (Unity 2022.3.62) · **VoidManager:** 1.2.11

Written after the 26-07-19 two-client test, where chat problems blocked client-side command
testing entirely: the non-host could not run `!difficulty`, which in turn made it *look* like
escalation state wasn't syncing. It was — the logs show `→ sent` / `← applied` pairs matching
on non-default values (see `TODO`, 8-E).

Two distinct symptoms were reported:

1. Chat "does not seem to work" — messages appear to send but the other player sees nothing.
2. A player who goes AFK for even a second can no longer use chat; input is "eaten".

**Both are vanilla game bugs.** Neither is caused by VoidCrewTerminus, and neither is fixed by
VoidManager. Symptom 2 has a mod-side workaround (shipped); symptom 1 does not (documented only).

---

## Symptom 2 — chat input is "eaten"

### Mechanism

`UI.Chat.TextChatVE.GetMessage()` blanks the field while it still holds focus and a live cursor:

```csharp
public string GetMessage()
{
    string text = ((TextInputBaseField<string>)(object)inputField).text;
    ((BaseField<string>)(object)inputField).value = "";   // cursorIndex/selectIndex still index the OLD string
    return text;
}
```

`TextChatVE.HideInput()` only toggles CSS classes:

```csharp
public void HideInput()
{
    ((VisualElement)this).RemoveFromClassList("text-chat__active");
    inputContainer.AddToClassList("chat-log-input__disabled");
}
```

There is **no `Blur()` and no `SelectNone()` anywhere in the chat code**. The UIElements panel
therefore keeps routing keydown events into a hidden text editor whose selection range now
points past the end of an empty string. The next keystroke throws:

```
ArgumentOutOfRangeException: Specified argument was out of the range of valid values.
Parameter name: startIndex
  System.String.Insert (System.Int32 startIndex, System.String value)
  UnityEngine.TextEditingUtilities.ReplaceSelection (System.String replace)
  UnityEngine.TextEditingUtilities.Insert (System.Char c)
  UnityEngine.UIElements.KeyboardTextEditorEventHandler.OnKeyDown (...)
  ...
  UnityEngine.UIElements.PanelEventHandler.Update ()
```

That exact stack appears repeatedly in the captured player log. Unity's own issue tracker
documents this signature as throwing **on every keypress** once the indices desync:

- [ArgumentOutOfRangeException thrown every time a keyboard key is pressed (UI Builder rename)](https://issuetracker.unity3d.com/issues/argumentoutofrangeexception-errors-are-thrown-everytime-a-keyboard-key-is-pressed-when-renaming-a-component-in-ui-builder-with-a-symbol-and-changing-the-name-after-label-attribute-warning)
- [TextFields in SkinningEditor lead to ArgumentOutOfRangeException in ReplaceSelection](https://issuetracker.unity3d.com/issues/attempting-to-edit-bone-data-via-textfields-in-skinningeditor-leads-to-argumentoutofrangeexception)

`SetMessage()` has the same hazard via `SetValueWithoutNotify`.

**The game already knows the correct pattern.** `UI/UIToolkitNavigationExtensions.OnGainedFocus`
does it properly:

```csharp
nav.FocusLostCallback = delegate {
    ((TextInputBaseField<string>)(object)tf).SelectNone();
    ((Focusable)tf).Blur();
};
```

The chat widget simply doesn't.

### Why it's permanent, not transient

`Gameplay.Chat.TextChat.OpenChat` early-returns unless the `"TextChatting"` character state is
false:

```csharp
if (!StateManager.GetState(((Component)LocalPlayer.I).gameObject, "TextChatting"))
{
    SetInput(); _chatUI.ShowInput(); _frameOpened = Time.frameCount; _chatOpened = true;
}
```

`SetInput()` sets that state true; **only `RemoveInput()` clears it** — and `RemoveInput()` is
itself guarded:

```csharp
private void RemoveInput()
{
    if (Object.op_Implicit((Object)(object)LocalPlayer.I))
    {
        InputActionMapRequests.RemoveRequest(this);
        StateManager.SetState(((Component)LocalPlayer.I).gameObject, "TextChatting", false);
        _textingEnabled = false;
    }
}
```

If `LocalPlayer.I` is momentarily null (respawn, scene transition) the cleanup silently skips,
the flag stays true, and `OpenChat` refuses on every subsequent Enter. Nothing else in the
codebase resets it. There is no timeout and no recovery path.

### There is no AFK system

Grepped the full decompiled source for `AFK`, `Afk`, `IdleTimer`, `idleTime`, `AwayFromKeyboard`,
`InactivityTimeout` — zero hits outside unrelated AI code (`ActTimedIdle`). "AFK" is the
community's name for what is actually a window-focus / state-latch symptom. The log's
`--- OnApplicationFocused: False` / `True` lines come from `MenuScreenController.OnApplicationFocus`.

### Fix shipped

`VoidCrewTerminus/Patches/ChatInputFixPatch.cs`:

| Patch | Target | Effect |
|---|---|---|
| Postfix | `TextChatVE.GetMessage` | `SelectNone()` + `Blur()` after the field is blanked |
| Postfix | `TextChatVE.HideInput` | same, for closing chat without sending |
| Prefix | `TextChat.OpenChat` | clears a stale `"TextChatting"` state via the game's own `RemoveInput()` |

The prefix works precisely because `OpenChat` dereferences `LocalPlayer.I` — by then the player
reference is alive, so the cleanup that failed earlier now succeeds.

Config gate: `EnableChatInputFix` (default on). All members resolved via `AccessTools` with
one-shot logging, so a game update that moves the vanilla UI makes the fix inert and *says so*
rather than failing `CreateAndPatchAll`.

---

## Symptom 1 — chat sends nothing (documented, NOT fixed)

`VivoxAdapter.SendTextMessage` returns silently when the channel isn't in the dictionary:

```csharp
public void SendTextMessage(string message)
{
    if (!channelUsers.TryGetValue(textChannel, out var value))
    {
        return;                       // silent — no log, no error
    }
    foreach (VivoxParticipant value3 in value.Forward.Values) { ... }
    FireMessageReceivedEvent(VivoxService.Instance.SignedInPlayerId, message);   // local echo
}
```

Two consequences: messages are sent as **per-recipient direct messages** rather than to the
group channel, so an empty participant list transmits nothing; and the sender still gets a
**local echo**, so chat looks like it works from their side.

`textChannel` goes empty because of an un-awaited race in `VoipBoostrap.OnJoinedRoom`:

```csharp
public void OnJoinedRoom()
{
    Debug.Log((object)("[VIVOX] JOINED ROOM: " + PhotonNetwork.CurrentRoom.Name));
    VoipService.Instance?.LeaveChannels();      // returns Task — discarded, never awaited
    if (VoipService.Instance != null)
    {
        VoipService.Instance.JoinChannels();    // fires immediately
    }
}
```

`LeaveChannels()` wraps `LeaveAllChannelsAsync()` and also does `AudioChannel = ""`,
`textChannel = ""`, `channelUsers.Clear()`. If the in-flight leave resolves *after* the joins
are issued, it tears down the channels just joined. The captured log shows exactly that ordering:

```
[VIVOX] Joining channel: 677419_chat
[VIVOX] Joining channel: 677419_audio
...
[VIVOX] Left channel: 677419_audio
[VIVOX] Left channel: 677419_chat
```

The fingerprint is the follow-on error, with a **blank channel name** and an **empty participant
list**:

```
[Vivox] Player not found.
 name: DCyBHwT2BsszBpRxEYWWhIk8DAJu
 channel:
Players in channel:
```

That comes from `GetParticipant`, which prints `AudioChannel` — blank because `LeaveChannels`
emptied it. (Secondary bug: `GetParticipant` ignores its own `channel` parameter and always
indexes `AudioChannel`.)

**Not fixed deliberately.** A fix means reordering async Vivox lifecycle from a Harmony patch;
getting it wrong breaks voice chat as well as text. Worth an upstream report.

### A second, independent way chat can fail to arm

`TextChat.OnEnable` subscribes the chat hotkeys only inside an async continuation:

```csharp
VoipService.Instance.ChatEnabled.ContinueWith(delegate(Task<bool> task) {
    if (task.IsFaulted) Debug.LogError(...);
    if (task.Result) EnableChatInput();      // rethrows if faulted; continuation dies here
});
```

`ChatEnabled` is a **property that starts a fresh task on every access**
(`PAL.Privileges.HasCommunicationsPrivilege(false)`), so the three call sites get unrelated
tasks with no ordering between them. The continuations also run on the ThreadPool — no
`TaskScheduler.FromCurrentSynchronizationContext()`, which `VivoxAdapter` itself passes
correctly elsewhere — so `InputAction.performed` delegate lists get mutated off the main thread.
A disable/enable pair landing out of order leaves the component enabled with zero handlers
attached, and Enter does nothing.

---

## VoidManager is orthogonal

Its README's only chat claims, verbatim:

> * Unlocks mouse while using text chat
> * Chat input history
> * Command Auto-complete via tab key-press

The plugin description says *"minor Chat improvements"*. The word "fixes" does not appear.

All three of its chat patches are **postfix-only and purely additive**:

| Patch | Target | Purpose |
|---|---|---|
| `ChatCommandDetectPatch` | `TextChatVE.GetMessage` | route `/` commands |
| `PublicCommandDetectPatch` | `TextChat.IncomingMessage` | parse `!` commands |
| `TextChatVEPatch` | `ShowInput` / `HideInput` | raise `ChatWindowOpened/Closed` events |

Nothing prefixes, transpiles, or replaces `OpenChat`, `SetInput`, `RemoveInput`,
`EnableChatInput`, `DisableChatInput`, `OnEnable`, or `OnDisable`. Nothing touches the
`"TextChatting"` state or field focus.

One aggravating interaction worth noting: `CursorUnlock` is driven by the `ShowInput`/`HideInput`
postfixes, so if the game latches into a state where `HideInput` never runs, VoidManager leaves
the cursor unlocked — which makes the breakage *feel* worse but is not its cause.

---

## VoidCrewTerminus is not implicated

This mod only *consumes* `VoidManager.Chat.Router` to register commands. Its full Harmony target
list is gameplay/UI-context only:

`AIDirector`, `BuildBox`, `CarryableFactoryLogic`, `CarryableInteract`, `CompositeWeaponBuildBox`,
`ContextInfoProvider`, `Deconstruct`, `DestroyableComponent`, `FabricationTab`, `HubShipManager`,
`LootManager`, `OrbitObject`, `RecycleTab`, `SpawnerProfile`

— nothing touching chat, input, or focus. (`ChatInputFixPatch` above is the first, added *after*
this research.)

---

## What could not be determined

- **`InputActionsGameConfigTable` is a `ScriptableObject` loaded from `Resources`**, so which
  actions are enabled/disabled per input state lives in an asset, not in code. A third candidate
  mechanism — `InputActionMapRequests` promoting `MenuScreenController` above `TextChat` on the
  request stack and disabling the very actions needed to escape — is therefore plausible but
  **unconfirmed**. Related defects visible in code: `SetTopState()` skips entirely when the
  request list empties (last state stays latched), and `MenuScreenController` only ever calls
  `AddOrChangeRequest`, never `RemoveRequest`.
- **The engine-internal step from "exception during dispatch" to "all later keydowns dropped"**
  is inferred from Unity's issue reports, not read from source. The stale-index throw is proven;
  the exact dispatcher state left behind is not.
- **The chat UXML/USS is not available**, so whether `chat-log-input__disabled` sets
  `display: none` — which would change the focus semantics — is unverified.
- **VoidManager 1.2.11's binary was not inspected** (only 1.2.7 and 1.2.10 are in the local NuGet
  cache). Chat strings are identical between those two and the master-branch source is labelled
  1.2.11, so drift is unlikely but unconfirmed.
- **Upstream issue tracker not fully checked.** `Nihility-Shift/VoidManager` reports 0 issues,
  but its README badges point at `Void-Crew-Modding-Team/VoidManager`, which may hold the real
  tracker. Not checked.
- **No deterministic repro was found for either symptom.** Both are timing-dependent. The fix
  for symptom 2 is therefore reasoned from source, not validated against a reproduction.

## Sources

- Decompiled game source, `.voidcrew/decompiled/Assembly-CSharp/`:
  `Gameplay.Chat/TextChat.cs`, `UI.Chat/TextChatVE.cs`, `VivoxAdapter.cs`, `VoipBoostrap.cs`,
  `CG.Input/InputActionMapRequests.cs`, `MenuScreenController.cs`,
  `UI/UIToolkitNavigationExtensions.cs`
- [VoidManager README](https://github.com/Nihility-Shift/VoidManager) and `VoidManager/Chat/` source
- Unity Issue Tracker entries linked above
- Player logs `VoidCrewTerminus-LogOutput-2026-07-20_00-44-00.log` (client) and `…00-44-03.log` (host)
