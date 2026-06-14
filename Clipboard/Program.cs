using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Clipboard;

internal static class Program
{
	private static WpfNotifyIcon? _notifyIcon;

	[STAThread]
	private static void Main()
	{
		Logger.Setup();
		ClipboardSettings.Load();

		var application = new Application
		{
			ShutdownMode = ShutdownMode.OnExplicitShutdown
		};

		try
		{
			_notifyIcon = new WpfNotifyIcon();
			_notifyIcon.HistoryRequested += (_, _) => ClipboardManager.ShowHistoryWindow();
			_notifyIcon.OpenSaveDirectoryRequested += (_, _) => OpenSaveDirectory();
			_notifyIcon.SettingsRequested += (_, _) => OpenSettings();
			_notifyIcon.ExitRequested += (_, _) => application.Shutdown();

			Logger.Debug("Clipboardを起動しました。");
			ClipboardManager.Start();
			application.Run();
		}
		finally
		{
			ClipboardManager.Stop();
			_notifyIcon?.Dispose();
			_notifyIcon = null;
			Logger.Debug("Clipboardを終了しました。");
			Logger.Close();
		}
	}

	private static void OpenSettings()
	{
		try
		{
			var settingsWindow = new SettingsWindow();
			settingsWindow.ShowDialog();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "設定画面を開けませんでした。");
			MessageBox.Show("設定画面を開けませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private static void OpenSaveDirectory()
	{
		try
		{
			string directoryPath = ClipboardManager.GetSaveDirectoryPath();
			Directory.CreateDirectory(directoryPath);

			Process.Start(new ProcessStartInfo
			{
				FileName = "explorer.exe",
				Arguments = $"\"{directoryPath}\"",
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "保存先フォルダを開けませんでした。");
			MessageBox.Show("保存先フォルダを開けませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}
}

internal sealed class WpfNotifyIcon : IDisposable
{
	private const uint NotifyIconId = 1;
	private const int CallbackMessage = NativeMethods.WM_APP + 1;
	private readonly HwndSource _source;
	private readonly ContextMenu _contextMenu;
	private NativeMethods.NotifyIconData _notifyIconData;
	private IntPtr _iconHandle;
	private bool _isDisposed;

	public event EventHandler? HistoryRequested;
	public event EventHandler? OpenSaveDirectoryRequested;
	public event EventHandler? SettingsRequested;
	public event EventHandler? ExitRequested;

	public WpfNotifyIcon()
	{
		var parameters = new HwndSourceParameters("Clipboard Notify Icon")
		{
			Width = 0,
			Height = 0,
			WindowStyle = 0
		};
		_source = new HwndSource(parameters);
		_source.AddHook(WndProc);

		_contextMenu = CreateContextMenu();
		AddNotifyIcon();
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;
		_contextMenu.IsOpen = false;
		NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _notifyIconData);
		if (_iconHandle != IntPtr.Zero)
		{
			NativeMethods.DestroyIcon(_iconHandle);
			_iconHandle = IntPtr.Zero;
		}

		_source.RemoveHook(WndProc);
		_source.Dispose();
	}

	private ContextMenu CreateContextMenu()
	{
		var menu = new ContextMenu
		{
			Placement = PlacementMode.MousePoint
		};

		var historyItem = new MenuItem { Header = "履歴を開く" };
		historyItem.Click += (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty);
		menu.Items.Add(historyItem);

		var saveDirectoryItem = new MenuItem { Header = "保存先を開く" };
		saveDirectoryItem.Click += (_, _) => OpenSaveDirectoryRequested?.Invoke(this, EventArgs.Empty);
		menu.Items.Add(saveDirectoryItem);

		var settingsItem = new MenuItem { Header = "設定" };
		settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
		menu.Items.Add(settingsItem);

		menu.Items.Add(new Separator());

		var exitItem = new MenuItem { Header = "終了" };
		exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
		menu.Items.Add(exitItem);

		return menu;
	}

	private void AddNotifyIcon()
	{
		string iconPath = ResolveIconPath();
		if (!string.IsNullOrEmpty(iconPath))
		{
			_iconHandle = NativeMethods.LoadImage(
				IntPtr.Zero,
				iconPath,
				NativeMethods.IMAGE_ICON,
				0,
				0,
				NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
		}

		_notifyIconData = CreateNotifyIconData();
		if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _notifyIconData))
		{
			Logger.Warning($"Program: 通知領域アイコンの追加に失敗しました。Win32Error={Marshal.GetLastWin32Error()}");
			return;
		}

		_notifyIconData.UTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
		NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _notifyIconData);
	}

	private NativeMethods.NotifyIconData CreateNotifyIconData()
	{
		return new NativeMethods.NotifyIconData
		{
			CbSize = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
			HWnd = _source.Handle,
			UID = NotifyIconId,
			UFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_TIP | (_iconHandle == IntPtr.Zero ? 0 : NativeMethods.NIF_ICON),
			UCallbackMessage = CallbackMessage,
			HIcon = _iconHandle,
			SzTip = "クリップボード",
			SzInfo = string.Empty,
			SzInfoTitle = string.Empty
		};
	}

	private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == CallbackMessage)
		{
			int mouseMessage = lParam.ToInt32();
			if (mouseMessage is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_RBUTTONUP or NativeMethods.WM_CONTEXTMENU)
			{
				ShowContextMenu();
				handled = true;
			}
		}

		return IntPtr.Zero;
	}

	private void ShowContextMenu()
	{
		NativeMethods.SetForegroundWindow(_source.Handle);
		_contextMenu.IsOpen = true;
	}

	private static string ResolveIconPath()
	{
		string baseDirectoryPath = Path.Combine(AppContext.BaseDirectory, "Clipboard.ico");
		if (File.Exists(baseDirectoryPath))
		{
			return baseDirectoryPath;
		}

		string currentDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), "Clipboard.ico");
		return File.Exists(currentDirectoryPath) ? currentDirectoryPath : string.Empty;
	}
}
