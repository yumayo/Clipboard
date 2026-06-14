using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
	private static ClipboardHistoryForm? _historyForm;
	private static string _concatenatedText = string.Empty;
	private static int _concatenationCount = 0;

	// キーボードフック関連
	private static IntPtr _hookID = IntPtr.Zero;
	private static NativeMethods.LowLevelKeyboardProc? _proc;
	private static bool _ctrlPressed = false;
	private static bool _winVHotkeyRegistered = false;
	private static bool _winKeySuppressed = false;
	private static bool _winComboHandled = false;
	private static bool _swallowWinVKeyUp = false;
	private static int _suppressedWinKeyCode = 0;

	// 前回保存した内容を記憶
	private static string _lastSavedContent = string.Empty;
	private static bool _suppressNextClipboardSave = false;
	private static Image? _restoredImage = null;

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

		if (_historyForm != null)
		{
			_historyForm.Close();
			_historyForm.Dispose();
			_historyForm = null;
		}

		_restoredImage?.Dispose();
		_restoredImage = null;
	}

	public static string GetSaveDirectoryPath()
	{
		string dateFolder = DateTime.Now.ToString("yyyyMMdd");
		return Path.Combine(ClipboardSettings.BaseDirectoryPath, dateFolder);
	}

	public static void ShowHistoryWindow()
	{
		ShowHistoryFromHotKey();
	}

	public static bool PasteHistoryFile(string filePath, IntPtr targetWindow)
	{
		try
		{
			if (!File.Exists(filePath))
			{
				Logger.Warning($"ClipboardManager: 履歴ファイルが見つかりません: {filePath}");
				return false;
			}

			RestoreClipboardFromFile(filePath);
			PasteToTargetWindow(targetWindow);
			return true;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, $"ClipboardManager: 履歴の貼り付けに失敗しました: {filePath}");
			MessageBox.Show("履歴の貼り付けに失敗しました。", "Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
			return false;
		}
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
			var keyboard = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
			if ((keyboard.Flags & NativeMethods.LLKHF_INJECTED) != 0)
			{
				return NativeMethods.CallNextHookEx(_hookID, nCode, wParam, lParam);
			}

			int vkCode = keyboard.VkCode;
			bool isKeyDown = wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN;
			bool isKeyUp = wParam == (IntPtr)NativeMethods.WM_KEYUP || wParam == (IntPtr)NativeMethods.WM_SYSKEYUP;
			bool isWindowsKey = IsWindowsKey(vkCode);

			if (isKeyDown)
			{
				if (vkCode == NativeMethods.VK_V && !_winVHotkeyRegistered && (_winKeySuppressed || IsPhysicalWindowsKeyDown()))
				{
					if (!_swallowWinVKeyUp)
					{
						_swallowWinVKeyUp = true;
						_winComboHandled = true;
						if (!_winKeySuppressed)
						{
							NeutralizeWindowsKey();
						}

						ShowHistoryFromHotKey();
					}

					return (IntPtr)1;
				}

				if (vkCode == NativeMethods.VK_LCONTROL || vkCode == NativeMethods.VK_RCONTROL)
				{
					if (!_ctrlPressed)
					{
						_ctrlPressed = true;
						Logger.Debug("ClipboardManager: Ctrlキーが押されました。");
					}
				}
				else if (isWindowsKey)
				{
					if (!_winVHotkeyRegistered)
					{
						if (!_winKeySuppressed)
						{
							_winKeySuppressed = true;
							_winComboHandled = false;
							_suppressedWinKeyCode = vkCode;
						}

						return (IntPtr)1;
					}
				}
				else if (_winKeySuppressed && !_winVHotkeyRegistered)
				{
					ReplaySuppressedWindowsKeyDown();
				}
			}
			else if (isKeyUp)
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
				else if (isWindowsKey)
				{
					if (_winKeySuppressed && vkCode == _suppressedWinKeyCode)
					{
						bool shouldOpenStart = !_winComboHandled;
						ResetSuppressedWindowsKeyState();
						if (shouldOpenStart)
						{
							SendKeyPress(vkCode);
						}

						return (IntPtr)1;
					}
				}
				else if (vkCode == NativeMethods.VK_V && _swallowWinVKeyUp)
				{
					_swallowWinVKeyUp = false;
					return (IntPtr)1;
				}
			}
		}

		return NativeMethods.CallNextHookEx(_hookID, nCode, wParam, lParam);
	}

	public static void SaveClipboardToFile()
	{
		try
		{
			if (_suppressNextClipboardSave)
			{
				_suppressNextClipboardSave = false;
				Logger.Debug("ClipboardManager: 履歴からの復元のため、保存をスキップしました。");
				return;
			}

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
							_concatenatedText += ClipboardSettings.ConcatenationSeparator + newText;
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

	private static void ShowHistoryFromHotKey()
	{
		IntPtr targetWindow = NativeMethods.GetForegroundWindow();
		if (_monitorForm != null && _monitorForm.IsHandleCreated)
		{
			_monitorForm.BeginInvoke((Action)(() => ShowHistoryWindow(targetWindow)));
			return;
		}

		ShowHistoryWindow(targetWindow);
	}

	private static void ShowHistoryWindow(IntPtr targetWindow)
	{
		try
		{
			if (_historyForm == null || _historyForm.IsDisposed)
			{
				_historyForm = new ClipboardHistoryForm();
			}

			_historyForm.ShowHistory(targetWindow);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardManager: 履歴画面を開けませんでした。");
			MessageBox.Show("履歴画面を開けませんでした。", "Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	private static void RestoreClipboardFromFile(string filePath)
	{
		string extension = Path.GetExtension(filePath).ToLowerInvariant();
		switch (extension)
		{
			case ".png":
				SetClipboardImage(filePath);
				break;

			case ".html":
				SetClipboardHtml(File.ReadAllText(filePath, Encoding.UTF8));
				break;

			case ".rtf":
				SetClipboardRtf(File.ReadAllText(filePath, Encoding.UTF8));
				break;

			default:
				SetClipboardText(File.ReadAllText(filePath, Encoding.UTF8));
				break;
		}
	}

	private static void SetClipboardText(string text)
	{
		_suppressNextClipboardSave = true;
		System.Windows.Forms.Clipboard.SetText(text);
	}

	private static void SetClipboardImage(string filePath)
	{
		using var stream = new MemoryStream(File.ReadAllBytes(filePath));
		using var image = Image.FromStream(stream);
		var bitmap = new Bitmap(image);
		try
		{
			_suppressNextClipboardSave = true;
			System.Windows.Forms.Clipboard.SetImage(bitmap);
			_restoredImage?.Dispose();
			_restoredImage = bitmap;
		}
		catch
		{
			bitmap.Dispose();
			throw;
		}
	}

	private static void SetClipboardHtml(string html)
	{
		var dataObject = new DataObject();
		dataObject.SetData(DataFormats.Html, html);

		string plainText = ConvertHtmlToPlainText(html);
		if (!string.IsNullOrWhiteSpace(plainText))
		{
			dataObject.SetText(plainText);
		}

		_suppressNextClipboardSave = true;
		System.Windows.Forms.Clipboard.SetDataObject(dataObject, true);
	}

	private static void SetClipboardRtf(string rtf)
	{
		var dataObject = new DataObject();
		dataObject.SetData(DataFormats.Rtf, rtf);

		string plainText = ConvertRtfToPlainText(rtf);
		if (!string.IsNullOrWhiteSpace(plainText))
		{
			dataObject.SetText(plainText);
		}

		_suppressNextClipboardSave = true;
		System.Windows.Forms.Clipboard.SetDataObject(dataObject, true);
	}

	private static void PasteToTargetWindow(IntPtr targetWindow)
	{
		if (targetWindow == IntPtr.Zero || !NativeMethods.IsWindow(targetWindow))
		{
			Logger.Warning("ClipboardManager: 貼り付け先ウィンドウを特定できませんでした。");
			return;
		}

		NativeMethods.SetForegroundWindow(targetWindow);
		Thread.Sleep(80);
		SendKeys.SendWait("^v");
	}

	private static string ConvertHtmlToPlainText(string html)
	{
		string fragment = html;
		const string startMarker = "<!--StartFragment-->";
		const string endMarker = "<!--EndFragment-->";
		int start = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
		int end = html.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
		if (start >= 0 && end > start)
		{
			start += startMarker.Length;
			fragment = html[start..end];
		}

		string noTags = Regex.Replace(fragment, "<[^>]+>", " ");
		string decoded = WebUtility.HtmlDecode(noTags);
		return Regex.Replace(decoded, @"\s+", " ").Trim();
	}

	private static string ConvertRtfToPlainText(string rtf)
	{
		try
		{
			using var richTextBox = new RichTextBox();
			richTextBox.Rtf = rtf;
			return richTextBox.Text;
		}
		catch
		{
			return string.Empty;
		}
	}

	private static bool IsWindowsKey(int vkCode)
	{
		return vkCode == NativeMethods.VK_LWIN || vkCode == NativeMethods.VK_RWIN;
	}

	private static bool IsPhysicalWindowsKeyDown()
	{
		return IsKeyDown(NativeMethods.VK_LWIN) || IsKeyDown(NativeMethods.VK_RWIN);
	}

	private static bool IsKeyDown(int vkCode)
	{
		return (NativeMethods.GetAsyncKeyState(vkCode) & unchecked((short)0x8000)) != 0;
	}

	private static void NeutralizeWindowsKey()
	{
		SendKeyPress(NativeMethods.VK_CONTROL);
	}

	private static void ReplaySuppressedWindowsKeyDown()
	{
		if (!_winKeySuppressed)
		{
			return;
		}

		int vkCode = _suppressedWinKeyCode == 0 ? NativeMethods.VK_LWIN : _suppressedWinKeyCode;
		_winKeySuppressed = false;
		_winComboHandled = false;
		SendKeyDown(vkCode);
	}

	private static void ResetSuppressedWindowsKeyState()
	{
		_winKeySuppressed = false;
		_winComboHandled = false;
		_swallowWinVKeyUp = false;
		_suppressedWinKeyCode = 0;
	}

	private static void SendKeyPress(int vkCode)
	{
		var inputs = new[]
		{
			new NativeMethods.Input
			{
				Type = NativeMethods.INPUT_KEYBOARD,
				U = new NativeMethods.InputUnion
				{
					Ki = new NativeMethods.KeyboardInput
					{
						WVk = (ushort)vkCode
					}
				}
			},
			new NativeMethods.Input
			{
				Type = NativeMethods.INPUT_KEYBOARD,
				U = new NativeMethods.InputUnion
				{
					Ki = new NativeMethods.KeyboardInput
					{
						WVk = (ushort)vkCode,
						DwFlags = NativeMethods.KEYEVENTF_KEYUP
					}
				}
			}
		};

		NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
	}

	private static void SendKeyDown(int vkCode)
	{
		var inputs = new[]
		{
			new NativeMethods.Input
			{
				Type = NativeMethods.INPUT_KEYBOARD,
				U = new NativeMethods.InputUnion
				{
					Ki = new NativeMethods.KeyboardInput
					{
						WVk = (ushort)vkCode
					}
				}
			}
		};

		NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
	}

	private class ClipboardMonitorForm : Form
	{
		private bool _registeredWinVHotkey;

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

			_registeredWinVHotkey = NativeMethods.RegisterHotKey(
				Handle,
				NativeMethods.HOTKEY_ID_WIN_V,
				NativeMethods.MOD_WIN | NativeMethods.MOD_NOREPEAT,
				NativeMethods.VK_V);
			_winVHotkeyRegistered = _registeredWinVHotkey;

			if (_registeredWinVHotkey)
			{
				Logger.Debug("ClipboardManager: Win+V ホットキーを登録しました。");
			}
			else
			{
				Logger.Warning($"ClipboardManager: Win+V ホットキー登録に失敗しました。低レベルフックで代替します。Win32Error={Marshal.GetLastWin32Error()}");
			}
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			if (_registeredWinVHotkey)
			{
				NativeMethods.UnregisterHotKey(Handle, NativeMethods.HOTKEY_ID_WIN_V);
				_registeredWinVHotkey = false;
				_winVHotkeyRegistered = false;
			}

			// クリップボード変更通知を解除
			NativeMethods.RemoveClipboardFormatListener(Handle);
			base.OnHandleDestroyed(e);
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == NativeMethods.HOTKEY_ID_WIN_V)
			{
				ShowHistoryFromHotKey();
				return;
			}

			if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
			{
				// クリップボードが更新された
				SaveClipboardToFile();
			}

			base.WndProc(ref m);
		}
	}
}
