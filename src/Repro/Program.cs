using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;

// bug: the trigger (an exception during a property write) fires and escapes to the caller.
// control: identical steps, nothing throws.
// workaround: the exception still happens but is caught inside the callback, so it never escapes SetValue.
var mode = args.Length > 0 ? args[0] : "bug";
if (mode is not ("bug" or "control" or "workaround"))
{
	Console.Error.WriteLine("usage: Repro [bug|control|workaround]");
	return 2;
}

var resultsDirectory = args.Length > 1 ? args[1] : "results";
Directory.CreateDirectory(resultsDirectory);

DispatcherProvider.SetCurrent(new InlineDispatcherProvider());

var throwing = mode != "control";
var catchInsideCallback = mode == "workaround";
var thrownCount = 0;

var scenarios = new List<ScenarioResult>
{
	RunEventHandlerScenario(),
	RunPropertyChangedCallbackScenario(),
	RunOneWayBindingScenario(),
	RunTwoWaySetterScenario(),
};

foreach (var scenario in scenarios)
	Console.WriteLine($"[{mode}] {scenario.Name}: observed '{scenario.Observed}', expected '{scenario.Expected}', stuckValue '{scenario.StuckValue}', thrown {scenario.ThrownCount}, escapedToCaller {scenario.EscapedToCaller}, queuedWritesNeverApplied {scenario.QueuedWritesNeverApplied}, applyingStuck {scenario.ApplyingStuck} -> {(scenario.Observed == scenario.Expected ? "OK" : "STUCK")}");

File.WriteAllText(Path.Combine(resultsDirectory, $"{mode}.json"),
	JsonSerializer.Serialize(new { mode, scenarios }, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"C11-REPRO-DONE mode={mode} scenarios={scenarios.Count}");
return 0;

ScenarioResult RunEventHandlerScenario()
{
	thrownCount = 0;
	var probe = new ProbeObject();
	probe.PropertyChanged += (_, e) =>
	{
		if (e.PropertyName == nameof(ProbeObject.Text) && probe.Text == "throw")
			ThrowIfConfigured("handler failed");
	};
	var escaped = CallAndSwallowTrigger(() => probe.Text = "throw");
	probe.Text = "after";
	return new("PropertyChanged handler throws once, later write", probe.Text, "after", "throw", thrownCount, escaped, CountQueuedWrites(probe, ProbeObject.TextProperty), false);
}

ScenarioResult RunPropertyChangedCallbackScenario()
{
	thrownCount = 0;
	var probe = new ProbeObject
	{
		OnValueChanged = newValue =>
		{
			if (newValue == "throw")
				ThrowIfConfigured("callback failed");
		}
	};
	var escaped = CallAndSwallowTrigger(() => probe.Value = "throw");
	probe.Value = "after";
	return new("BindableProperty propertyChanged callback throws once, later write", probe.Value, "after", "throw", thrownCount, escaped, CountQueuedWrites(probe, ProbeObject.ValueProperty), false);
}

ScenarioResult RunOneWayBindingScenario()
{
	thrownCount = 0;
	var viewModel = new ViewModel();
	var probe = new ProbeObject { BindingContext = viewModel };
	probe.SetBinding(ProbeObject.TextProperty, new Binding(nameof(ViewModel.Name)));
	probe.PropertyChanged += (_, e) =>
	{
		if (e.PropertyName == nameof(ProbeObject.Text) && probe.Text == "throw")
			ThrowIfConfigured("handler failed");
	};
	var escaped = CallAndSwallowTrigger(() => viewModel.Name = "throw");
	viewModel.Name = "from view model";
	return new("OneWay binding: view model change after one throw", probe.Text, "from view model", "throw", thrownCount, escaped, CountQueuedWrites(probe, ProbeObject.TextProperty), false);
}

ScenarioResult RunTwoWaySetterScenario()
{
	thrownCount = 0;
	var viewModel = new TwoWayViewModel { ThrowInSetter = value => { if (value == "throw") ThrowIfConfigured("source setter failed"); } };
	var probe = new ProbeObject { BindingContext = viewModel };
	probe.SetBinding(ProbeObject.TextProperty, new Binding(nameof(TwoWayViewModel.Name), BindingMode.TwoWay));
	probe.SetBinding(ProbeObject.ValueProperty, new Binding(nameof(TwoWayViewModel.Other), BindingMode.TwoWay));
	var escaped = CallAndSwallowTrigger(() => probe.Text = "throw");
	probe.Value = "pushed";
	return new("TwoWay: other property writes back after a source setter threw", viewModel.Other, "pushed", "initial", thrownCount, escaped, CountQueuedWrites(probe, ProbeObject.ValueProperty), IsApplyingStuck(probe));
}

void ThrowIfConfigured(string message)
{
	if (!throwing)
		return;

	thrownCount++;
	try
	{
		throw new InvalidOperationException(message);
	}
	catch (InvalidOperationException) when (catchInsideCallback)
	{
	}
}

static bool CallAndSwallowTrigger(Action action)
{
	try
	{
		action();
		return false;
	}
	catch (Exception exception) when (exception is InvalidOperationException or TargetInvocationException)
	{
		return true;
	}
}

// Reads MAUI internals only to show the root cause (queued writes that are never applied); the value checks use public API.
static int CountQueuedWrites(BindableObject bindable, BindableProperty property)
{
	var getContext = typeof(BindableObject).GetMethod("GetContext", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(BindableProperty)])
		?? throw new InvalidOperationException("BindableObject.GetContext not found");
	var context = getContext.Invoke(bindable, [property])
		?? throw new InvalidOperationException("BindableObject.GetContext returned null");
	var delayedSetters = context.GetType().GetField("DelayedSetters")
		?? throw new InvalidOperationException("BindableObject property context field DelayedSetters not found");
	return (delayedSetters.GetValue(context) as System.Collections.ICollection)?.Count ?? 0;
}

static bool IsApplyingStuck(BindableObject bindable)
{
	var applying = typeof(BindableObject).GetField("_applying", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("BindableObject._applying not found");
	return (bool)applying.GetValue(bindable)!;
}

record ScenarioResult(string Name, string Observed, string Expected, string StuckValue, int ThrownCount, bool EscapedToCaller, int QueuedWritesNeverApplied, bool ApplyingStuck);

class ProbeObject : BindableObject
{
	public Action<string>? OnValueChanged;

	public static readonly BindableProperty TextProperty = BindableProperty.Create(nameof(Text), typeof(string), typeof(ProbeObject), "initial");

	public static readonly BindableProperty ValueProperty = BindableProperty.Create(nameof(Value), typeof(string), typeof(ProbeObject), "initial",
		propertyChanged: static (bindable, _, newValue) => ((ProbeObject)bindable).OnValueChanged?.Invoke((string)newValue));

	public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
	public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
}

class ViewModel : INotifyPropertyChanged
{
	string name = "initial";
	public event PropertyChangedEventHandler? PropertyChanged;
	public string Name { get => name; set { name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }
}

class TwoWayViewModel : INotifyPropertyChanged
{
	string name = "initial";
	string other = "initial";
	public Action<string>? ThrowInSetter;
	public event PropertyChangedEventHandler? PropertyChanged;

	public string Name
	{
		get => name;
		set
		{
			ThrowInSetter?.Invoke(value);
			name = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
		}
	}

	public string Other { get => other; set { other = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Other))); } }
}

class InlineDispatcherProvider : IDispatcherProvider
{
	readonly InlineDispatcher dispatcher = new();
	public IDispatcher? GetForCurrentThread() => dispatcher;
}

class InlineDispatcher : IDispatcher
{
	public bool IsDispatchRequired => false;
	public bool Dispatch(Action action) { action(); return true; }
	public bool DispatchDelayed(TimeSpan delay, Action action) { action(); return true; }
	public IDispatcherTimer CreateTimer() => throw new NotSupportedException();
}
