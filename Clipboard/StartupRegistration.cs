using Microsoft.Win32;

namespace Clipboard;

/// <summary>
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run を使ったログイン時起動の登録を扱う。
/// </summary>
internal static class StartupRegistration
{
	private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "Clipboard";

	public static bool IsEnabled()
	{
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
			if (key?.GetValue(ValueName) is not string command)
			{
				return false;
			}

			return string.Equals(command, BuildCommand(), StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "StartupRegistration: ログイン時起動の登録状態を取得できませんでした。");
			return false;
		}
	}

	public static void SetEnabled(bool enabled)
	{
		using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
		if (enabled)
		{
			key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
			Logger.Debug($"StartupRegistration: ログイン時起動を登録しました。Command={BuildCommand()}");
		}
		else
		{
			key.DeleteValue(ValueName, throwOnMissingValue: false);
			Logger.Debug("StartupRegistration: ログイン時起動の登録を解除しました。");
		}
	}

	private static string BuildCommand()
	{
		string executablePath = Environment.ProcessPath
			?? throw new InvalidOperationException("実行ファイルのパスを取得できませんでした。");
		return $"\"{executablePath}\"";
	}
}
