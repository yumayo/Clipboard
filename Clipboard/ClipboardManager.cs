using System;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Clipboard;

public static class ClipboardManager
{
	private static ClipboardMonitorForm? _monitorForm;

	public static void Start()
	{
		_monitorForm = new ClipboardMonitorForm();
		_monitorForm.Show();
		Logger.Debug("ClipboardManager: クリップボード監視を開始しました。");
	}

	public static void Stop()
	{
		if (_monitorForm != null)
		{
			_monitorForm.Close();
			_monitorForm.Dispose();
			_monitorForm = null;
			Logger.Debug("ClipboardManager: クリップボード監視を停止しました。");
		}
	}

	public static void SaveClipboardToFile()
	{
		try
		{
			// ベースディレクトリパスを生成
			string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			string dateFolder = DateTime.Now.ToString("yyyyMMdd");
			string directoryPath = Path.Combine(appData, "yumayo", "clipboard", dateFolder);

			// ディレクトリが存在しない場合は作成
			if (!Directory.Exists(directoryPath))
			{
				Directory.CreateDirectory(directoryPath);
				Logger.Debug($"ClipboardManager: 出力先ディレクトリを作成しました: {directoryPath}");
			}

			// クリップボードの内容に応じて保存
			if (System.Windows.Forms.Clipboard.ContainsImage())
			{
				// 画像の場合
				var image = System.Windows.Forms.Clipboard.GetImage();
				if (image != null)
				{
					string filePath = GetNextFilePath(directoryPath, ".png");
					image.Save(filePath, ImageFormat.Png);
					Logger.Info($"ClipboardManager: 画像を保存しました: {filePath}");
				}
			}
			else if (System.Windows.Forms.Clipboard.ContainsFileDropList())
			{
				// ファイルのリストの場合
				var files = System.Windows.Forms.Clipboard.GetFileDropList();
				foreach (string sourceFile in files)
				{
					if (File.Exists(sourceFile))
					{
						string extension = Path.GetExtension(sourceFile);
						string filePath = GetNextFilePath(directoryPath, extension);
						File.Copy(sourceFile, filePath, true);
						Logger.Info($"ClipboardManager: ファイルをコピーしました: {filePath}");
					}
					else if (Directory.Exists(sourceFile))
					{
						Logger.Debug($"ClipboardManager: ディレクトリはスキップします: {sourceFile}");
					}
				}
			}
			else if (System.Windows.Forms.Clipboard.ContainsData(DataFormats.Html))
			{
				// HTML形式の場合
				var htmlData = System.Windows.Forms.Clipboard.GetData(DataFormats.Html);
				if (htmlData != null)
				{
					string filePath = GetNextFilePath(directoryPath, ".html");
					File.WriteAllText(filePath, htmlData.ToString()!, Encoding.UTF8);
					Logger.Info($"ClipboardManager: HTMLを保存しました: {filePath}");
				}
			}
			else if (System.Windows.Forms.Clipboard.ContainsData(DataFormats.Rtf))
			{
				// RTF形式の場合
				var rtfData = System.Windows.Forms.Clipboard.GetData(DataFormats.Rtf);
				if (rtfData != null)
				{
					string filePath = GetNextFilePath(directoryPath, ".rtf");
					File.WriteAllText(filePath, rtfData.ToString()!, Encoding.UTF8);
					Logger.Info($"ClipboardManager: RTFを保存しました: {filePath}");
				}
			}
			else if (System.Windows.Forms.Clipboard.ContainsText())
			{
				// テキストの場合
				string clipboardText = System.Windows.Forms.Clipboard.GetText();
				if (!string.IsNullOrEmpty(clipboardText))
				{
					string filePath = GetNextFilePath(directoryPath, ".txt");
					File.WriteAllText(filePath, clipboardText, Encoding.UTF8);
					Logger.Info($"ClipboardManager: テキストを保存しました: {filePath}");
				}
			}
			else
			{
				Logger.Debug("ClipboardManager: サポートされていない形式のデータです。");
			}
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardManager: クリップボードの保存中にエラーが発生しました。");
		}
	}

	private static string GetNextFilePath(string directoryPath, string extension)
	{
		int counter = 1;
		string filePath;

		// 拡張子が.で始まっていない場合は追加
		if (!extension.StartsWith("."))
		{
			extension = "." + extension;
		}

		do
		{
			string fileName = $"{counter:D4}{extension}";
			filePath = Path.Combine(directoryPath, fileName);
			counter++;
		} while (File.Exists(filePath));

		return filePath;
	}

	private class ClipboardMonitorForm : Form
	{
		public ClipboardMonitorForm()
		{
			// 隠しフォームとして設定
			ShowInTaskbar = false;
			WindowState = FormWindowState.Minimized;
			Opacity = 0;
			Width = 0;
			Height = 0;
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			// クリップボード変更通知を登録
			NativeMethods.AddClipboardFormatListener(Handle);
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			// クリップボード変更通知を解除
			NativeMethods.RemoveClipboardFormatListener(Handle);
			base.OnHandleDestroyed(e);
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
			{
				// クリップボードが更新された
				SaveClipboardToFile();
			}

			base.WndProc(ref m);
		}
	}
}
