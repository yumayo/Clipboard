using System.IO;

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
		get => LegacyBaseDirectoryPath;
	}

	public static string ApplicationDirectoryPath
	{
		get
		{
			string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			return Path.Combine(appData, "yumayo");
		}
	}

	public static string LegacyBaseDirectoryPath => Path.Combine(ApplicationDirectoryPath, "clipboard");

	public static string SettingsFilePath => Path.Combine(LegacyBaseDirectoryPath, "settings.json");

	public static string DatabaseFilePath => Path.Combine(ApplicationDirectoryPath, "clipboard.db");

	public static string ImageObjectDirectoryPath => Path.Combine(ApplicationDirectoryPath, "image-objects");

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
			_current = new ClipboardSettingsData
			{
				ConcatenationSeparator = NormalizeSeparator(settings.ConcatenationSeparator)
			};

			ClipboardDatabase.SaveConcatenationSeparator(_current.ConcatenationSeparator);
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
			return NormalizeSettings(new ClipboardSettingsData
			{
				ConcatenationSeparator = ClipboardDatabase.LoadConcatenationSeparator()
			});
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "設定の読み込みに失敗しました。デフォルト設定を使用します。");
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

	internal static string NormalizeSeparator(string? separator)
	{
		return (separator ?? DefaultConcatenationSeparator).Replace("\r\n", "\n").Replace("\r", "\n");
	}
}
