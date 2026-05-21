using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Clipboard;

internal enum ClipboardDataType
{
	Image,
	Html,
	Rtf,
	Text,
	Unknown
}

public static class ClipboardManager
{
	private static ClipboardMonitorForm? _monitorForm;
	private static string _concatenatedText = string.Empty;
	private static int _concatenationCount = 0;

	// キーボードフック関連
	private static IntPtr _hookID = IntPtr.Zero;
	private static NativeMethods.LowLevelKeyboardProc? _proc;
	private static bool _ctrlPressed = false;

	// 前回保存した内容を記憶
	private static string _lastSavedContent = string.Empty;

	public static void Start()
	{
		_monitorForm = new ClipboardMonitorForm();
		_monitorForm.Show();

		// キーボードフックを設定
		_proc = HookCallback;
		_hookID = SetHook(_proc);

		Logger.Debug("ClipboardManager: クリップボード監視とキーボードフックを開始しました。");
	}

	public static void Stop()
	{
		// キーボードフックを解除
		if (_hookID != IntPtr.Zero)
		{
			NativeMethods.UnhookWindowsHookEx(_hookID);
			_hookID = IntPtr.Zero;
		}

		if (_monitorForm != null)
		{
			_monitorForm.Close();
			_monitorForm.Dispose();
			_monitorForm = null;
			Logger.Debug("ClipboardManager: クリップボード監視とキーボードフックを停止しました。");
		}
	}

	public static string GetSaveDirectoryPath()
	{
		string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		string dateFolder = DateTime.Now.ToString("yyyyMMdd");
		return Path.Combine(appData, "yumayo", "clipboard", dateFolder);
	}

	private static IntPtr SetHook(NativeMethods.LowLevelKeyboardProc proc)
	{
		using var curProcess = Process.GetCurrentProcess();
		using var curModule = curProcess.MainModule;
		return NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, proc, NativeMethods.GetModuleHandle(curModule?.ModuleName), 0);
	}

	private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
	{
		if (nCode >= 0)
		{
			int vkCode = Marshal.ReadInt32(lParam);

			if (wParam == (IntPtr)NativeMethods.WM_KEYDOWN)
			{
				if (vkCode == NativeMethods.VK_LCONTROL || vkCode == NativeMethods.VK_RCONTROL)
				{
					if (!_ctrlPressed)
					{
						_ctrlPressed = true;
						Logger.Debug("ClipboardManager: Ctrlキーが押されました。");
					}
				}
			}
			else if (wParam == (IntPtr)NativeMethods.WM_KEYUP)
			{
				if (vkCode == NativeMethods.VK_LCONTROL || vkCode == NativeMethods.VK_RCONTROL)
				{
					if (_ctrlPressed)
					{
						_ctrlPressed = false;
						Logger.Debug("ClipboardManager: Ctrlキーが離されました。");

						// Ctrlが離されたときに連結テキストとカウンターをクリア
						if (!string.IsNullOrEmpty(_concatenatedText))
						{
							Logger.Debug("ClipboardManager: Ctrlキーが離されたため、連結テキストをクリアしました。");
							_concatenatedText = string.Empty;
							_concatenationCount = 0;
						}
					}
				}
			}
		}

		return NativeMethods.CallNextHookEx(_hookID, nCode, wParam, lParam);
	}

	public static void SaveClipboardToFile()
	{
		try
		{
			// Ctrl押下中でテキスト形式の場合は連結処理
			if (_ctrlPressed && System.Windows.Forms.Clipboard.ContainsText())
			{
				string newText = System.Windows.Forms.Clipboard.GetText();
				if (!string.IsNullOrEmpty(newText))
				{
					if (_concatenatedText != newText)
					{
						// 既存のテキストがある場合は改行して連結
						if (!string.IsNullOrEmpty(_concatenatedText))
						{
							_concatenatedText += "\n\n\n" + newText;
							_concatenationCount++;
						}
						else
						{
							_concatenatedText = newText;
							_concatenationCount = 1;
						}

						// 2回目以降のみクリップボードに書き戻す（1回目はリッチテキスト等を保持するため書き戻さない）
						if (_concatenationCount >= 2)
						{
							System.Windows.Forms.Clipboard.SetText(_concatenatedText);
							Logger.Debug($"ClipboardManager: Ctrl押下中 - テキストを連結してクリップボードに書き戻しました（{_concatenatedText.Length}文字、{_concatenationCount}回目）");
						}
						else
						{
							Logger.Debug($"ClipboardManager: Ctrl押下中 - テキストを連結しました（{_concatenatedText.Length}文字、1回目のため書き戻しなし）");
						}
					}
				}
			}

			// ベースディレクトリパスを生成
			string directoryPath = GetSaveDirectoryPath();

			// ディレクトリが存在しない場合は作成
			if (!Directory.Exists(directoryPath))
			{
				Directory.CreateDirectory(directoryPath);
				Logger.Debug($"ClipboardManager: 出力先ディレクトリを作成しました: {directoryPath}");
			}

			var (bytes, extension) = GetClipboardContentAsBytesWithExtension();

			// 現在のクリップボード内容を取得して識別用の文字列を生成
			string currentContent = CalculateHash(bytes);

			// 前回保存した内容と同じ場合はスキップ
			if (currentContent == _lastSavedContent)
			{
				Logger.Debug("ClipboardManager: 前回保存した内容と同じのため、保存をスキップします。");
				return;
			}

			if (bytes.Length > 0 && !string.IsNullOrEmpty(extension))
			{
				string filePath = GetNextFilePath(directoryPath, extension);
				File.WriteAllBytes(filePath, bytes);
				Logger.Info($"ClipboardManager: クリップボードの内容を保存しました: {filePath}");
				_lastSavedContent = currentContent;
			}
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardManager: クリップボードの保存中にエラーが発生しました。");
		}
	}

	private static string CalculateHash(byte[] bytes)
	{
		using var sha256 = System.Security.Cryptography.SHA256.Create();
		byte[] hash = sha256.ComputeHash(bytes);
		return BitConverter.ToString(hash);
	}

	private static ClipboardDataType GetClipboardDataType()
	{
		if (System.Windows.Forms.Clipboard.ContainsImage())
		{
			return ClipboardDataType.Image;
		}
		else if (System.Windows.Forms.Clipboard.ContainsData(DataFormats.Html))
		{
			return ClipboardDataType.Html;
		}
		else if (System.Windows.Forms.Clipboard.ContainsData(DataFormats.Rtf))
		{
			return ClipboardDataType.Rtf;
		}
		else if (System.Windows.Forms.Clipboard.ContainsText())
		{
			return ClipboardDataType.Text;
		}

		return ClipboardDataType.Unknown;
	}

	private static (byte[] bytes, string extension) GetClipboardContentAsBytesWithExtension()
	{
		ClipboardDataType dataType = GetClipboardDataType();

		switch (dataType)
		{
			case ClipboardDataType.Image:
				var image = System.Windows.Forms.Clipboard.GetImage();
				if (image != null)
				{
					using var ms = new MemoryStream();
					image.Save(ms, ImageFormat.Png);
					return (ms.ToArray(), ".png");
				}
				break;

			case ClipboardDataType.Html:
				if (System.Windows.Forms.Clipboard.TryGetData<string>(DataFormats.Html, out var htmlData))
				{
					return (Encoding.UTF8.GetBytes(htmlData), ".html");
				}
				break;

			case ClipboardDataType.Rtf:
				if (System.Windows.Forms.Clipboard.TryGetData<string>(DataFormats.Rtf, out var rtfData))
				{
					return (Encoding.UTF8.GetBytes(rtfData), ".rtf");
				}
				break;

			case ClipboardDataType.Text:
				string text = System.Windows.Forms.Clipboard.GetText();
				return (Encoding.UTF8.GetBytes(text), ".txt");
		}

		return (Array.Empty<byte>(), "");
	}

	private static string GetNextFilePath(string directoryPath, string extension)
	{
		// 拡張子が.で始まっていない場合は追加
		if (!extension.StartsWith("."))
		{
			extension = "." + extension;
		}

		// ディレクトリ内のすべてのファイルから最大の番号を取得
		int maxNumber = 0;
		if (Directory.Exists(directoryPath))
		{
			var files = Directory.GetFiles(directoryPath);
			foreach (var file in files)
			{
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file);
				if (int.TryParse(fileNameWithoutExtension, out int number))
				{
					if (number > maxNumber)
					{
						maxNumber = number;
					}
				}
			}
		}

		// 次の番号を使用
		int nextNumber = maxNumber + 1;
		string fileName = $"{nextNumber:D4}{extension}";
		string filePath = Path.Combine(directoryPath, fileName);

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
