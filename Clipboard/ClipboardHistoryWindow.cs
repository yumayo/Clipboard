using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
	private const int SearchFilterDelayMilliseconds = 150;
	private const int SearchResultBatchSize = 16;
	private readonly TextBox _searchBox;
	private readonly ScrollViewer _scrollViewer;
	private readonly StackPanel _listPanel;
	private readonly List<ClipboardHistoryEntry> _historyEntries = new();
	private readonly NativeMethods.WinEventProc _externalFocusChangedProc;
	private CancellationTokenSource? _loadHistoryCancellation;
	private CancellationTokenSource? _filterHistoryCancellation;
	private IntPtr _foregroundWindowChangedHook;
	private IntPtr _focusChangedHook;
	private IntPtr _targetWindow;
	private IntPtr _windowHandle;
	private int _selectedItemIndex = -1;
	private bool _isLoadingHistory;
	private bool _hasLoadedHistory;
	private bool _allowClose;

	public bool IsClosed { get; private set; }

	public ClipboardHistoryWindow()
	{
		_externalFocusChangedProc = OnExternalFocusChanged;
		Title = "クリップボード履歴";
		WindowStartupLocation = WindowStartupLocation.Manual;
		Width = 520;
		Height = 640;
		MinWidth = 380;
		MinHeight = 320;
		ShowInTaskbar = false;
		ShowActivated = false;
		Focusable = false;
		Icon = LoadIcon();
		SourceInitialized += (_, _) =>
		{
			_windowHandle = new WindowInteropHelper(this).Handle;
			HideMinimizeAndMaximizeButtons();
		};

		var rootPanel = new DockPanel
		{
			Background = new SolidColorBrush(Color.FromRgb(245, 246, 248))
		};

		var searchPanel = new Grid
		{
			Margin = new Thickness(10, 10, 10, 0)
		};
		searchPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		searchPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

		searchPanel.Children.Add(new TextBlock
		{
			Text = "検索",
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 8, 0),
			Foreground = new SolidColorBrush(Color.FromRgb(64, 64, 64)),
			FontWeight = FontWeights.Bold
		});

		_searchBox = new TextBox
		{
			MinHeight = 32,
			Padding = new Thickness(8, 5, 8, 5),
			VerticalContentAlignment = VerticalAlignment.Center,
			BorderBrush = new SolidColorBrush(Color.FromRgb(188, 194, 204)),
			Background = Brushes.White,
			ToolTip = "履歴を検索"
		};
		_searchBox.TextChanged += (_, _) =>
		{
			if (_hasLoadedHistory || _historyEntries.Count > 0)
			{
				BeginApplyHistoryFilter(delay: true);
			}
		};
		_searchBox.LostKeyboardFocus += (_, _) => ClearSearchInputState();
		Grid.SetColumn(_searchBox, 1);
		searchPanel.Children.Add(_searchBox);

		DockPanel.SetDock(searchPanel, Dock.Top);
		rootPanel.Children.Add(searchPanel);

		_listPanel = new StackPanel
		{
			Margin = new Thickness(10)
		};

		_scrollViewer = new ScrollViewer
		{
			Content = _listPanel,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Background = new SolidColorBrush(Color.FromRgb(245, 246, 248))
		};
		rootPanel.Children.Add(_scrollViewer);
		Content = rootPanel;
		IsVisibleChanged += (_, _) =>
		{
			if (!IsVisible)
			{
				StopDismissOnExternalFocus();
				CancelHistoryLoad();
				CancelHistoryFilter();
			}

			VisibleStateChanged?.Invoke(IsVisible);
		};
	}

	public event Action<bool>? VisibleStateChanged;

	public bool IsForegroundWindow()
	{
		return _windowHandle != IntPtr.Zero && NativeMethods.GetForegroundWindow() == _windowHandle;
	}

	public void ShowHistory(IntPtr targetWindow)
	{
		_targetWindow = targetWindow;
		MoveNearTextInput(targetWindow);
		if (!IsVisible && _searchBox.Text.Length > 0)
		{
			_searchBox.Clear();
		}

		Topmost = true;
		Show();
		SelectFirstHistoryItem();
		Topmost = false;
		StopWindowFlash();
		Dispatcher.BeginInvoke((Action)StopWindowFlash);
		BeginDismissOnExternalFocus();

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

	public bool MoveSelectionFromKeyboard(int offset)
	{
		return IsVisible && MoveSelection(offset);
	}

	public bool ActivateSelectedItemFromKeyboard()
	{
		return IsVisible && ActivateSelectedItem();
	}

	public void HideFromKeyboard()
	{
		if (IsVisible)
		{
			Hide();
		}
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (_searchBox.IsKeyboardFocusWithin)
		{
			if (e.Key == Key.Down && FocusSelectedHistoryItem())
			{
				e.Handled = true;
				return;
			}

			base.OnPreviewKeyDown(e);
			return;
		}

		if (e.Key == Key.Down)
		{
			if (MoveSelection(1))
			{
				e.Handled = true;
				return;
			}
		}
		else if (e.Key == Key.Up)
		{
			if (MoveSelection(-1))
			{
				e.Handled = true;
				return;
			}
		}
		else if (e.Key == Key.Return)
		{
			if (ActivateSelectedItem())
			{
				e.Handled = true;
				return;
			}
		}

		base.OnPreviewKeyDown(e);
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

		StopDismissOnExternalFocus();
		CancelHistoryLoad();
		CancelHistoryFilter();
		IsClosed = true;
		base.OnClosing(e);
	}

	private void BeginDismissOnExternalFocus()
	{
		StopDismissOnExternalFocus();
		_foregroundWindowChangedHook = SetDismissWinEventHook(
			NativeMethods.EVENT_SYSTEM_FOREGROUND,
			nameof(NativeMethods.EVENT_SYSTEM_FOREGROUND));
		_focusChangedHook = SetDismissWinEventHook(
			NativeMethods.EVENT_OBJECT_FOCUS,
			nameof(NativeMethods.EVENT_OBJECT_FOCUS));
	}

	private IntPtr SetDismissWinEventHook(uint eventType, string eventName)
	{
		IntPtr hook = NativeMethods.SetWinEventHook(
			eventType,
			eventType,
			IntPtr.Zero,
			_externalFocusChangedProc,
			0,
			0,
			NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
		if (hook == IntPtr.Zero)
		{
			Logger.Warning($"ClipboardHistoryWindow: 外部フォーカス監視の開始に失敗しました。Event={eventName} Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
		}

		return hook;
	}

	private void StopDismissOnExternalFocus()
	{
		UnhookDismissWinEvent(ref _foregroundWindowChangedHook, nameof(NativeMethods.EVENT_SYSTEM_FOREGROUND));
		UnhookDismissWinEvent(ref _focusChangedHook, nameof(NativeMethods.EVENT_OBJECT_FOCUS));
	}

	private static void UnhookDismissWinEvent(ref IntPtr hook, string eventName)
	{
		if (hook == IntPtr.Zero)
		{
			return;
		}

		if (!NativeMethods.UnhookWinEvent(hook))
		{
			Logger.Warning($"ClipboardHistoryWindow: 外部フォーカス監視の解除に失敗しました。Event={eventName} Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
		}

		hook = IntPtr.Zero;
	}

	private void OnExternalFocusChanged(
		IntPtr hWinEventHook,
		uint eventType,
		IntPtr hwnd,
		int idObject,
		int idChild,
		uint dwEventThread,
		uint dwmsEventTime)
	{
		if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
		{
			return;
		}

		Dispatcher.BeginInvoke((Action)(() => HideIfExternalFocusMoved(eventType, hwnd)));
	}

	private void HideIfExternalFocusMoved(uint eventType, IntPtr eventWindow)
	{
		if (!IsVisible || IsClosed)
		{
			return;
		}

		IntPtr windowHandle = GetWindowHandle();
		if (IsSameRootWindow(eventWindow, windowHandle))
		{
			return;
		}

		IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
		if (eventType == NativeMethods.EVENT_OBJECT_FOCUS &&
			eventWindow != IntPtr.Zero &&
			foregroundWindow != IntPtr.Zero &&
			!IsSameRootWindow(eventWindow, foregroundWindow))
		{
			return;
		}

		if (IsSameRootWindow(foregroundWindow, windowHandle))
		{
			return;
		}

		Logger.Debug($"ClipboardHistoryWindow: 外部へフォーカスが移動したため履歴画面を閉じます。Event=0x{eventType:X} EventWindow={FormatHandle(eventWindow)} ForegroundWindow={FormatHandle(foregroundWindow)}");
		Hide();
	}

	private IntPtr GetWindowHandle()
	{
		if (_windowHandle == IntPtr.Zero)
		{
			_windowHandle = new WindowInteropHelper(this).EnsureHandle();
		}

		return _windowHandle;
	}

	private static bool IsSameRootWindow(IntPtr window, IntPtr rootWindow)
	{
		if (window == IntPtr.Zero || rootWindow == IntPtr.Zero)
		{
			return false;
		}

		if (window == rootWindow)
		{
			return true;
		}

		IntPtr root = NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT);
		return root == rootWindow;
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
		_historyEntries.Clear();
		_historyEntries.AddRange(entries);
		BeginApplyHistoryFilter(delay: false);
	}

	private void BeginApplyHistoryFilter(bool delay)
	{
		CancelHistoryFilter();
		var cancellationTokenSource = new CancellationTokenSource();
		_filterHistoryCancellation = cancellationTokenSource;

		string searchText = _searchBox.Text;
		var entriesSnapshot = _historyEntries.ToList();
		_ = ApplyHistoryFilterAsync(entriesSnapshot, searchText, delay, cancellationTokenSource);
	}

	private async Task ApplyHistoryFilterAsync(
		List<ClipboardHistoryEntry> entriesSnapshot,
		string searchText,
		bool delay,
		CancellationTokenSource cancellationTokenSource)
	{
		try
		{
			CancellationToken cancellationToken = cancellationTokenSource.Token;
			if (delay)
			{
				await Task.Delay(SearchFilterDelayMilliseconds, cancellationToken);
			}

			await RunCurrentFilterOnUiAsync(
				cancellationTokenSource,
				() => BeginHistoryFilterDisplay(entriesSnapshot.Count));

			if (entriesSnapshot.Count == 0)
			{
				return;
			}

			bool hasSearchText = !string.IsNullOrWhiteSpace(searchText);
			List<ClipboardHistoryEntry> entriesToDisplay = hasSearchText
				? await Task.Run(() => LoadHistoryEntries(cancellationToken, searchText), cancellationToken)
				: entriesSnapshot;

			int matchedEntryCount = await Task.Run(
				() => DisplayHistoryEntriesProgressivelyAsync(entriesToDisplay, cancellationTokenSource),
				cancellationToken);

			if (cancellationToken.IsCancellationRequested || _filterHistoryCancellation != cancellationTokenSource)
			{
				return;
			}

			await RunCurrentFilterOnUiAsync(
				cancellationTokenSource,
				() => CompleteHistoryFilterDisplay(entriesSnapshot.Count, matchedEntryCount));
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardHistoryWindow: 履歴の検索に失敗しました。");
			if (_filterHistoryCancellation == cancellationTokenSource)
			{
				ClearHistoryControls();
				AddMessage("検索に失敗しました");
			}
		}
		finally
		{
			if (_filterHistoryCancellation == cancellationTokenSource)
			{
				_filterHistoryCancellation = null;
			}
		}
	}

	private Task RunCurrentFilterOnUiAsync(CancellationTokenSource cancellationTokenSource, Action action)
	{
		return Dispatcher.InvokeAsync(() =>
		{
			if (cancellationTokenSource.IsCancellationRequested || _filterHistoryCancellation != cancellationTokenSource)
			{
				return;
			}

			action();
		}).Task;
	}

	private void BeginHistoryFilterDisplay(int totalEntryCount)
	{
		ClearHistoryControls();
		if (totalEntryCount == 0)
		{
			AddMessage("履歴がありません");
		}
	}

	private void AddMatchedHistoryEntries(List<ClipboardHistoryEntry> entries)
	{
		bool shouldSelectFirstItem = _selectedItemIndex < 0 && !_listPanel.Children.OfType<HistoryItemControl>().Any();
		foreach (var entry in entries)
		{
			var item = new HistoryItemControl(entry);
			item.Activated += (_, _) => PasteEntry(entry);
			_listPanel.Children.Add(item);
		}

		if (shouldSelectFirstItem)
		{
			SelectFirstHistoryItem();
		}
	}

	private void CompleteHistoryFilterDisplay(int totalEntryCount, int matchedEntryCount)
	{
		if (totalEntryCount == 0)
		{
			return;
		}

		if (matchedEntryCount == 0)
		{
			AddMessage("一致する履歴がありません");
		}
		else if (_selectedItemIndex < 0)
		{
			SelectFirstHistoryItem();
		}
	}

	private async Task<int> DisplayHistoryEntriesProgressivelyAsync(
		List<ClipboardHistoryEntry> entries,
		CancellationTokenSource cancellationTokenSource)
	{
		CancellationToken cancellationToken = cancellationTokenSource.Token;
		cancellationToken.ThrowIfCancellationRequested();
		var matchedEntries = new List<ClipboardHistoryEntry>(SearchResultBatchSize);
		int matchedEntryCount = 0;
		foreach (var entry in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			matchedEntries.Add(entry);
			matchedEntryCount++;
			if (matchedEntryCount == 1 || matchedEntries.Count >= SearchResultBatchSize)
			{
				await FlushMatchedHistoryEntriesAsync(matchedEntries, cancellationTokenSource);
			}
		}

		await FlushMatchedHistoryEntriesAsync(matchedEntries, cancellationTokenSource);
		return matchedEntryCount;
	}

	private async Task FlushMatchedHistoryEntriesAsync(
		List<ClipboardHistoryEntry> matchedEntries,
		CancellationTokenSource cancellationTokenSource)
	{
		if (matchedEntries.Count == 0)
		{
			return;
		}

		var entriesToAdd = matchedEntries.ToList();
		matchedEntries.Clear();
		await RunCurrentFilterOnUiAsync(
			cancellationTokenSource,
			() => AddMatchedHistoryEntries(entriesToAdd));
		cancellationTokenSource.Token.ThrowIfCancellationRequested();
	}

	private void PasteEntry(ClipboardHistoryEntry entry)
	{
		Hide();
		ClipboardManager.PasteHistoryEntry(entry.Id, _targetWindow);
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
		_selectedItemIndex = -1;
		_scrollViewer.ScrollToTop();
	}

	private bool SelectFirstHistoryItem()
	{
		return SelectHistoryItem(0);
	}

	private bool MoveSelection(int offset)
	{
		var items = GetHistoryItems();
		if (items.Count == 0)
		{
			_selectedItemIndex = -1;
			return false;
		}

		if (offset < 0 && _selectedItemIndex <= 0)
		{
			return FocusSearchBox();
		}

		int nextIndex = _selectedItemIndex < 0 ? 0 : _selectedItemIndex + offset;
		nextIndex = Math.Max(0, Math.Min(nextIndex, items.Count - 1));
		return SelectHistoryItem(nextIndex, items);
	}

	private bool FocusSearchBox()
	{
		Activate();
		_searchBox.Focus();
		Keyboard.Focus(_searchBox);
		_searchBox.CaretIndex = _searchBox.Text.Length;
		return _searchBox.IsKeyboardFocusWithin;
	}

	private void ClearSearchInputState()
	{
		_searchBox.SelectionLength = 0;
		_searchBox.CaretIndex = Math.Min(_searchBox.CaretIndex, _searchBox.Text.Length);

		try
		{
			InputMethod.SetIsInputMethodEnabled(_searchBox, false);
			InputMethod.SetIsInputMethodEnabled(_searchBox, true);
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException)
		{
			Logger.Debug($"ClipboardHistoryWindow: 検索欄の入力状態解除に失敗しました。{ex.Message}");
		}
	}

	private bool FocusSelectedHistoryItem()
	{
		var items = GetHistoryItems();
		if (items.Count == 0)
		{
			_selectedItemIndex = -1;
			return false;
		}

		int index = _selectedItemIndex < 0 ? 0 : Math.Min(_selectedItemIndex, items.Count - 1);
		return SelectHistoryItem(index, items, focusItem: true);
	}

	private bool ActivateSelectedItem()
	{
		var items = GetHistoryItems();
		if (_selectedItemIndex < 0 || _selectedItemIndex >= items.Count)
		{
			return false;
		}

		items[_selectedItemIndex].Activate();
		return true;
	}

	private bool SelectHistoryItem(int index)
	{
		return SelectHistoryItem(index, GetHistoryItems());
	}

	private bool SelectHistoryItem(int index, List<HistoryItemControl> items, bool focusItem = false)
	{
		if (items.Count == 0)
		{
			_selectedItemIndex = -1;
			return false;
		}

		index = Math.Max(0, Math.Min(index, items.Count - 1));
		for (int i = 0; i < items.Count; i++)
		{
			items[i].IsSelected = i == index;
		}

		_selectedItemIndex = index;
		items[index].BringIntoView();
		if (focusItem)
		{
			items[index].Focus();
			Keyboard.Focus(items[index]);
		}

		return true;
	}

	private List<HistoryItemControl> GetHistoryItems()
	{
		return _listPanel.Children.OfType<HistoryItemControl>().ToList();
	}

	private void CancelHistoryLoad()
	{
		if (_loadHistoryCancellation is { IsCancellationRequested: false })
		{
			_loadHistoryCancellation.Cancel();
		}
	}

	private void CancelHistoryFilter()
	{
		if (_filterHistoryCancellation is { IsCancellationRequested: false })
		{
			_filterHistoryCancellation.Cancel();
		}
	}

	private void MoveNearTextInput(IntPtr targetWindow)
	{
		bool hasTextInputBounds = TryGetTextInputBounds(targetWindow, out Rect textInputBoundsDevice, out string textInputSource);
		Rect anchorBoundsDevice;
		if (hasTextInputBounds)
		{
			anchorBoundsDevice = textInputBoundsDevice;
			Logger.Debug($"ClipboardHistoryWindow: テキスト入力座標を取得しました。Source={textInputSource} TargetWindow={FormatHandle(targetWindow)} BoundsDevice={anchorBoundsDevice}");
		}
		else
		{
			Point cursorPoint = GetCursorPoint();
			anchorBoundsDevice = new Rect(cursorPoint, new Size(0, 0));
			Logger.Debug($"ClipboardHistoryWindow: テキスト入力座標を取得できなかったためマウス座標にフォールバックします。TargetWindow={FormatHandle(targetWindow)} Cursor={cursorPoint}");
		}

		Matrix transformFromDevice = GetTransformFromDevice();
		Rect anchorBounds = TransformRect(anchorBoundsDevice, transformFromDevice);
		Rect workingArea = TryGetWorkingAreaDevice(anchorBoundsDevice.Location, out Rect workingAreaDevice)
			? TransformRect(workingAreaDevice, transformFromDevice)
			: SystemParameters.WorkArea;
		double windowWidth = ActualWidth > 0 ? ActualWidth : Width;
		double windowHeight = ActualHeight > 0 ? ActualHeight : Height;
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
		Logger.Debug($"ClipboardHistoryWindow: 履歴画面の表示位置を決定しました。AnchorDevice={anchorBoundsDevice} Anchor={anchorBounds} WorkingArea={workingArea} Location=({Left},{Top}) Size=({windowWidth},{windowHeight})");
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

	private Matrix GetTransformFromDevice()
	{
		IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
		HwndSource? source = HwndSource.FromHwnd(handle);
		return source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
	}

	private static Rect TransformRect(Rect rect, Matrix transform)
	{
		if (rect.IsEmpty)
		{
			return rect;
		}

		Point topLeft = transform.Transform(new Point(rect.Left, rect.Top));
		Point bottomRight = transform.Transform(new Point(rect.Right, rect.Bottom));
		return new Rect(topLeft, bottomRight);
	}

	private static bool TryGetWorkingAreaDevice(Point point, out Rect workingArea)
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
				workingArea = ToRect(monitorInfo.RcWork);
				return true;
			}
		}

		workingArea = Rect.Empty;
		return false;
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

	private void HideMinimizeAndMaximizeButtons()
	{
		IntPtr handle = new WindowInteropHelper(this).Handle;
		if (handle == IntPtr.Zero)
		{
			return;
		}

		int style = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_STYLE);
		int newStyle = style & ~NativeMethods.WS_MINIMIZEBOX & ~NativeMethods.WS_MAXIMIZEBOX;
		if (newStyle == style)
		{
			return;
		}

		NativeMethods.SetWindowLong(handle, NativeMethods.GWL_STYLE, newStyle);
		NativeMethods.SetWindowPos(
			handle,
			IntPtr.Zero,
			0,
			0,
			0,
			0,
			NativeMethods.SWP_NOMOVE |
			NativeMethods.SWP_NOSIZE |
			NativeMethods.SWP_NOZORDER |
			NativeMethods.SWP_FRAMECHANGED);
	}

	private static List<ClipboardHistoryEntry> LoadHistoryEntries(CancellationToken cancellationToken, string? searchText = null)
	{
		try
		{
			return ClipboardDatabase.LoadHistorySummaries(searchText, cancellationToken)
				.Select(CreateHistoryEntry)
				.ToList();
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

	private static ClipboardHistoryEntry CreateHistoryEntry(ClipboardHistorySummary summary)
	{
		return new ClipboardHistoryEntry
		{
			Id = summary.Id,
			Kind = summary.Kind,
			LastWriteTime = summary.CreatedAt,
			PreviewText = summary.PreviewText,
			Thumbnail = CreateThumbnail(summary.ThumbnailBytes)
		};
	}

	private static ImageSource? CreateThumbnail(byte[]? bytes)
	{
		if (bytes == null || bytes.Length == 0)
		{
			return null;
		}

		try
		{
			using var stream = new MemoryStream(bytes);
			var thumbnail = new BitmapImage();
			thumbnail.BeginInit();
			thumbnail.CacheOption = BitmapCacheOption.OnLoad;
			thumbnail.StreamSource = stream;
			thumbnail.EndInit();
			thumbnail.Freeze();
			return thumbnail;
		}
		catch
		{
			return null;
		}
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

	private sealed class ClipboardHistoryEntry
	{
		public required long Id { get; init; }
		public required ClipboardHistoryKind Kind { get; init; }
		public required DateTime LastWriteTime { get; init; }
		public required string PreviewText { get; init; }
		public ImageSource? Thumbnail { get; init; }
	}

	private sealed class HistoryItemControl : Border
	{
		private static readonly Brush NormalBackground = Brushes.White;
		private static readonly Brush HoverBackground = new SolidColorBrush(Color.FromRgb(232, 240, 254));
		private static readonly Brush SelectedBackground = new SolidColorBrush(Color.FromRgb(220, 232, 255));
		private static readonly Brush NormalBorderBrush = new SolidColorBrush(Color.FromRgb(216, 220, 226));
		private static readonly Brush SelectedBorderBrush = new SolidColorBrush(Color.FromRgb(70, 116, 218));
		private bool _isSelected;

		public event EventHandler? Activated;

		public bool IsSelected
		{
			get => _isSelected;
			set
			{
				if (_isSelected == value)
				{
					return;
				}

				_isSelected = value;
				UpdateVisualState();
			}
		}

		public HistoryItemControl(ClipboardHistoryEntry entry)
		{
			Margin = new Thickness(0, 0, 0, 8);
			Padding = new Thickness(10);
			Background = NormalBackground;
			BorderBrush = NormalBorderBrush;
			BorderThickness = new Thickness(1);
			Cursor = Cursors.Hand;
			Height = entry.Kind == ClipboardHistoryKind.Image ? 92 : 78;
			HorizontalAlignment = HorizontalAlignment.Stretch;
			Focusable = true;
			Child = CreateContent(entry);

			MouseEnter += (_, _) => UpdateVisualState();
			MouseLeave += (_, _) => UpdateVisualState();
			MouseLeftButtonUp += (_, _) => Activate();
		}

		public void Activate()
		{
			Activated?.Invoke(this, EventArgs.Empty);
		}

		private void UpdateVisualState()
		{
			Background = IsSelected ? SelectedBackground : IsMouseOver ? HoverBackground : NormalBackground;
			BorderBrush = IsSelected ? SelectedBorderBrush : NormalBorderBrush;
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
