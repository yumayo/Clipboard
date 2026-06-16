using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace Clipboard;

internal static class Program
{
	private static TrayNotifyIcon? _notifyIcon;

	[STAThread]
	private static void Main()
	{
		Logger.Setup();
		WinForms.Application.EnableVisualStyles();

		var application = new Application
		{
			ShutdownMode = ShutdownMode.OnExplicitShutdown
		};
		RegisterUnhandledExceptionLogging(application);

		try
		{
			ClipboardDatabase.Initialize();
			if (!MigrationProgressWindow.RunIfNeeded())
			{
				Logger.Warning("Program: 旧データの移行が完了しないまま起動を継続します。");
			}

			ClipboardSettings.Load();

			_notifyIcon = new TrayNotifyIcon();
			_notifyIcon.HistoryRequested += (_, _) => ClipboardManager.ShowHistoryWindow();
			_notifyIcon.OpenSaveDirectoryRequested += (_, _) => OpenSaveDirectory();
			_notifyIcon.SettingsRequested += (_, _) => OpenSettings();
			_notifyIcon.ExitRequested += (_, _) => application.Shutdown();

			Logger.Debug("Clipboardを起動しました。");
			ClipboardManager.Start();
			application.Run();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Program: アプリケーションで致命的な例外が発生しました。");
			MessageBox.Show("アプリケーションでエラーが発生しました。ログを確認してください。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
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

	private static void RegisterUnhandledExceptionLogging(Application application)
	{
		application.DispatcherUnhandledException += (_, e) =>
		{
			Logger.Error(e.Exception, "Program: UIスレッドで未処理例外が発生しました。");
			MessageBox.Show("処理中にエラーが発生しました。ログを確認してください。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
			e.Handled = true;
		};

		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
		{
			if (e.ExceptionObject is Exception ex)
			{
				Logger.Error(ex, $"Program: 未処理例外が発生しました。IsTerminating={e.IsTerminating}");
			}
			else
			{
				Logger.Error(null, $"Program: 未処理例外が発生しました。IsTerminating={e.IsTerminating} ExceptionObject={e.ExceptionObject}");
			}
		};

		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			Logger.Error(e.Exception, "Program: タスクで未監視の例外が発生しました。");
			e.SetObserved();
		};
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

internal sealed class TrayNotifyIcon : IDisposable
{
	private readonly WinForms.NotifyIcon _notifyIcon;
	private readonly WinForms.ContextMenuStrip _contextMenu;
	private readonly Icon _icon;
	private bool _isDisposed;

	public event EventHandler? HistoryRequested;
	public event EventHandler? OpenSaveDirectoryRequested;
	public event EventHandler? SettingsRequested;
	public event EventHandler? ExitRequested;

	public TrayNotifyIcon()
	{
		_icon = LoadIcon();
		_contextMenu = CreateContextMenu();
		_notifyIcon = new WinForms.NotifyIcon
		{
			Text = "クリップボード",
			Icon = _icon,
			ContextMenuStrip = _contextMenu,
			Visible = true
		};
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;
		_notifyIcon.Visible = false;
		_notifyIcon.ContextMenuStrip = null;
		_contextMenu.Close();
		_contextMenu.Dispose();
		_notifyIcon.Dispose();
		_icon.Dispose();
	}

	private WinForms.ContextMenuStrip CreateContextMenu()
	{
		var menu = new WinForms.ContextMenuStrip();

		menu.Items.Add("履歴を開く", null, (_, _) => InvokeMenuAction("履歴を開く", HistoryRequested));
		menu.Items.Add("保存先を開く", null, (_, _) => InvokeMenuAction("保存先を開く", OpenSaveDirectoryRequested));
		menu.Items.Add("設定", null, (_, _) => InvokeMenuAction("設定", SettingsRequested));
		menu.Items.Add(new WinForms.ToolStripSeparator());
		menu.Items.Add("終了", null, (_, _) => InvokeMenuAction("終了", ExitRequested));

		return menu;
	}

	private void InvokeMenuAction(string actionName, EventHandler? handler)
	{
		try
		{
			handler?.Invoke(this, EventArgs.Empty);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, $"Program: タスクトレイメニューの処理に失敗しました。Action={actionName}");
			MessageBox.Show($"{actionName} の処理に失敗しました。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private static Icon LoadIcon()
	{
		string iconPath = ResolveIconPath();
		if (!string.IsNullOrEmpty(iconPath))
		{
			try
			{
				return new Icon(iconPath);
			}
			catch (Exception ex)
			{
				Logger.Error(ex, $"Program: アイコンファイルを読み込めませんでした。Path={iconPath}");
			}
		}

		return (Icon)SystemIcons.Application.Clone();
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
