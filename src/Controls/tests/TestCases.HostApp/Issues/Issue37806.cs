using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
#if IOS || MACCATALYST
using MapKit;
using ObjCRuntime;
#endif
using Location = Microsoft.Maui.Devices.Sensors.Location;
using Map = Microsoft.Maui.Controls.Maps.Map;

namespace Maui.Controls.Sample.Issues;

[Issue(IssueTracker.Github, 37806, "[iOS] Map: custom cluster icon is never applied to cluster markers", PlatformAffected.iOS)]
public class Issue37806 : ContentPage
{
	// Deterministic layout: fixed groups at fixed offsets, so every run clusters the same way and a
	// pass either reproduces or it doesn't - no randomness to explain away a differing verdict.
	const int GroupCount = 12;
	const int PinsPerGroup = 6;
	const int Passes = 10;

	static readonly Location Center = new(37.7749, -122.4194);
	static readonly MapSpan Far = MapSpan.FromCenterAndRadius(Center, Distance.FromKilometers(25));
	static readonly MapSpan Near = MapSpan.FromCenterAndRadius(Center, Distance.FromKilometers(12));

	readonly Map _map;
	readonly Label _verdict;
	readonly Label _log;
	readonly Button _checkButton;
	readonly Button _passesButton;

	public Issue37806()
	{
		_verdict = new Label
		{
			Text = "Wait for the map to settle, then run a check.",
			Margin = new Thickness(10, 10, 10, 0),
			FontAttributes = FontAttributes.Bold,
			AutomationId = "Verdict"
		};

		_log = new Label
		{
			Margin = new Thickness(10, 0),
			FontSize = 12,
			AutomationId = "Log"
		};

		_checkButton = new Button { Text = "Check once", AutomationId = "CheckOnce" };
		_checkButton.Clicked += (_, _) => Report(Inspect(), "single");

		_passesButton = new Button { Text = $"Run {Passes} recluster passes", AutomationId = "RunPasses" };
		_passesButton.Clicked += async (_, _) => await RunPassesAsync();

		// The whole point of the repro: a cluster image is set up front, before any pin exists, so
		// there is no ordering subtlety to blame - the handler sees it on the very first cluster pass.
		_map = new Map
		{
			IsClusteringEnabled = true,
			ClusterImageSource = ImageSource.FromFile("coffee.png"),
		};

		AddPins();
		_map.MoveToRegion(Far);

		var buttons = new HorizontalStackLayout
		{
			Spacing = 5,
			Padding = 10,
			Children = { _checkButton, _passesButton }
		};

		var grid = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Star),
			}
		};

		grid.Add(_verdict, 0, 0);
		grid.Add(buttons, 0, 1);
		grid.Add(_log, 0, 2);
		grid.Add(_map, 0, 3);

		Content = grid;
	}

	void AddPins()
	{
		for (int group = 0; group < GroupCount; group++)
		{
			// Groups on a ring far enough apart to stay separate clusters, pins inside a group close
			// enough to always collapse together.
			var angle = group * 2 * Math.PI / GroupCount;
			var groupLat = Center.Latitude + (0.12 * Math.Sin(angle));
			var groupLon = Center.Longitude + (0.12 * Math.Cos(angle));

			for (int i = 0; i < PinsPerGroup; i++)
			{
				_map.Pins.Add(new Pin
				{
					Label = $"Pin {group}-{i}",
					Location = new Location(groupLat + (i * 0.0004), groupLon + (i * 0.0004)),
				});
			}
		}
	}

	async Task RunPassesAsync()
	{
		_passesButton.IsEnabled = false;
		_checkButton.IsEnabled = false;

		try
		{
			var log = new StringBuilder();
			int reproduced = 0;
			int inconclusive = 0;

			for (int pass = 0; pass < Passes; pass++)
			{
				// Alternate the region so MapKit rebuilds its clusters on every pass.
				_map.MoveToRegion(pass % 2 == 0 ? Near : Far);
				await Task.Delay(1500);

				var result = Inspect();
				if (result.Clusters == 0)
					inconclusive++;
				else if (result.Custom == 0)
					reproduced++;

				log.AppendLine($"pass {pass + 1}: {result}");
				_log.Text = log.ToString();
			}

			_verdict.Text = inconclusive == Passes
				? $"INCONCLUSIVE - no cluster was ever found in {Passes} passes."
				: $"{reproduced}/{Passes - inconclusive} conclusive passes reproduced the bug"
					+ (inconclusive > 0 ? $" ({inconclusive} inconclusive)." : ".");
		}
		finally
		{
			_passesButton.IsEnabled = true;
			_checkButton.IsEnabled = true;
		}
	}

	void Report(InspectionResult result, string label)
	{
		_log.Text = $"{label}: {result}";
		_verdict.Text = result.Clusters == 0
			? "INCONCLUSIVE - no cluster annotation on screen yet; wait or zoom out."
			: result.Custom == 0
				? $"REPRODUCED - all {result.Clusters} clusters use MapKit's default marker."
				: result.Custom == result.Clusters
					? $"OK - all {result.Clusters} clusters use the custom icon."
					: $"PARTIAL - {result.Custom}/{result.Clusters} clusters use the custom icon.";
	}

	readonly record struct InspectionResult(int Clusters, int Custom, int DefaultMarker)
	{
		public override string ToString() =>
			$"clusters={Clusters}, custom={Custom}, defaultMarker={DefaultMarker}";
	}

#if IOS || MACCATALYST
	InspectionResult Inspect()
	{
		if (_map.Handler?.PlatformView is not MKMapView mapView)
			return default;

		var annotations = mapView.GetAnnotations(mapView.VisibleMapRect)?.ToArray();
		if (annotations is null)
			return default;

		int clusters = 0, custom = 0, defaultMarker = 0;

		foreach (var annotation in annotations)
		{
			// Resolve the peer from the handle rather than with a managed `is` check: MapKit hands out
			// clusters as protocol wrappers, and missing that is exactly the bug under test - the probe
			// must not share the assumption it is verifying.
			if (annotation.Handle == IntPtr.Zero ||
				Runtime.GetNSObject(annotation.Handle) is not MKClusterAnnotation cluster)
			{
				continue;
			}

			clusters++;

			var view = mapView.ViewForAnnotation(cluster);
			if (view is MKMarkerAnnotationView)
				defaultMarker++;
			else if (view?.ReuseIdentifier == "customClusterPin")
				custom++;
		}

		return new InspectionResult(clusters, custom, defaultMarker);
	}
#else
	InspectionResult Inspect() => default;
#endif
}
