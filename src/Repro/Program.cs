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

var scenarios = new List<ScenarioResult>
{
	RunEventHandlerScenario(),
	RunPropertyChangedCallbackScenario(),
	RunOneWayBindingScenario(),
	RunTwoWaySetterScenario(),
};

foreach (var scenario in scenarios)
	Console.WriteLine($"[{mode}] {scenario.Name}: observed '{scenario.Observed}', expected '{scenario.Expected}' -> {(scenario.Observed == scenario.Expected ? "OK" : "STUCK")}");

File.WriteAllText(Path.Combine(resultsDirectory, $"{mode}.json"),
	JsonSerializer.Serialize(new { mode, scenarios }, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"C11-REPRO-DONE mode={mode} scenarios={scenarios.Count}");
return 0;

ScenarioResult RunEventHandlerScenario()
{
	var probe = new ProbeObject();
	probe.PropertyChanged += (_, e) =>
	{
		if (e.PropertyName == nameof(ProbeObject.Text) && probe.Text == "throw")
			ThrowIfConfigured("handler failed");
	};
	CallAndSwallowTrigger(() => probe.Text = "throw");
	probe.Text = "after";
	return new("PropertyChanged handler throws once, later write", probe.Text, "after", CountQueuedWrites(probe, ProbeObject.TextProperty));
}

ScenarioResult RunPropertyChangedCallbackScenario()
{
	var probe = new ProbeObject
	{
		OnValueChanged = newValue =>
		{
			if (newValue == "throw")
				ThrowIfConfigured("callback failed");
		}
	};
	CallAndSwallowTrigger(() => probe.Value = "throw");
	probe.Value = "after";
	return new("BindableProperty propertyChanged callback throws once, later write", probe.Value, "after", CountQueuedWrites(probe, ProbeObject.ValueProperty));
}

ScenarioResult RunOneWayBindingScenario()
{
	var viewModel = new ViewModel();
	var probe = new ProbeObject { BindingContext = viewModel };
	probe.SetBinding(ProbeObject.TextProperty, new Binding(nameof(ViewModel.Name)));
	probe.PropertyChanged += (_, e) =>
	{
		if (e.PropertyName == nameof(ProbeObject.Text) && probe.Text == "throw")
			ThrowIfConfigured("handler failed");
	};
	CallAndSwallowTrigger(() => viewModel.Name = "throw");
	viewModel.Name = "from view model";
	return new("OneWay binding: view model change after one throw", probe.Text, "from view model", CountQueuedWrites(probe, ProbeObject.TextProperty));
}

ScenarioResult RunTwoWaySetterScenario()
{
	var viewModel = new TwoWayViewModel { ThrowInSetter = value => { if (value == "throw") ThrowIfConfigured("source setter failed"); } };
	var probe = new ProbeObject { BindingContext = viewModel };
	probe.SetBinding(ProbeObject.TextProperty, new Binding(nameof(TwoWayViewModel.Name), BindingMode.TwoWay));
	probe.SetBinding(ProbeObject.ValueProperty, new Binding(nameof(TwoWayViewModel.Other), BindingMode.TwoWay));
	CallAndSwallowTrigger(() => probe.Text = "throw");
	probe.Value = "pushed";
	return new("TwoWay: other property writes back after a source setter threw", viewModel.Other, "pushed", 0);
}

void ThrowIfConfigured(string message)
{
	if (!throwing)
		return;

	try
	{
		throw new InvalidOperationException(message);
	}
	catch (InvalidOperationException) when (catchInsideCallback)
	{
	}
}

static void CallAndSwallowTrigger(Action action)
{
	try
	{
		action();
	}
	catch (Exception exception) when (exception is InvalidOperationException or TargetInvocationException)
	{
	}
}

// Reflection only reports the queue size for the log; the assertions never depend on it.
static int CountQueuedWrites(BindableObject bindable, BindableProperty property)
{
	var getContext = typeof(BindableObject).GetMethod("GetContext", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(BindableProperty)]);
	var context = getContext?.Invoke(bindable, [property]);
	var queue = context?.GetType().GetField("DelayedSetters")?.GetValue(context) as System.Collections.ICollection;
	return queue?.Count ?? 0;
}

record ScenarioResult(string Name, string Observed, string Expected, int QueuedWritesNeverApplied);

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
