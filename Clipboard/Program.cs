using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Clipboard;

static class Program
{
	[STAThread]
	static void Main()
	{
		ApplicationConfiguration.Initialize();
		CreateNotifyIcon();
		
		Logger.Setup();

		Logger.Debug("Clipboardを起動しました。");

		ClipboardManager.Start();

		Application.Run();

		ClipboardManager.Stop();
		Logger.Debug("Clipboardを終了しました。");
		Logger.Close();
	}

	private static void CreateNotifyIcon()
	{
		var icon = new NotifyIcon();
		icon.Icon = new Icon("Clipboard.ico");
		icon.ContextMenuStrip = ContextMenu();
		icon.Text = "クリップボード";
		icon.Visible = true;
	}

	private static ContextMenuStrip ContextMenu()
	{
		var menu = new ContextMenuStrip();
		menu.Items.Add("保存先を開く", null, (s, e) => { OpenSaveDirectory(); });
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add("終了", null, (s, e) => { Application.Exit(); });
		return menu;
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
			MessageBox.Show("保存先フォルダを開けませんでした。", "Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}
}
