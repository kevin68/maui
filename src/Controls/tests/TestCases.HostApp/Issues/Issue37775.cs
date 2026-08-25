using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Location = Microsoft.Maui.Devices.Sensors.Location;
using Map = Microsoft.Maui.Controls.Maps.Map;

namespace Maui.Controls.Sample.Issues;

[Issue(IssueTracker.Github, 37775, "[Android] Map: default cluster bubble bypasses the cluster icon cache", PlatformAffected.Android)]
public class Issue37775 : ContentPage
{
	// 30 tight groups spread far apart, so each group stays its own cluster at both zoom
	// levels below and every pass has to build ~30 cluster icons.
	const int GroupCount = 30;
	const int PinsPerGroup = 10;
	const int ZoomCycles = 10;

	static readonly Location Center = new(37.7749, -122.4194);
	static readonly MapSpan Far = MapSpan.FromCenterAndRadius(Center, Distance.FromKilometers(20));
	static readonly MapSpan Near = MapSpan.FromCenterAndRadius(Center, Distance.FromKilometers(8));

	readonly Map _map;
	readonly Label _status;
	readonly Label _uncachedResult;
	readonly Label _cachedResult;
	readonly Button _uncachedButton;
	readonly Button _cachedButton;

	long _uncachedDelta;
	long _cachedDelta;

	public Issue37775()
	{
		_status = new Label
		{
			Text = "Add an API key to Platforms/Android/AndroidManifest.xml, then run both measurements.",
			Margin = new Thickness(10),
			AutomationId = "Status"
		};

		_uncachedResult = new Label { Margin = new Thickness(10, 0), AutomationId = "UncachedResult" };
		_cachedResult = new Label { Margin = new Thickness(10, 0), AutomationId = "CachedResult" };

		_uncachedButton = new Button { Text = "1. Default bubble (uncached)", AutomationId = "RunUncached" };
		_uncachedButton.Clicked += async (_, _) => await RunAsync(useCustomImage: false);

		_cachedButton = new Button { Text = "2. Custom image (cached)", AutomationId = "RunCached" };
		_cachedButton.Clicked += async (_, _) => await RunAsync(useCustomImage: true);

		_map = new Map { IsClusteringEnabled = true };

		AddPins();
		_map.MoveToRegion(Far);

		var buttons = new HorizontalStackLayout
		{
			Spacing = 5,
			Padding = 10,
			Children = { _uncachedButton, _cachedButton }
		};

		var grid = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Star),
			}
		};

		grid.Add(_status, 0, 0);
		grid.Add(buttons, 0, 1);
		grid.Add(_uncachedResult, 0, 2);
		grid.Add(_cachedResult, 0, 3);
		grid.Add(_map, 0, 4);

		Content = grid;
	}

	void AddPins()
	{
		// Deterministic layout - no Random, so two runs cluster identically.
		for (int g = 0; g < GroupCount; g++)
		{
			double groupLat = Center.Latitude + ((g / 6) - 2) * 0.09;
			double groupLon = Center.Longitude + ((g % 6) - 3) * 0.09;

			for (int p = 0; p < PinsPerGroup; p++)
			{
				_map.Pins.Add(new Pin
				{
					Label = $"Pin {g}-{p}",
					Address = "Cluster member",
					Type = PinType.Place,
					Location = new Location(groupLat + p * 0.0004, groupLon + p * 0.0004)
				});
			}
		}
	}

	async Task RunAsync(bool useCustomImage)
	{
		_uncachedButton.IsEnabled = _cachedButton.IsEnabled = false;
		try
		{
			// Both arms are identical except for which icon path AddClusterMarkerAsync takes:
			// a custom image goes through the handler's icon cache, a null one falls through to
			// CreateClusterIcon, which allocates a 100x100 ARGB_8888 bitmap per cluster per pass.
			_map.ClusterImageSource = useCustomImage ? ImageSource.FromFile("coffee.png") : null;

			_status.Text = useCustomImage ? "Running cached arm..." : "Running uncached arm...";

			// Settle the map and let the first pass (and its one-off allocations) finish before
			// the baseline sample, so the delta only covers the repeated passes.
			_map.MoveToRegion(Far);
			await SettleAsync();
			Collect();
			await SettleAsync();

			long before = NativeHeapBytes();

			for (int i = 0; i < ZoomCycles; i++)
			{
				_map.MoveToRegion(i % 2 == 0 ? Near : Far);
				await SettleAsync();
			}

			long delta = NativeHeapBytes() - before;

			if (useCustomImage)
			{
				_cachedDelta = delta;
				_cachedResult.Text = $"Cached (custom image):   {Mb(delta)} over {ZoomCycles} zoom cycles";
			}
			else
			{
				_uncachedDelta = delta;
				_uncachedResult.Text = $"Uncached (default bubble): {Mb(delta)} over {ZoomCycles} zoom cycles";
			}

			_status.Text = _uncachedDelta != 0 && _cachedDelta != 0
				? $"Ratio uncached/cached: {(double)_uncachedDelta / _cachedDelta:F1}x. " +
				  $"Both arms drew ~{GroupCount} clusters over {ZoomCycles} passes; only the icon path differs."
				: "Now run the other measurement to compare.";
		}
		finally
		{
			_uncachedButton.IsEnabled = _cachedButton.IsEnabled = true;
		}
	}

	// The map has no "camera settled" callback to await, so the passes are paced by a delay.
	// This is a repro page, not a UI test; the numbers below are not timing-sensitive.
	static Task SettleAsync() => Task.Delay(1500);

	static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";

	static void Collect()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
#if ANDROID
		Java.Lang.JavaSystem.Gc();
#endif
	}

	static long NativeHeapBytes()
	{
#if ANDROID
		// Bitmap pixel data lives in the native heap since API 26, so this is where
		// CreateClusterIcon's per-pass bitmaps show up.
		return Android.OS.Debug.NativeHeapAllocatedSize;
#else
		return GC.GetTotalMemory(false);
#endif
	}
}
