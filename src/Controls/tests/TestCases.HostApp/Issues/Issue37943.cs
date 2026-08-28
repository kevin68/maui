using System.Text;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Storage;
using Map = Microsoft.Maui.Controls.Maps.Map;

namespace Maui.Controls.Sample.Issues;

// The defect is decode churn, not a wrong picture: the icons still render, they are just
// decoded and rescaled again. Counting those decodes needs one line inside the handler, so
// this page's job is to drive the three scenarios deterministically rather than to measure
// them. To take the measurement, add to the top of MapHandler.LoadPinIconAsync:
//
//     Console.WriteLine($"PROBE decode {System.IO.Path.GetFileName((imageSource as IFileImageSource)?.File)}");
//
// then run this page and watch `adb logcat -s DOTNET`, using the PROBE phase markers below
// to attribute each decode to a phase.
[Issue(IssueTracker.Github, 37943, "Map: pin icons and cluster icons evict each other from the shared icon cache", PlatformAffected.Android)]
public class Issue37943 : ContentPage
{
	// MapHandler.MaxIconCacheSize. Phase 1 needs more distinct pin images than this.
	const int IconCacheCapacity = 64;
	const int DistinctPinImages = 100;

	static readonly Location Center = new(37.7749, -122.4194);

	readonly Map _map;
	readonly Label _status;
	readonly MapSpan _wide = MapSpan.FromCenterAndRadius(Center, Distance.FromKilometers(60));
	readonly MapSpan _tight = MapSpan.FromCenterAndRadius(Center, Distance.FromKilometers(4));

	public Issue37943()
	{
		_map = new Map { IsClusteringEnabled = true, HeightRequest = 400 };
		_map.MoveToRegion(_wide);

		_status = new Label { Text = "Ready", AutomationId = "StatusLabel" };

		var run = new Button { Text = "Run probe", AutomationId = "RunProbeButton" };
		run.Clicked += async (_, _) =>
		{
			run.IsEnabled = false;
			await RunProbeAsync();
			run.IsEnabled = true;
		};

		Content = new VerticalStackLayout
		{
			Padding = 12,
			Spacing = 8,
			Children = { run, _status, _map },
		};
	}

	async Task RunProbeAsync()
	{
		// Phase 1: pin traffic past the capacity keeps the cluster entry least recently used, so a
		// shared cache evicts and re-decodes the cluster image on every pass.
		await Phase("phase1 setup");
		_map.ClusterImageProvider = null;
		_map.ClusterImageSource = ImageSource.FromFile(Icon("cluster_a", 30, 90, 220));
		_map.Pins.Clear();

		for (int i = 0; i < DistinctPinImages; i++)
		{
			// Spread far apart so each stays a cluster of one and actually decodes its icon.
			AddPin($"P{i}", Icon($"pin_{i:D3}", (byte)(i * 2), (byte)(255 - i), 128),
				(i % 10 - 5) * 0.09, (i / 10 - 5) * 0.09, $"solo{i}");
		}

		// Three pins on top of each other so there is a real cluster marker to draw.
		for (int i = 0; i < 3; i++)
		{
			_map.Pins.Add(new Pin
			{
				Label = $"C{i}",
				Address = "clustered",
				Location = new Location(Center.Latitude + 0.001 * i, Center.Longitude),
				Type = PinType.Place,
				ClusteringIdentifier = "grouped",
			});
		}

		await Task.Delay(3000);

		for (int pass = 1; pass <= 6; pass++)
		{
			await Phase($"phase1 pass {pass}");
			_map.MoveToRegion(pass % 2 == 0 ? _wide : _tight);
			await Task.Delay(2500);
		}

		// Phase 2: a cluster image change bumps ClusterImageVersion, and clearing a shared cache
		// takes the pins' decoded icons with it even though no pin ImageSource changed.
		await Phase("phase2 setup");
		_map.Pins.Clear();
		for (int i = 0; i < 5; i++)
			AddPin($"F{i}", Icon($"few_{i}", (byte)(40 * i), 60, 200), (i - 2) * 0.15, (i - 2) * 0.15, $"few{i}");

		await Task.Delay(3000);
		await Phase("phase2 warm");
		_map.MoveToRegion(_wide);
		await Task.Delay(2500);
		_map.MoveToRegion(_tight);
		await Task.Delay(2500);

		await Phase("phase2 bump");
		_map.ClusterImageSource = ImageSource.FromFile(Icon("cluster_b", 220, 90, 30));
		await Task.Delay(2500);
		_map.MoveToRegion(_wide);
		await Task.Delay(2500);

		// Phase 3: switching many live pins to one image - a selection highlight - is one load per
		// pin unless the update path shares the pin cache, plus one more on the next pass.
		await Phase("phase3 setup");
		_map.Pins.Clear();
		var pins = new List<Pin>();
		for (int i = 0; i < 12; i++)
		{
			var pin = AddPin($"S{i}", Icon($"base_{i}", (byte)(20 * i), 200, 60), (i % 4 - 2) * 0.15, (i / 4 - 1) * 0.15, $"sel{i}");
			pins.Add(pin);
		}

		await Task.Delay(4000);

		await Phase("phase3 highlight");
		var highlight = Icon("highlight", 255, 200, 0);
		foreach (var pin in pins)
			pin.ImageSource = ImageSource.FromFile(highlight);

		await Task.Delay(4000);

		// _tight, not _wide: phase 2 left the camera on _wide, and moving there again is not a zoom
		// change, so no recluster pass would run at all.
		await Phase("phase3 recluster");
		_map.MoveToRegion(_tight);
		await Task.Delay(3000);

		await Phase("done");
	}

	Pin AddPin(string label, string file, double latOffset, double lonOffset, string clusterId)
	{
		var pin = new Pin
		{
			Label = label,
			Address = file,
			Location = new Location(Center.Latitude + latOffset, Center.Longitude + lonOffset),
			Type = PinType.Place,
			ClusteringIdentifier = clusterId,
			ImageSource = ImageSource.FromFile(file),
		};

		_map.Pins.Add(pin);
		return pin;
	}

	Task Phase(string name)
	{
		Console.WriteLine($"PROBE {name}");
		_status.Text = $"{name} (cache capacity {IconCacheCapacity}, {DistinctPinImages} distinct pin images)";
		return Task.CompletedTask;
	}

	// One file per distinct cache key: MapHandler keys a file source on its path, so writing the
	// same bytes to different paths is enough to fill the cache. The colour only makes the pins
	// tellable apart on screen.
	static string Icon(string name, byte r, byte g, byte b)
	{
		var dir = Path.Combine(FileSystem.CacheDirectory, "issue37943");
		Directory.CreateDirectory(dir);

		var path = Path.Combine(dir, name + ".bmp");
		if (!File.Exists(path))
			File.WriteAllBytes(path, Bmp(r, g, b));

		return path;
	}

	// Minimal 2x2 24-bit BMP. The handler rescales every icon to 64x64 anyway, so the source size
	// is irrelevant and this avoids shipping an asset per distinct image.
	static byte[] Bmp(byte r, byte g, byte b)
	{
		const int width = 2, height = 2;
		const int stride = 8;             // 2 px * 3 bytes, padded to a 4-byte boundary
		const int pixels = stride * height;
		const int offset = 54;            // file header + info header

		var bytes = new byte[offset + pixels];
		var w = new BinaryWriter(new MemoryStream(bytes));

		w.Write(Encoding.ASCII.GetBytes("BM"));
		w.Write(bytes.Length);
		w.Write(0);                       // reserved
		w.Write(offset);
		w.Write(40);                      // info header size
		w.Write(width);
		w.Write(height);
		w.Write((short)1);                // planes
		w.Write((short)24);               // bits per pixel
		w.Write(0);                       // BI_RGB
		w.Write(pixels);
		w.Write(0);                       // x pixels per metre
		w.Write(0);                       // y pixels per metre
		w.Write(0);                       // palette colours used
		w.Write(0);                       // palette colours important

		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				var i = offset + y * stride + x * 3;
				bytes[i] = b;             // BMP stores BGR
				bytes[i + 1] = g;
				bytes[i + 2] = r;
			}
		}

		return bytes;
	}
}
