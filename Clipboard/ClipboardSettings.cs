using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Clipboard;

public sealed class ClipboardSettingsData
{
	public string ConcatenationSeparator { get; set; } = ClipboardSettings.DefaultConcatenationSeparator;
}

public static class ClipboardSettings
{
	public const string DefaultConcatenationSeparator = "\n\n\n";

	private static readonly object Sync = new();
	private static ClipboardSettingsData _current = new();
	private static bool _loaded = false;

	public static string BaseDirectoryPath
	{
		get
		{
			string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			return Path.Combine(appData, "yumayo", "clipboard");
		}
	}

	public static string SettingsFilePath => Path.Combine(BaseDirectoryPath, "settings.json");

	public static string ConcatenationSeparator
	{
		get
		{
			lock (Sync)
			{
				EnsureLoaded();
				return NormalizeSeparator(_current.ConcatenationSeparator);
			}
		}
	}

	public static ClipboardSettingsData GetCopy()
	{
		lock (Sync)
		{
			EnsureLoaded();
			return new ClipboardSettingsData
			{
				ConcatenationSeparator = NormalizeSeparator(_current.ConcatenationSeparator)
			};
		}
	}

	public static void Load()
	{
		lock (Sync)
		{
			_current = LoadFromFile();
			_loaded = true;
		}
	}

	public static void Save(ClipboardSettingsData settings)
	{
		lock (Sync)
		{
			Directory.CreateDirectory(BaseDirectoryPath);
			_current = new ClipboardSettingsData
			{
				ConcatenationSeparator = NormalizeSeparator(settings.ConcatenationSeparator)
			};

			var options = new JsonSerializerOptions
			{
				WriteIndented = true,
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			};
			string json = JsonSerializer.Serialize(_current, options);
			File.WriteAllText(SettingsFilePath, json);
			_loaded = true;
		}
	}

	private static void EnsureLoaded()
	{
		if (_loaded)
		{
			return;
		}

		_current = LoadFromFile();
		_loaded = true;
	}

	private static ClipboardSettingsData LoadFromFile()
	{
		try
		{
			if (!File.Exists(SettingsFilePath))
			{
				return new ClipboardSettingsData();
			}

			string json = File.ReadAllText(SettingsFilePath);
			var settings = JsonSerializer.Deserialize<ClipboardSettingsData>(json);
			return NormalizeSettings(settings);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "設定ファイルの読み込みに失敗しました。デフォルト設定を使用します。");
			return new ClipboardSettingsData();
		}
	}

	private static ClipboardSettingsData NormalizeSettings(ClipboardSettingsData? settings)
	{
		return new ClipboardSettingsData
		{
			ConcatenationSeparator = NormalizeSeparator(settings?.ConcatenationSeparator)
		};
	}

	private static string NormalizeSeparator(string? separator)
	{
		return (separator ?? DefaultConcatenationSeparator).Replace("\r\n", "\n").Replace("\r", "\n");
	}
}
