using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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
	private const int ForegroundRestoreTimeoutMilliseconds = 500;
	private const int ForegroundRestorePollIntervalMilliseconds = 20;
	private const int WindowClassNameCapacity = 256;
	private const int ClipboardSetRetryCount = 20;
	private const int ClipboardSetRetryDelayMilliseconds = 25;
	private const int ClipboardOpenFailedErrorCode = unchecked((int)0x800401D0);
	private const char ByteOrderMark = '\uFEFF';
	private const string ConsoleWindowClassName = "ConsoleWindowClass";
	private const string WindowsTerminalClassName = "CASCADIA_HOSTING_WINDOW_CLASS";
	private static ClipboardMonitorWindow? _monitorWindow;
	private static ClipboardHistoryWindow? _historyWindow;
	private static string _concatenatedText = string.Empty;
	private static int _concatenationCount = 0;
	private static readonly object _concatenationLock = new();

	private static IntPtr _hookID = IntPtr.Zero;
	private static NativeMethods.LowLevelKeyboardProc? _proc;
	private static Thread? _hookThread;
	private static Dispatcher? _hookDispatcher;
	private static readonly ManualResetEventSlim _hookThreadReady = new(false);
	private static readonly object _hookLock = new();
	private static volatile bool _ctrlPressed = false;
	private static bool _winKeySuppressed = false;
	private static bool _winComboHandled = false;
	private static bool _swallowWinVKeyUp = false;
	private static int _suppressedWinKeyCode = 0;
	private static uint _hookThreadId = 0;
	private static int _historyConsumedKeyUpCode = 0;
	private static volatile bool _historyWindowVisible = false;

	private static string _lastSavedContent = string.Empty;
	private static bool _suppressNextClipboardSave = false;

	public static void Start()
	{
		_monitorWindow = new ClipboardMonitorWindow();

		_proc = HookCallback;
		StartKeyboardHookThread();

		Logger.Debug("ClipboardManager: クリップボード監視とキーボードフックを開始しました。");
	}

	public static void Stop()
	{
		StopKeyboardHookThread();

		if (_monitorWindow != null)
		{
			_monitorWindow.Dispose();
			_monitorWindow = null;
			Logger.Debug("ClipboardManager: クリップボード監視とキーボードフックを停止しました。");
		}

		if (_historyWindow != null)
		{
			_historyWindowVisible = false;
			_historyWindow.CloseWindow();
			_historyWindow = null;
		}
	}

	public static string GetSaveDirectoryPath()
	{
		return ClipboardSettings.ApplicationDirectoryPath;
	}

	public static void ShowHistoryWindow()
	{
		ShowHistoryFromHotKey();
	}

	public static bool PasteHistoryEntry(long historyId, IntPtr targetWindow, bool preferPlainTextPaste = false)
	{
		try
		{
			ClipboardStoredContent? content = ClipboardDatabase.LoadContent(historyId);
			if (content == null)
			{
				Logger.Warning($"ClipboardManager: 履歴が見つかりません: Id={historyId}");
				return false;
			}

			bool useConsolePaste = preferPlainTextPaste || IsConsoleLikeWindow(targetWindow);
			RestoreClipboardFromContent(content, useConsolePaste);
			PasteToTargetWindow(targetWindow, useConsolePaste);
			return true;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, $"ClipboardManager: 履歴の貼り付けに失敗しました: Id={historyId}");
			MessageBox.Show("履歴の貼り付けに失敗しました。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
			return false;
		}
	}

	private static IntPtr SetHook(NativeMethods.LowLevelKeyboardProc proc)
	{
		using var curProcess = Process.GetCurrentProcess();
		using var curModule = curProcess.MainModule;
		return NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, proc, NativeMethods.GetModuleHandle(curModule?.ModuleName), 0);
	}

	private static void StartKeyboardHookThread()
	{
		if (_hookThread != null)
		{
			return;
		}

		_hookThreadReady.Reset();
		_hookThread = new Thread(KeyboardHookThreadMain)
		{
			IsBackground = true,
			Name = "Clipboard Keyboard Hook"
		};
		_hookThread.SetApartmentState(ApartmentState.STA);
		_hookThread.Start();

		if (!_hookThreadReady.Wait(TimeSpan.FromSeconds(3)))
		{
			Logger.Warning("ClipboardManager: キーボードフックスレッドの開始がタイムアウトしました。");
		}
	}

	private static void StopKeyboardHookThread()
	{
		_hookDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);

		uint hookThreadId = _hookThreadId;
		if (hookThreadId != 0 &&
			!NativeMethods.PostThreadMessage(hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero))
		{
			Logger.Warning($"ClipboardManager: キーボードフックスレッドの終了通知に失敗しました。Win32Error={Marshal.GetLastWin32Error()}");
		}

		if (_hookThread is { IsAlive: true } hookThread && !hookThread.Join(TimeSpan.FromSeconds(2)))
		{
			Logger.Warning("ClipboardManager: キーボードフックスレッドが時間内に終了しませんでした。");
		}

		lock (_hookLock)
		{
			if (_hookID != IntPtr.Zero)
			{
				NativeMethods.UnhookWindowsHookEx(_hookID);
				_hookID = IntPtr.Zero;
			}
		}

		_hookThread = null;
		_hookDispatcher = null;
		_hookThreadId = 0;
		_hookThreadReady.Reset();
		ResetSuppressedWindowsKeyState();
		_swallowWinVKeyUp = false;
	}

	private static void KeyboardHookThreadMain()
	{
		_hookThreadId = NativeMethods.GetCurrentThreadId();
		NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);
		_hookDispatcher = Dispatcher.CurrentDispatcher;

		IntPtr hookID = _proc == null ? IntPtr.Zero : SetHook(_proc);
		lock (_hookLock)
		{
			_hookID = hookID;
		}

		if (hookID == IntPtr.Zero)
		{
			Logger.Warning($"ClipboardManager: キーボードフックの設定に失敗しました。Win32Error={Marshal.GetLastWin32Error()}");
		}

		_hookThreadReady.Set();

		try
		{
			Dispatcher.Run();
		}
		finally
		{
			lock (_hookLock)
			{
				if (_hookID != IntPtr.Zero)
				{
					NativeMethods.UnhookWindowsHookEx(_hookID);
					_hookID = IntPtr.Zero;
				}
			}

			_hookThreadId = 0;
			_hookDispatcher = null;
			ResetSuppressedWindowsKeyState();
			_swallowWinVKeyUp = false;
		}
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

			if (TryHandleHistoryKey(vkCode, isKeyDown, isKeyUp))
			{
				return (IntPtr)1;
			}

			if (isKeyDown)
			{
				if (vkCode == NativeMethods.VK_V && (_winKeySuppressed || IsPhysicalWindowsKeyDown()))
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
					if (!_winKeySuppressed)
					{
						_winKeySuppressed = true;
						_winComboHandled = false;
						_suppressedWinKeyCode = vkCode;
					}

					return (IntPtr)1;
				}
				else if (_winKeySuppressed)
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

						lock (_concatenationLock)
						{
							if (!string.IsNullOrEmpty(_concatenatedText))
							{
								Logger.Debug("ClipboardManager: Ctrlキーが離されたため、連結テキストをクリアしました。");
								_concatenatedText = string.Empty;
								_concatenationCount = 0;
							}
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

	public static void SaveClipboardToDatabase()
	{
		try
		{
			if (_suppressNextClipboardSave)
			{
				_suppressNextClipboardSave = false;
				Logger.Debug("ClipboardManager: 履歴からの復元のため、保存をスキップしました。");
				return;
			}

			if (_ctrlPressed && System.Windows.Clipboard.ContainsText())
			{
				string newText = System.Windows.Clipboard.GetText();
				if (!string.IsNullOrEmpty(newText))
				{
					lock (_concatenationLock)
					{
						if (_concatenatedText != newText)
						{
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

							if (_concatenationCount >= 2)
							{
								System.Windows.Clipboard.SetText(_concatenatedText);
								Logger.Debug($"ClipboardManager: Ctrl押下中 - テキストを連結してクリップボードに書き戻しました（{_concatenatedText.Length}文字、{_concatenationCount}回目）");
							}
							else
							{
								Logger.Debug($"ClipboardManager: Ctrl押下中 - テキストを連結しました（{_concatenatedText.Length}文字、1回目のため書き戻しなし）");
							}
						}
					}
				}
			}

			var (bytes, kind) = GetClipboardContentAsBytes();
			string currentContent = CalculateHash(bytes);
			if (currentContent == _lastSavedContent)
			{
				Logger.Debug("ClipboardManager: 前回保存した内容と同じのため、保存をスキップします。");
				return;
			}

			if (bytes.Length > 0 && kind != ClipboardHistoryKind.Unknown)
			{
				ClipboardDatabase.InsertHistory(kind, bytes, currentContent, DateTime.Now);
				Logger.Info($"ClipboardManager: クリップボードの内容をDBに保存しました。Kind={kind} Size={bytes.Length}");
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
		if (System.Windows.Clipboard.ContainsImage())
		{
			return ClipboardDataType.Image;
		}
		else if (System.Windows.Clipboard.ContainsData(DataFormats.Html))
		{
			return ClipboardDataType.Html;
		}
		else if (System.Windows.Clipboard.ContainsData(DataFormats.Rtf))
		{
			return ClipboardDataType.Rtf;
		}
		else if (System.Windows.Clipboard.ContainsText())
		{
			return ClipboardDataType.Text;
		}

		return ClipboardDataType.Unknown;
	}

	private static (byte[] bytes, ClipboardHistoryKind kind) GetClipboardContentAsBytes()
	{
		ClipboardDataType dataType = GetClipboardDataType();

		switch (dataType)
		{
			case ClipboardDataType.Image:
				BitmapSource? image = System.Windows.Clipboard.GetImage();
				if (image != null)
				{
					using var ms = new MemoryStream();
					var encoder = new PngBitmapEncoder();
					encoder.Frames.Add(BitmapFrame.Create(image));
					encoder.Save(ms);
					return (ms.ToArray(), ClipboardHistoryKind.Image);
				}
				break;

			case ClipboardDataType.Html:
				if (GetClipboardStringData(DataFormats.Html) is { } htmlData)
				{
					return (Encoding.UTF8.GetBytes(htmlData), ClipboardHistoryKind.Html);
				}
				break;

			case ClipboardDataType.Rtf:
				if (GetClipboardStringData(DataFormats.Rtf) is { } rtfData)
				{
					return (Encoding.UTF8.GetBytes(rtfData), ClipboardHistoryKind.Rtf);
				}
				break;

			case ClipboardDataType.Text:
				string text = System.Windows.Clipboard.GetText();
				return (Encoding.UTF8.GetBytes(text), ClipboardHistoryKind.Text);
		}

		return (Array.Empty<byte>(), ClipboardHistoryKind.Unknown);
	}

	private static string? GetClipboardStringData(string format)
	{
		return System.Windows.Clipboard.GetData(format) as string;
	}

	private static void ShowHistoryFromHotKey()
	{
		IntPtr targetWindow = NativeMethods.GetForegroundWindow();
		Logger.Debug($"ClipboardManager: Win+V 対象ウィンドウを取得しました。TargetWindow={FormatHandle(targetWindow)}");
		DispatchToUi(() => ShowHistoryWindow(targetWindow));
	}

	private static void DispatchToUi(Action action)
	{
		Dispatcher? dispatcher = Application.Current?.Dispatcher;
		if (dispatcher == null || dispatcher.CheckAccess())
		{
			action();
			return;
		}

		dispatcher.BeginInvoke(action);
	}

	private static string FormatHandle(IntPtr handle)
	{
		return $"0x{handle.ToInt64():X}";
	}

	private static void ShowHistoryWindow(IntPtr targetWindow)
	{
		try
		{
			if (_historyWindow == null || _historyWindow.IsClosed)
			{
				_historyWindow = new ClipboardHistoryWindow();
				_historyWindow.VisibleStateChanged += isVisible => _historyWindowVisible = isVisible;
			}

			_historyWindow.ShowHistory(targetWindow);
			_historyWindowVisible = _historyWindow.IsVisible;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardManager: 履歴画面を開けませんでした。");
			MessageBox.Show("履歴画面を開けませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private static void RestoreClipboardFromContent(ClipboardStoredContent content, bool pasteAsPlainText)
	{
		switch (content.Kind)
		{
			case ClipboardHistoryKind.Image:
				SetClipboardImage(content.Bytes);
				break;

			case ClipboardHistoryKind.Html:
				SetClipboardHtml(Encoding.UTF8.GetString(content.Bytes), pasteAsPlainText);
				break;

			case ClipboardHistoryKind.Rtf:
				SetClipboardRtf(Encoding.UTF8.GetString(content.Bytes), pasteAsPlainText);
				break;

			default:
				SetClipboardText(Encoding.UTF8.GetString(content.Bytes));
				break;
		}
	}

	private static void SetClipboardText(string text)
	{
		text = NormalizeTextForClipboardPaste(text);
		SetClipboardWithRetry(() => System.Windows.Clipboard.SetText(text), "Text");
	}

	private static void SetClipboardImage(byte[] bytes)
	{
		using var stream = new MemoryStream(bytes);
		var bitmap = new BitmapImage();
		bitmap.BeginInit();
		bitmap.CacheOption = BitmapCacheOption.OnLoad;
		bitmap.StreamSource = stream;
		bitmap.EndInit();
		bitmap.Freeze();

		SetClipboardWithRetry(() => System.Windows.Clipboard.SetImage(bitmap), "Image");
	}

	private static void SetClipboardHtml(string html, bool pasteAsPlainText)
	{
		if (pasteAsPlainText)
		{
			SetClipboardText(ConvertHtmlToPlainText(html));
			return;
		}

		var dataObject = new DataObject();
		dataObject.SetData(DataFormats.Html, html);

		string plainText = NormalizeTextForClipboardPaste(ConvertHtmlToPlainText(html));
		if (!string.IsNullOrWhiteSpace(plainText))
		{
			dataObject.SetText(plainText);
		}

		SetClipboardWithRetry(() => System.Windows.Clipboard.SetDataObject(dataObject, true), "Html");
	}

	private static void SetClipboardRtf(string rtf, bool pasteAsPlainText)
	{
		if (pasteAsPlainText)
		{
			SetClipboardText(ConvertRtfToPlainText(rtf));
			return;
		}

		var dataObject = new DataObject();
		dataObject.SetData(DataFormats.Rtf, rtf);

		string plainText = NormalizeTextForClipboardPaste(ConvertRtfToPlainText(rtf));
		if (!string.IsNullOrWhiteSpace(plainText))
		{
			dataObject.SetText(plainText);
		}

		SetClipboardWithRetry(() => System.Windows.Clipboard.SetDataObject(dataObject, true), "Rtf");
	}

	private static void SetClipboardWithRetry(Action setClipboard, string contentType)
	{
		for (int attempt = 1; attempt <= ClipboardSetRetryCount; attempt++)
		{
			try
			{
				_suppressNextClipboardSave = true;
				setClipboard();
				return;
			}
			catch (Exception ex) when (IsClipboardOpenFailure(ex))
			{
				_suppressNextClipboardSave = false;
				if (attempt >= ClipboardSetRetryCount)
				{
					throw;
				}

				Logger.Debug($"ClipboardManager: クリップボードへの復元を再試行します。Type={contentType} Attempt={attempt}/{ClipboardSetRetryCount} Error={ex.Message}");
				Thread.Sleep(ClipboardSetRetryDelayMilliseconds);
			}
			catch
			{
				_suppressNextClipboardSave = false;
				throw;
			}
		}
	}

	private static bool IsClipboardOpenFailure(Exception exception)
	{
		return exception is ExternalException externalException &&
			externalException.ErrorCode == ClipboardOpenFailedErrorCode;
	}

	private static string NormalizeTextForClipboardPaste(string text)
	{
		if (text.Length == 0 || text[0] != ByteOrderMark)
		{
			return text;
		}

		int startIndex = 0;
		while (startIndex < text.Length && text[startIndex] == ByteOrderMark)
		{
			startIndex++;
		}

		return text[startIndex..];
	}

	private static void PasteToTargetWindow(IntPtr targetWindow, bool useConsolePaste)
	{
		if (targetWindow == IntPtr.Zero || !NativeMethods.IsWindow(targetWindow))
		{
			Logger.Warning("ClipboardManager: 貼り付け先ウィンドウを特定できませんでした。");
			return;
		}

		if (!NativeMethods.SetForegroundWindow(targetWindow))
		{
			Logger.Warning($"ClipboardManager: 貼り付け先ウィンドウを前面化できませんでした。TargetWindow={FormatHandle(targetWindow)} Win32Error={Marshal.GetLastWin32Error()}");
		}

		if (!WaitForForegroundWindow(targetWindow))
		{
			Logger.Warning($"ClipboardManager: 貼り付け先ウィンドウの前面化を確認できませんでした。TargetWindow={FormatHandle(targetWindow)} ForegroundWindow={FormatHandle(NativeMethods.GetForegroundWindow())}");
		}

		SendPasteShortcut(targetWindow, useConsolePaste);
	}

	private static void SendPasteShortcut(IntPtr targetWindow, bool useConsolePaste)
	{
		if (useConsolePaste)
		{
			Logger.Debug($"ClipboardManager: Console向け貼り付けショートカットを送信します。TargetWindow={FormatHandle(targetWindow)}");
			SendKeyCombination(NativeMethods.VK_SHIFT, NativeMethods.VK_INSERT);
			return;
		}

		SendKeyCombination(NativeMethods.VK_CONTROL, NativeMethods.VK_V);
	}

	private static bool IsConsoleLikeWindow(IntPtr window)
	{
		string className = GetWindowClassName(window);
		if (string.Equals(className, ConsoleWindowClassName, StringComparison.Ordinal) ||
			string.Equals(className, WindowsTerminalClassName, StringComparison.Ordinal))
		{
			return true;
		}

		return IsConsoleLikeProcess(window);
	}

	private static string GetWindowClassName(IntPtr window)
	{
		var className = new StringBuilder(WindowClassNameCapacity);
		int length = NativeMethods.GetClassName(window, className, className.Capacity);
		if (length <= 0)
		{
			return string.Empty;
		}

		return className.ToString();
	}

	private static bool IsConsoleLikeProcess(IntPtr window)
	{
		uint threadProcessId = NativeMethods.GetWindowThreadProcessId(window, out uint processId);
		if (threadProcessId == 0 || processId == 0)
		{
			return false;
		}

		try
		{
			using Process process = Process.GetProcessById(unchecked((int)processId));
			string processName = process.ProcessName;
			return string.Equals(processName, "conhost", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(processName, "OpenConsole", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(processName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
		{
			Logger.Debug($"ClipboardManager: 貼り付け先プロセス名を取得できませんでした。TargetWindow={FormatHandle(window)} ProcessId={processId} Error={ex.Message}");
			return false;
		}
	}

	private static bool WaitForForegroundWindow(IntPtr targetWindow)
	{
		long startTicks = Environment.TickCount64;
		do
		{
			if (NativeMethods.GetForegroundWindow() == targetWindow)
			{
				return true;
			}

			Thread.Sleep(ForegroundRestorePollIntervalMilliseconds);
		}
		while (Environment.TickCount64 - startTicks < ForegroundRestoreTimeoutMilliseconds);

		return NativeMethods.GetForegroundWindow() == targetWindow;
	}

	private static string ConvertHtmlToPlainText(string html)
	{
		return ClipboardHtmlTextConverter.ConvertToPlainText(html);
	}

	private static string ConvertRtfToPlainText(string rtf)
	{
		try
		{
			var richTextBox = new System.Windows.Controls.RichTextBox();
			var range = new TextRange(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd);
			using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rtf));
			range.Load(stream, DataFormats.Rtf);
			return range.Text;
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

	private static bool TryHandleHistoryKey(int vkCode, bool isKeyDown, bool isKeyUp)
	{
		if (isKeyUp && vkCode == _historyConsumedKeyUpCode)
		{
			_historyConsumedKeyUpCode = 0;
			return true;
		}

		if (!_historyWindowVisible || !isKeyDown || IsModifierKeyDown())
		{
			return false;
		}

		if (_historyWindow?.IsForegroundWindow() == true)
		{
			return false;
		}

		if (IsHistoryNavigationKey(vkCode))
		{
			_historyConsumedKeyUpCode = vkCode;
			int offset = vkCode == NativeMethods.VK_DOWN ? 1 : -1;
			DispatchToUi(() => _historyWindow?.MoveSelectionFromKeyboard(offset));
			return true;
		}

		if (vkCode == NativeMethods.VK_RETURN)
		{
			_historyConsumedKeyUpCode = vkCode;
			DispatchToUi(() => _historyWindow?.ActivateSelectedItemFromKeyboard());
			return true;
		}

		if (vkCode == NativeMethods.VK_ESCAPE)
		{
			_historyConsumedKeyUpCode = vkCode;
			DispatchToUi(() => _historyWindow?.HideFromKeyboard());
			return true;
		}

		return false;
	}

	private static bool IsHistoryNavigationKey(int vkCode)
	{
		return vkCode == NativeMethods.VK_UP || vkCode == NativeMethods.VK_DOWN;
	}

	private static bool IsModifierKeyDown()
	{
		return IsKeyDown(NativeMethods.VK_CONTROL) ||
			IsKeyDown(NativeMethods.VK_SHIFT) ||
			IsKeyDown(NativeMethods.VK_MENU) ||
			IsPhysicalWindowsKeyDown();
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
		_suppressedWinKeyCode = 0;
	}

	private static void SendKeyPress(int vkCode)
	{
		var inputs = new[]
		{
			CreateKeyInput(vkCode, false),
			CreateKeyInput(vkCode, true)
		};

		SendInput(inputs, "key press");
	}

	private static void SendKeyDown(int vkCode)
	{
		var inputs = new[]
		{
			CreateKeyInput(vkCode, false)
		};

		SendInput(inputs, "key down");
	}

	private static void SendKeyCombination(int modifierVkCode, int keyVkCode)
	{
		var inputs = new[]
		{
			CreateKeyInput(modifierVkCode, false),
			CreateKeyInput(keyVkCode, false),
			CreateKeyInput(keyVkCode, true),
			CreateKeyInput(modifierVkCode, true)
		};

		SendInput(inputs, "key combination");
	}

	private static void SendInput(NativeMethods.Input[] inputs, string operation)
	{
		int inputSize = Marshal.SizeOf<NativeMethods.Input>();
		uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, inputSize);
		if (sent != inputs.Length)
		{
			int win32Error = Marshal.GetLastWin32Error();
			Logger.Warning($"ClipboardManager: SendInput に失敗しました。Operation={operation} Sent={sent}/{inputs.Length} InputSize={inputSize} Win32Error={win32Error}");
		}
	}

	private static NativeMethods.Input CreateKeyInput(int vkCode, bool keyUp)
	{
		return new NativeMethods.Input
		{
			Type = NativeMethods.INPUT_KEYBOARD,
			U = new NativeMethods.InputUnion
			{
				Ki = new NativeMethods.KeyboardInput
				{
					WVk = (ushort)vkCode,
					DwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0
				}
			}
		};
	}

	private sealed class ClipboardMonitorWindow : IDisposable
	{
		private readonly HwndSource _source;
		private bool _registeredWinVHotkey;
		private bool _isDisposed;

		public ClipboardMonitorWindow()
		{
			var parameters = new HwndSourceParameters("Clipboard Monitor")
			{
				Width = 0,
				Height = 0,
				WindowStyle = 0
			};
			_source = new HwndSource(parameters);
			_source.AddHook(WndProc);

			NativeMethods.AddClipboardFormatListener(_source.Handle);

			_registeredWinVHotkey = NativeMethods.RegisterHotKey(
				_source.Handle,
				NativeMethods.HOTKEY_ID_WIN_V,
				NativeMethods.MOD_WIN | NativeMethods.MOD_NOREPEAT,
				NativeMethods.VK_V);

			if (_registeredWinVHotkey)
			{
				Logger.Debug("ClipboardManager: Win+V ホットキーを登録しました。");
			}
			else
			{
				Logger.Warning($"ClipboardManager: Win+V ホットキー登録に失敗しました。低レベルフックで代替します。Win32Error={Marshal.GetLastWin32Error()}");
			}
		}

		public void Dispose()
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;
			if (_registeredWinVHotkey)
			{
				NativeMethods.UnregisterHotKey(_source.Handle, NativeMethods.HOTKEY_ID_WIN_V);
				_registeredWinVHotkey = false;
			}

			NativeMethods.RemoveClipboardFormatListener(_source.Handle);
			_source.RemoveHook(WndProc);
			_source.Dispose();
		}

		private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == NativeMethods.HOTKEY_ID_WIN_V)
			{
				ShowHistoryFromHotKey();
				handled = true;
				return IntPtr.Zero;
			}

			if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
			{
				SaveClipboardToDatabase();
			}

			return IntPtr.Zero;
		}
	}
}
