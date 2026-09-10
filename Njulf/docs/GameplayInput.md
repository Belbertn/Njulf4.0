# Gameplay input

`Game.Input` exposes the complete ordinary input API. Cache actions during `Load`; the host publishes
their values before each `Update`, and publishes all action states before button callbacks. Operations
require the game thread. The manager owns actions; state access and binding changes throw after disposal.
Names are case-sensitive and unique across button, float and vector actions.

```csharp
var gameplay = Input.CreateContext("Gameplay");
var menus = Input.CreateContext("Menus");
var pause = Input.CreateAction("Pause"); // Global: available in either context.
pause.AddBinding(new InputBinding(InputKey.Escape));
pause.AddBinding(new InputBinding(GamepadButton.Start));

var move = Input.CreateVector2Action("Move", gameplay);
move.AddBinding(new InputVector2Binding(
    new InputBinding(InputKey.A), new InputBinding(InputKey.D),
    new InputBinding(InputKey.S), new InputBinding(InputKey.W)));
move.AddBinding(new InputVector2Binding(GamepadStick.Left));

var throttle = Input.CreateFloatAction("Throttle", gameplay);
throttle.AddBinding(new InputFloatBinding(GamepadAxis.RightTrigger));
var look = Input.CreateVector2Action("MouseLook", gameplay, InputValueMode.Delta);
look.AddBinding(InputVector2Binding.MouseMotion(scale: .002f));
var zoom = Input.CreateFloatAction("Zoom", gameplay, InputValueMode.Delta);
zoom.AddBinding(new InputFloatBinding(MouseAxis.WheelY));
Input.ActiveContext = gameplay;
Input.SetCursorMode(InputCursorMode.Captured);
```

## Values and bindings

| Action | Sources | Combination and units |
| --- | --- | --- |
| `InputAction` | Keys, mouse/joystick/gamepad buttons, thresholded axes | OR; `IsDown`, `WasPressed`, `WasReleased` |
| `InputFloatAction`, State | Buttons, negative/positive button pairs, joystick/gamepad axes | Greatest absolute value; `[-1,1]` |
| `InputVector2Action`, State | Four buttons, scalar-axis pairs, gamepad sticks | Greatest vector magnitude; maximum length one |
| Float/Vector2, Delta | Mouse motion, wheel axes, matching scalar-axis pairs | Sum; scaled pixels or platform wheel steps per input update |

State alternatives use binding order to break ties. Opposing composite directions cancel; diagonals
are normalized. Triggers start at zero and reach one. Gamepad stick Y is positive up; mouse Y is
positive down. Raw joystick axes retain native directions; invert a component with negative `scale`
when constructing an axis pair.

State and delta bindings cannot share an action, and both parts of an axis pair must have the same mode.
Keep mouse look and stick look separate: multiply stick input by elapsed seconds to produce rotation;
mouse displacement is already accumulated over the update interval and must not be multiplied by time.
Delta action reads do not consume input. Reading `ConsumeMouseDelta` does not change a delta action's
published value, and action updates do not clear legacy unconsumed mouse motion.

Dead zones are scalar for float axes and radial for vector sticks: zero inside the zone, then a linear
rescaling of the remaining magnitude to one. Defaults are `.15` for sticks/joysticks and `.05` for
triggers. Thresholds must be finite and in `[0,1)`; scales must be finite. Axis pairs default to no
additional radial dead zone; set child scalar dead zones to zero when supplying a radial zone yourself.

The current adapter uses the repository's Silk 2.23 GLFW conventions, including signed native triggers
and downward-positive native stick Y. It disables native joystick/gamepad dead zones and restores the
borrowed settings on disposal. Gamepad buttons use standard names, not joystick button offsets.
Device zero is the first device registered in its family. Slots are retained for the session, including
disconnected slots; unavailable devices produce zero and held buttons release once on disconnect.
Connection changes attach new devices automatically. Slot numbers are not persistent hardware identities.

## Contexts, cursor and text

Exactly one named context may be active, alongside global actions created without a context. Setting
`ActiveContext` requests a change for the next input update, even from a button callback. Deactivated
actions become neutral and held buttons release once. On activation, state actions already held must
return to neutral before activating; inactive delta actions discard their updates. A null active context
enables only globals. Contexts cannot be shared between managers.

The game decides when a menu opens and whether it needs the cursor:

```csharp
Input.ActiveContext = menus;
Input.SetCursorMode(InputCursorMode.Normal);
Input.TextInput += OnCharacter;
// Subscribe/unsubscribe for the lifetime of the text consumer, or gate inside OnCharacter.
```

`Normal` is visible/unrestricted, `Hidden` is invisible/unrestricted, and `Captured` prefers raw relative
motion with disabled/relative motion as fallback. Unsupported modes throw `NotSupportedException`.
`CursorMode` reports the requested mode. Focus loss suspends actions and public text, cancels rebinding,
clears transient motion, and releases capture. Focus return restores the requested cursor and requires
held state actions to become neutral. Cursor-mode changes also reset motion to avoid warp jumps.

`TextInput` forwards platform UTF-16 characters, including repeated characters; it does not infer text
from physical key presses. The consumer handles text editing and subscription lifetime. IME composition
UI is outside this API. Concrete native cursor methods and raw events remain available to editor adapters;
raw events are not suppressed by contexts or rebinding.

## Rebinding and persistence

All action types expose read-only `Bindings`, `AddBinding`, `RemoveBinding`, `ReplaceBinding` and
`ClearBindings`. Binding edits affect the next snapshot. `BeginRebind` targets an **existing** binding slot;
register defaults before capturing. Button actions capture a complete binding. Float/vector actions can
also target `Negative`/`Positive`, `Left`/`Right`/`Down`/`Up`, or `X`/`Y` on the corresponding composite.

```csharp
InputRebindSession capture = move.BeginRebind(0, InputBindingPart.Up);
// During Update: inspect capture.Status (Pending, Completed, Cancelled).
// capture.Cancel() or Dispose() cancels without replacing the binding.
```

One session may be pending per manager. All actions and public text are suspended during capture.
Initially held controls are ignored until neutral; a fresh compatible control completes capture. Analog
movement must exceed `.5`, mouse motion must accumulate at least eight pixels, and wheel input must be
nonzero. Whole vector capture chooses a stick/axis pair or mouse displacement source; rebind individual
digital directions through composite parts. Standard gamepads take precedence over their duplicate
joystick representations. Source replacement preserves sensitivity/inversion, and preserves the dead
zone when replacing the same source kind.

Escape cancels by default; supply another `cancelKey`, or null to allow capturing Escape. Losing focus,
disposing the session, or changing/removing its target slot cancels it. Captured controls must return to
neutral before state actions resume, preventing a capture from triggering gameplay.

```csharp
// After creating all actions and adding defaults:
bool loaded = Input.LoadBindings(path); // false if absent; defaults remain usable.
// Explicitly save when the player accepts changes:
Input.SaveBindings(path);
```

Files are versioned JSON containing current binding lists by action name, type and mode, including
device slots, composite parts and processing settings. Empty lists persist unbinding. Context definitions
and the active context are code-owned and are not loaded from the file. Unknown action names are skipped;
known incompatible or malformed entries and unsupported versions throw without changing any bindings.
Load outside input callbacks and capture sessions. Successful loading gates held state controls until
neutral. Save creates parent directories, writes a temporary sibling file and replaces the destination.
There is no automatic saving, default-profile service, conflict resolution or user-data directory policy.

## Runnable example

```powershell
dotnet run --project Njulf.ApiExamples -c Debug -- --example input --bindings artifacts/input-example/bindings.json
```

WASD/left stick move; mouse/right stick look; right trigger increases speed; wheel changes base speed.
Space/A demonstrates Jump. Escape/Start switches gameplay/menu; F12 exits. In the menu, characters print
to the console, Enter/A accepts text, and arrows/D-pad demonstrate menu navigation.

Menu keys: F2 rebinds Jump, F3 forward, F4 the movement stick, F5 saves, F6 reloads, F7 the wheel,
F8 mouse look, F9 throttle. Escape cancels capture. Without `--bindings`, the example uses
`input-bindings.json` beside its executable. This is a demonstration, not a settings screen.

Focused validation:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Debug --filter "FullyQualifiedName~Njulf.Tests.Input|FullyQualifiedName~Njulf.Tests.FrameworkArchitectureTests"
dotnet build Njulf.ApiExamples/Njulf.ApiExamples.csproj -c Debug
```
