# maui-bindable-exception-repro

In .NET MAUI, one exception thrown while a `BindableObject` property is being written leaves that property unable to change. Every later write is queued and never applied. A throwing TwoWay source setter also stops write-back for every other TwoWay property on the same object.

- CI: https://github.com/TheCodeTraveler/maui-bindable-exception-repro/actions/runs/45933257519 (green = all three jobs matched)
- Upstream issue: https://github.com/dotnet/maui/issues/40683

## Repro

Needs the .NET SDK 11.0.100 (see `global.json`). No MAUI workload or device.

```sh
dotnet run -c Release --project src/Repro -- bug
```

Each scenario throws once (the app catches it), then writes a new value and reads it back.

| Scenario | Expected | Observed (11.0.10) |
|---|---|---|
| `PropertyChanged` handler throws once | `Text` is `after` | `Text` stays `throw` |
| `BindableProperty` propertyChanged callback throws once | `Value` is `after` | `Value` stays `throw` |
| OneWay binding, handler throws once | target gets the next view model value | target stays `throw` |
| TwoWay source setter throws once | a different TwoWay property still writes back | source stays `initial` |

Versions: `Microsoft.Maui.Controls.Core` 11.0.10, SDK 11.0.100, net11.0. The code is identical on `main` (`90d22f82`).

## Legs

CI (`.github/workflows/repro.yml`) runs three legs and is green only when all three hold. Each leg fails if its marker line `C11-REPRO-DONE mode=<leg>` or `results/<leg>.json` is missing. The job summary has an expected-vs-observed table per leg.

- `bug`: all 4 scenarios must be stuck.
- `control`: identical steps, but nothing throws. All 4 must update.
- `workaround`: the exception still happens but is caught inside the callback, so it never escapes `SetValue`. All 4 must update.

There is no fix leg. The fix is in `BindableObject.SetValueCore` and `SetValueActual`, which user code cannot patch. The workaround leg shows the only app-side option.

## Root cause

In `src/Controls/src/Core/BindableObject.cs`:

- `SetValueCore` sets `context.Attributes |= IsBeingSet` (line 695), runs `SetValueActual` and the `DelayedSetters` drain, then clears the flag (line 711), with no `try`/`finally`. After an exception the flag stays set, so later writes see "already being set", join `DelayedSetters` and never run. `ClearValue` does not reset it.
- `SetValueActual` does `_applying = true; binding.Apply(true); _applying = false;` (lines 772-776), also without `try`/`finally`. `_applying` is one flag for the whole object, so one throwing TwoWay source setter stops write-back for all properties.

The same code is in Xamarin.Forms (`Xamarin.Forms.Core/BindableObject.cs`). Related: xamarin/Xamarin.Forms#8742 (same symptom, closed without a fix) and dotnet/maui#17171 (a view model getter throwing, a different path).

## Recommended fix

```csharp
// SetValueCore: wrap SetValueActual and the DelayedSetters drain
try { /* SetValueActual + drain DelayedSetters */ }
finally { context.Attributes &= ~BindableContextAttributes.IsBeingSet; context.DelayedSetters = null; }

// SetValueActual
_applying = true;
try { binding.Apply(true); }
finally { _applying = false; }
```

Add `BindableObjectUnitTests` cases for the four scenarios above.
