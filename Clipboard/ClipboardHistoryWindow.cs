using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clipboard;

internal sealed class ClipboardHistoryWindow : Window
{
	private const int MaxHistoryItems = 100;
	private const int PreviewTextLength = 180;
	private readonly StackPanel _listPanel;
	private CancellationTokenSource? _loadHistoryCancellation;
	private IntPtr _targetWindow;
	private bool _isLoadingHistory;
	private bool _hasLoadedHistory;
	private bool _allowClose;

	public bool IsClosed { get; private set; }

	public ClipboardHistoryWindow()
	{
		Title = "クリップボード履歴";
		WindowStartupLocation = WindowStartupLocation.Manual;
		Width = 520;
		Height = 640;
		MinWidth = 380;
		MinHeight = 320;
		ShowInTaskbar = false;
		Icon = LoadIcon();

		_listPanel = new StackPanel
		{
			Margin = new Thickness(10)
		};

		Content = new ScrollViewer
		{
			Content = _listPanel,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Background = new SolidColorBrush(Color.FromRgb(245, 246, 248))
		};
		IsVisibleChanged += (_, _) =>
		{
			if (!IsVisible)
			{
				CancelHistoryLoad();
			}
		};
	}

	public void ShowHistory(IntPtr targetWindow)
	{
		_targetWindow = targetWindow;
		MoveNearTextInput(targetWindow);

		Topmost = true;
		Show();
		Activate();
		Topmost = false;
		StopWindowFlash();
		Dispatcher.BeginInvoke((Action)StopWindowFlash);

		if (_isLoadingHistory && _loadHistoryCancellation?.IsCancellationRequested == true)
		{
			_isLoadingHistory = false;
		}

		if (!_isLoadingHistory)
		{
			BeginLoadHistory(_hasLoadedHistory && _listPanel.Children.Count > 0);
		}
	}

	public void CloseWindow()
	{
		if (IsClosed)
		{
			return;
		}

		_allowClose = true;
		Close();
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			Hide();
			e.Handled = true;
			return;
		}

		base.OnKeyDown(e);
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		if (!_allowClose)
		{
			e.Cancel = true;
			Hide();
			return;
		}

		IsClosed = true;
		base.OnClosing(e);
	}

	private async void BeginLoadHistory(bool preserveExistingItems)
	{
		CancelHistoryLoad();
		var cancellationTokenSource = new CancellationTokenSource();
		_loadHistoryCancellation = cancellationTokenSource;
		_isLoadingHistory = true;

		if (!preserveExistingItems)
		{
			ClearHistoryControls();
			AddMessage("読み込み中...");
		}

		List<ClipboardHistoryEntry> entries;
		try
		{
			entries = await Task.Run(() => LoadHistoryEntries(cancellationTokenSource.Token), cancellationTokenSource.Token);
		}
		catch (OperationCanceledException)
		{
			if (_loadHistoryCancellation == cancellationTokenSource)
			{
				_isLoadingHistory = false;
			}

			return;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardHistoryWindow: 履歴の読み込みに失敗しました。");
			if (_loadHistoryCancellation == cancellationTokenSource)
			{
				_isLoadingHistory = false;
				if (!preserveExistingItems)
				{
					ClearHistoryControls();
					AddMessage("履歴を読み込めませんでした");
				}
			}

			return;
		}

		if (cancellationTokenSource.IsCancellationRequested || _loadHistoryCancellation != cancellationTokenSource)
		{
			if (_loadHistoryCancellation == cancellationTokenSource)
			{
				_isLoadingHistory = false;
			}

			return;
		}

		_isLoadingHistory = false;
		_hasLoadedHistory = true;
		PopulateHistory(entries);
	}

	private void PopulateHistory(List<ClipboardHistoryEntry> entries)
	{
		ClearHistoryControls();
		if (entries.Count == 0)
		{
			AddMessage("履歴がありません");
			return;
		}

		foreach (var entry in entries)
		{
			var item = new HistoryItemControl(entry);
			item.Activated += (_, _) => PasteEntry(entry);
			_listPanel.Children.Add(item);
		}
	}

	private void PasteEntry(ClipboardHistoryEntry entry)
	{
		Hide();
		ClipboardManager.PasteHistoryFile(entry.FilePath, _targetWindow);
	}

	private void AddMessage(string text)
	{
		_listPanel.Children.Add(new TextBlock
		{
			Text = text,
			Height = 80,
			TextAlignment = TextAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = new SolidColorBrush(Color.FromRgb(96, 96, 96))
		});
	}

	private void ClearHistoryControls()
	{
		_listPanel.Children.Clear();
	}

	private void CancelHistoryLoad()
	{
		if (_loadHistoryCancellation is { IsCancellationRequested: false })
		{
			_loadHistoryCancellation.Cancel();
		}
	}

	private void MoveNearTextInput(IntPtr targetWindow)
	{
		bool hasTextInputBounds = TryGetTextInputBounds(targetWindow, out Rect textInputBounds, out string textInputSource);
		Rect anchorBounds;
		if (hasTextInputBounds)
		{
			anchorBounds = textInputBounds;
			Logger.Debug($"ClipboardHistoryWindow: テキスト入力座標を取得しました。Source={textInputSource} TargetWindow={FormatHandle(targetWindow)} Bounds={anchorBounds}");
		}
		else
		{
			Point cursorPoint = GetCursorPoint();
			anchorBounds = new Rect(cursorPoint, new Size(0, 0));
			Logger.Debug($"ClipboardHistoryWindow: テキスト入力座標を取得できなかったためマウス座標にフォールバックします。TargetWindow={FormatHandle(targetWindow)} Cursor={cursorPoint}");
		}

		Rect workingArea = GetWorkingArea(anchorBounds.Location);
		double windowWidth = Width;
		double windowHeight = Height;
		double x = anchorBounds.Left;
		double y = anchorBounds.Bottom + 8;

		x = Math.Max(workingArea.Left, Math.Min(x, workingArea.Right - windowWidth));
		if (y + windowHeight > workingArea.Bottom && anchorBounds.Top - windowHeight - 8 >= workingArea.Top)
		{
			y = anchorBounds.Top - windowHeight - 8;
		}

		y = Math.Max(workingArea.Top, Math.Min(y, workingArea.Bottom - windowHeight));
		Left = x;
		Top = y;
		Logger.Debug($"ClipboardHistoryWindow: 履歴画面の表示位置を決定しました。Anchor={anchorBounds} WorkingArea={workingArea} Location=({Left},{Top}) Size=({windowWidth},{windowHeight})");
	}

	private static bool TryGetTextInputBounds(IntPtr targetWindow, out Rect bounds, out string source)
	{
		if (TryGetWin32TextInputBounds(targetWindow, out bounds))
		{
			source = "Win32";
			return true;
		}

		if (TryGetAutomationTextInputBounds(out bounds))
		{
			source = "UIAutomation";
			return true;
		}

		source = string.Empty;
		return false;
	}

	private static bool TryGetWin32TextInputBounds(IntPtr targetWindow, out Rect bounds)
	{
		bounds = Rect.Empty;
		if (targetWindow == IntPtr.Zero || !NativeMethods.IsWindow(targetWindow))
		{
			Logger.Debug($"ClipboardHistoryWindow: Win32 テキスト入力座標の取得に失敗しました。対象ウィンドウが無効です。TargetWindow={FormatHandle(targetWindow)}");
			return false;
		}

		uint threadId = NativeMethods.GetWindowThreadProcessId(targetWindow, out _);
		if (threadId == 0)
		{
			Logger.Debug($"ClipboardHistoryWindow: Win32 テキスト入力座標の取得に失敗しました。GetWindowThreadProcessId が失敗しました。TargetWindow={FormatHandle(targetWindow)} Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
			return false;
		}

		var guiThreadInfo = new NativeMethods.GuiThreadInfo
		{
			CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
		};
		if (!NativeMethods.GetGUIThreadInfo(threadId, ref guiThreadInfo))
		{
			Logger.Debug($"ClipboardHistoryWindow: Win32 テキスト入力座標の取得に失敗しました。GetGUIThreadInfo が失敗しました。TargetWindow={FormatHandle(targetWindow)} ThreadId={threadId} Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
			return false;
		}

		Logger.Debug($"ClipboardHistoryWindow: GUI thread 情報を取得しました。TargetWindow={FormatHandle(targetWindow)} ThreadId={threadId} Active={FormatHandle(guiThreadInfo.HWndActive)} Focus={FormatHandle(guiThreadInfo.HWndFocus)} Caret={FormatHandle(guiThreadInfo.HWndCaret)} CaretRect=({guiThreadInfo.RcCaret.Left},{guiThreadInfo.RcCaret.Top})-({guiThreadInfo.RcCaret.Right},{guiThreadInfo.RcCaret.Bottom})");
		if (guiThreadInfo.HWndCaret == IntPtr.Zero)
		{
			Logger.Debug($"ClipboardHistoryWindow: Win32 テキスト入力座標の取得に失敗しました。キャレット HWND が 0 です。TargetWindow={FormatHandle(targetWindow)} ThreadId={threadId} Focus={FormatHandle(guiThreadInfo.HWndFocus)}");
			return false;
		}

		var topLeft = new NativeMethods.NativePoint
		{
			X = guiThreadInfo.RcCaret.Left,
			Y = guiThreadInfo.RcCaret.Top
		};
		var bottomRight = new NativeMethods.NativePoint
		{
			X = guiThreadInfo.RcCaret.Right,
			Y = guiThreadInfo.RcCaret.Bottom
		};

		if (!NativeMethods.ClientToScreen(guiThreadInfo.HWndCaret, ref topLeft))
		{
			Logger.Debug($"ClipboardHistoryWindow: Win32 テキスト入力座標の取得に失敗しました。ClientToScreen(topLeft) が失敗しました。Caret={FormatHandle(guiThreadInfo.HWndCaret)} Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
			return false;
		}

		if (!NativeMethods.ClientToScreen(guiThreadInfo.HWndCaret, ref bottomRight))
		{
			Logger.Debug($"ClipboardHistoryWindow: Win32 テキスト入力座標の取得に失敗しました。ClientToScreen(bottomRight) が失敗しました。Caret={FormatHandle(guiThreadInfo.HWndCaret)} Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
			return false;
		}

		double width = Math.Max(1, bottomRight.X - topLeft.X);
		double height = Math.Max(1, bottomRight.Y - topLeft.Y);
		bounds = new Rect(topLeft.X, topLeft.Y, width, height);
		return true;
	}

	private static bool TryGetAutomationTextInputBounds(out Rect bounds)
	{
		bounds = Rect.Empty;
		try
		{
			AutomationElement focusedElement = AutomationElement.FocusedElement;
			if (focusedElement == null)
			{
				Logger.Debug("ClipboardHistoryWindow: UI Automation テキスト入力座標の取得に失敗しました。FocusedElement が null です。");
				return false;
			}

			Logger.Debug($"ClipboardHistoryWindow: UI Automation focused element を取得しました。{FormatAutomationElement(focusedElement)}");
			if (TryGetAutomationTextPatternBounds(focusedElement, out bounds))
			{
				return true;
			}

			if (TryGetAutomationElementBounds(focusedElement, out bounds))
			{
				return true;
			}

			Logger.Debug("ClipboardHistoryWindow: UI Automation テキスト入力座標の取得に失敗しました。TextPattern または focused element の有効な矩形を取得できませんでした。");
			return false;
		}
		catch (Exception ex) when (ex is ElementNotAvailableException ||
			ex is InvalidOperationException ||
			ex is System.Runtime.InteropServices.COMException)
		{
			Logger.Error(ex, "ClipboardHistoryWindow: UI Automation テキスト入力座標の取得中に例外が発生しました。");
			return false;
		}
	}

	private static bool TryGetAutomationTextPatternBounds(AutomationElement element, out Rect bounds)
	{
		bounds = Rect.Empty;
		if (!element.TryGetCurrentPattern(TextPattern.Pattern, out object patternObject))
		{
			Logger.Debug("ClipboardHistoryWindow: UI Automation TextPattern は利用できません。");
			return false;
		}

		var textPattern = (TextPattern)patternObject;
		TextPatternRange[] selectionRanges = textPattern.GetSelection();
		Logger.Debug($"ClipboardHistoryWindow: UI Automation TextPattern.GetSelection を取得しました。Count={selectionRanges.Length}");
		foreach (TextPatternRange range in selectionRanges)
		{
			if (TryGetTextPatternRangeBounds(range, out bounds))
			{
				Logger.Debug($"ClipboardHistoryWindow: UI Automation TextPattern の選択範囲矩形を取得しました。Bounds={bounds}");
				return true;
			}
		}

		return false;
	}

	private static bool TryGetAutomationElementBounds(AutomationElement element, out Rect bounds)
	{
		Rect rectangle = element.Current.BoundingRectangle;
		if (IsUsableRect(rectangle))
		{
			bounds = rectangle;
			Logger.Debug($"ClipboardHistoryWindow: UI Automation focused element の矩形を取得しました。Bounds={bounds}");
			return true;
		}

		bounds = Rect.Empty;
		Logger.Debug($"ClipboardHistoryWindow: UI Automation focused element の矩形が無効です。Bounds={rectangle}");
		return false;
	}

	private static bool TryGetTextPatternRangeBounds(TextPatternRange range, out Rect bounds)
	{
		if (TryGetBoundingRectangle(range, out bounds))
		{
			return true;
		}

		foreach (TextUnit textUnit in new[] { TextUnit.Character, TextUnit.Word, TextUnit.Line })
		{
			TextPatternRange expandedRange = range.Clone();
			expandedRange.ExpandToEnclosingUnit(textUnit);
			if (TryGetBoundingRectangle(expandedRange, out bounds))
			{
				Logger.Debug($"ClipboardHistoryWindow: UI Automation range を {textUnit} に拡張して矩形を取得しました。Bounds={bounds}");
				return true;
			}
		}

		return false;
	}

	private static bool TryGetBoundingRectangle(TextPatternRange range, out Rect bounds)
	{
		bounds = Rect.Empty;
		foreach (Rect rectangle in range.GetBoundingRectangles())
		{
			if (IsUsableRect(rectangle))
			{
				bounds = rectangle;
				return true;
			}
		}

		return false;
	}

	private static bool IsUsableRect(Rect rectangle)
	{
		return !rectangle.IsEmpty &&
			double.IsFinite(rectangle.Left) &&
			double.IsFinite(rectangle.Top) &&
			double.IsFinite(rectangle.Width) &&
			double.IsFinite(rectangle.Height) &&
			rectangle.Width >= 0 &&
			rectangle.Height >= 0 &&
			(rectangle.Width > 0 || rectangle.Height > 0);
	}

	private static Point GetCursorPoint()
	{
		if (NativeMethods.GetCursorPos(out var point))
		{
			return new Point(point.X, point.Y);
		}

		return new Point(0, 0);
	}

	private static Rect GetWorkingArea(Point point)
	{
		var nativePoint = new NativeMethods.NativePoint
		{
			X = (int)Math.Round(point.X),
			Y = (int)Math.Round(point.Y)
		};
		IntPtr monitor = NativeMethods.MonitorFromPoint(nativePoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
		if (monitor != IntPtr.Zero)
		{
			var monitorInfo = new NativeMethods.MonitorInfo
			{
				CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
			};
			if (NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
			{
				return ToRect(monitorInfo.RcWork);
			}
		}

		return SystemParameters.WorkArea;
	}

	private static Rect ToRect(NativeMethods.NativeRect rect)
	{
		return new Rect(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
	}

	private static string FormatAutomationElement(AutomationElement element)
	{
		try
		{
			AutomationElement.AutomationElementInformation current = element.Current;
			return $"Name=\"{current.Name}\" ControlType={current.ControlType?.ProgrammaticName ?? string.Empty} NativeWindowHandle=0x{current.NativeWindowHandle:X} AutomationId=\"{current.AutomationId}\" ClassName=\"{current.ClassName}\"";
		}
		catch (Exception ex) when (ex is ElementNotAvailableException ||
			ex is InvalidOperationException ||
			ex is System.Runtime.InteropServices.COMException)
		{
			return $"取得失敗: {ex.Message}";
		}
	}

	private static string FormatHandle(IntPtr handle)
	{
		return $"0x{handle.ToInt64():X}";
	}

	private void StopWindowFlash()
	{
		IntPtr handle = new WindowInteropHelper(this).Handle;
		if (handle == IntPtr.Zero)
		{
			return;
		}

		var flashWindowInfo = new NativeMethods.FlashWindowInfo
		{
			CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FlashWindowInfo>(),
			HWnd = handle,
			DwFlags = NativeMethods.FLASHW_STOP
		};
		NativeMethods.FlashWindowEx(ref flashWindowInfo);
	}

	private static List<ClipboardHistoryEntry> LoadHistoryEntries(CancellationToken cancellationToken)
	{
		try
		{
			if (!Directory.Exists(ClipboardSettings.BaseDirectoryPath))
			{
				return new List<ClipboardHistoryEntry>();
			}

			var candidates = LoadHistoryCandidates(cancellationToken);

			var entries = new List<ClipboardHistoryEntry>(candidates.Count);
			foreach (var candidate in candidates)
			{
				cancellationToken.ThrowIfCancellationRequested();
				entries.Add(CreateHistoryEntry(candidate));
			}

			return entries;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardHistoryWindow: 履歴の読み込みに失敗しました。");
			return new List<ClipboardHistoryEntry>();
		}
	}

	private static List<ClipboardHistoryCandidate> LoadHistoryCandidates(CancellationToken cancellationToken)
	{
		var baseDirectory = new DirectoryInfo(ClipboardSettings.BaseDirectoryPath);
		var candidates = new List<ClipboardHistoryCandidate>(MaxHistoryItems);

		AddDirectoryCandidates(baseDirectory, SearchOption.TopDirectoryOnly, candidates, cancellationToken);

		var datedDirectories = baseDirectory
			.EnumerateDirectories()
			.Select(directory => new
			{
				Directory = directory,
				Date = TryGetHistoryDirectoryDate(directory.Name, out var date) ? date : (DateTime?)null
			})
			.Where(item => item.Date.HasValue)
			.OrderByDescending(item => item.Date.GetValueOrDefault())
			.ToList();

		var datedDirectoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in datedDirectories)
		{
			cancellationToken.ThrowIfCancellationRequested();
			datedDirectoryPaths.Add(item.Directory.FullName);
			AddDirectoryCandidates(item.Directory, SearchOption.TopDirectoryOnly, candidates, cancellationToken);
			if (candidates.Count >= MaxHistoryItems)
			{
				break;
			}
		}

		if (candidates.Count < MaxHistoryItems)
		{
			var fallbackDirectories = baseDirectory
				.EnumerateDirectories()
				.Where(directory => !datedDirectoryPaths.Contains(directory.FullName))
				.OrderByDescending(directory => directory.LastWriteTime);

			foreach (var directory in fallbackDirectories)
			{
				cancellationToken.ThrowIfCancellationRequested();
				AddDirectoryCandidates(directory, SearchOption.AllDirectories, candidates, cancellationToken);
				if (candidates.Count >= MaxHistoryItems)
				{
					break;
				}
			}
		}

		return candidates
			.OrderByDescending(candidate => candidate.LastWriteTime)
			.ThenByDescending(candidate => candidate.FilePath)
			.Take(MaxHistoryItems)
			.ToList();
	}

	private static void AddDirectoryCandidates(
		DirectoryInfo directory,
		SearchOption searchOption,
		List<ClipboardHistoryCandidate> candidates,
		CancellationToken cancellationToken)
	{
		foreach (var fileInfo in directory.EnumerateFiles("*.*", searchOption))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var candidate = CreateHistoryCandidate(fileInfo, cancellationToken);
			if (candidate != null)
			{
				candidates.Add(candidate);
			}
		}
	}

	private static bool TryGetHistoryDirectoryDate(string directoryName, out DateTime date)
	{
		return DateTime.TryParseExact(
			directoryName,
			"yyyyMMdd",
			CultureInfo.InvariantCulture,
			DateTimeStyles.None,
			out date);
	}

	private static ClipboardHistoryEntry CreateHistoryEntry(ClipboardHistoryCandidate candidate)
	{
		string previewText;
		ImageSource? thumbnail = null;

		if (candidate.Kind == ClipboardHistoryKind.Image)
		{
			(previewText, thumbnail) = CreateImagePreview(candidate.FilePath);
		}
		else
		{
			previewText = CreatePreviewText(candidate.FilePath, candidate.Kind);
		}

		return new ClipboardHistoryEntry
		{
			FilePath = candidate.FilePath,
			Kind = candidate.Kind,
			LastWriteTime = candidate.LastWriteTime,
			PreviewText = previewText,
			Thumbnail = thumbnail
		};
	}

	private static ClipboardHistoryCandidate? CreateHistoryCandidate(FileInfo fileInfo, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		string extension = fileInfo.Extension.ToLowerInvariant();
		ClipboardHistoryKind kind = extension switch
		{
			".png" => ClipboardHistoryKind.Image,
			".html" => ClipboardHistoryKind.Html,
			".rtf" => ClipboardHistoryKind.Rtf,
			".txt" => ClipboardHistoryKind.Text,
			_ => ClipboardHistoryKind.Unknown
		};

		if (kind == ClipboardHistoryKind.Unknown)
		{
			return null;
		}

		return new ClipboardHistoryCandidate
		{
			FilePath = fileInfo.FullName,
			Kind = kind,
			LastWriteTime = fileInfo.LastWriteTime
		};
	}

	private static string CreatePreviewText(string filePath, ClipboardHistoryKind kind)
	{
		try
		{
			string text = ReadTextStart(filePath, 4096);
			if (kind == ClipboardHistoryKind.Html)
			{
				text = ConvertHtmlToPlainText(text);
			}
			else if (kind == ClipboardHistoryKind.Rtf)
			{
				text = ConvertRtfToPlainText(text);
			}

			text = NormalizePreviewText(text);
			if (text.Length > PreviewTextLength)
			{
				text = text[..PreviewTextLength] + "...";
			}

			return string.IsNullOrWhiteSpace(text) ? Path.GetFileName(filePath) : text;
		}
		catch
		{
			return Path.GetFileName(filePath);
		}
	}

	private static string ReadTextStart(string filePath, int maxChars)
	{
		using var reader = new StreamReader(filePath, Encoding.UTF8, true);
		char[] buffer = new char[maxChars];
		int read = reader.ReadBlock(buffer, 0, buffer.Length);
		return new string(buffer, 0, read);
	}

	private static string NormalizePreviewText(string text)
	{
		return Regex.Replace(text, @"\s+", " ").Trim();
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
		return WebUtility.HtmlDecode(noTags);
	}

	private static string ConvertRtfToPlainText(string rtf)
	{
		string text = Regex.Replace(rtf, @"\\'[0-9a-fA-F]{2}", " ");
		text = Regex.Replace(text, @"\\[a-zA-Z]+\d* ?", " ");
		text = text.Replace(@"\par", " ");
		text = text.Replace(@"\tab", " ");
		text = Regex.Replace(text, @"[{}]", " ");
		return text;
	}

	private static (string PreviewText, ImageSource? Thumbnail) CreateImagePreview(string filePath)
	{
		try
		{
			using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
			BitmapFrame frame = decoder.Frames[0];
			ImageSource thumbnail = CreateThumbnail(filePath);
			return ($"{Path.GetFileName(filePath)} / {frame.PixelWidth} x {frame.PixelHeight}", thumbnail);
		}
		catch
		{
			return (Path.GetFileName(filePath), null);
		}
	}

	private static ImageSource CreateThumbnail(string filePath)
	{
		var thumbnail = new BitmapImage();
		thumbnail.BeginInit();
		thumbnail.CacheOption = BitmapCacheOption.OnLoad;
		thumbnail.DecodePixelWidth = 86;
		thumbnail.UriSource = new Uri(filePath);
		thumbnail.EndInit();
		thumbnail.Freeze();
		return thumbnail;
	}

	private static BitmapFrame? LoadIcon()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "Clipboard.ico");
		if (!File.Exists(path))
		{
			path = Path.Combine(Directory.GetCurrentDirectory(), "Clipboard.ico");
		}

		return File.Exists(path) ? BitmapFrame.Create(new Uri(path)) : null;
	}

	private sealed class ClipboardHistoryCandidate
	{
		public required string FilePath { get; init; }
		public required ClipboardHistoryKind Kind { get; init; }
		public required DateTime LastWriteTime { get; init; }
	}

	private sealed class ClipboardHistoryEntry
	{
		public required string FilePath { get; init; }
		public required ClipboardHistoryKind Kind { get; init; }
		public required DateTime LastWriteTime { get; init; }
		public required string PreviewText { get; init; }
		public ImageSource? Thumbnail { get; init; }
	}

	private enum ClipboardHistoryKind
	{
		Image,
		Html,
		Rtf,
		Text,
		Unknown
	}

	private sealed class HistoryItemControl : Border
	{
		private static readonly Brush NormalBackground = Brushes.White;
		private static readonly Brush HoverBackground = new SolidColorBrush(Color.FromRgb(232, 240, 254));

		public event EventHandler? Activated;

		public HistoryItemControl(ClipboardHistoryEntry entry)
		{
			Margin = new Thickness(0, 0, 0, 8);
			Padding = new Thickness(10);
			Background = NormalBackground;
			BorderBrush = new SolidColorBrush(Color.FromRgb(216, 220, 226));
			BorderThickness = new Thickness(1);
			Cursor = Cursors.Hand;
			Height = entry.Kind == ClipboardHistoryKind.Image ? 92 : 78;
			HorizontalAlignment = HorizontalAlignment.Stretch;
			Child = CreateContent(entry);

			MouseEnter += (_, _) => Background = HoverBackground;
			MouseLeave += (_, _) => Background = NormalBackground;
			MouseLeftButtonUp += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
		}

		private static Grid CreateContent(ClipboardHistoryEntry entry)
		{
			var grid = new Grid();
			if (entry.Kind == ClipboardHistoryKind.Image)
			{
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

				var thumbnailBox = new Border
				{
					Width = 86,
					Height = 66,
					Background = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
					Child = entry.Thumbnail == null ? null : new Image
					{
						Source = entry.Thumbnail,
						Stretch = Stretch.Uniform
					}
				};
				Grid.SetColumn(thumbnailBox, 0);
				grid.Children.Add(thumbnailBox);
			}
			else
			{
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			}

			var textGrid = new Grid();
			textGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
			textGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			var headerGrid = new Grid();
			headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
			headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

			var kindLabel = new TextBlock
			{
				Text = GetKindText(entry.Kind),
				FontWeight = FontWeights.Bold,
				Foreground = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
				VerticalAlignment = VerticalAlignment.Center
			};
			headerGrid.Children.Add(kindLabel);

			var dateLabel = new TextBlock
			{
				Text = entry.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss"),
				Foreground = new SolidColorBrush(Color.FromRgb(112, 112, 112)),
				TextAlignment = TextAlignment.Right,
				VerticalAlignment = VerticalAlignment.Center,
				TextTrimming = TextTrimming.CharacterEllipsis
			};
			Grid.SetColumn(dateLabel, 1);
			headerGrid.Children.Add(dateLabel);

			Grid.SetRow(headerGrid, 0);
			textGrid.Children.Add(headerGrid);

			var previewLabel = new TextBlock
			{
				Text = entry.PreviewText,
				Foreground = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
				TextWrapping = TextWrapping.Wrap,
				TextTrimming = TextTrimming.CharacterEllipsis
			};
			Grid.SetRow(previewLabel, 1);
			textGrid.Children.Add(previewLabel);

			Grid.SetColumn(textGrid, entry.Kind == ClipboardHistoryKind.Image ? 1 : 0);
			grid.Children.Add(textGrid);
			return grid;
		}

		private static string GetKindText(ClipboardHistoryKind kind)
		{
			return kind switch
			{
				ClipboardHistoryKind.Image => "画像",
				ClipboardHistoryKind.Html => "HTML",
				ClipboardHistoryKind.Rtf => "RTF",
				_ => "文字列"
			};
		}
	}
}
