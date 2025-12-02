using System;
using System.Drawing;
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
		menu.Items.Add("終了", null, (s, e) => { Application.Exit(); });
		return menu;
	}
}
