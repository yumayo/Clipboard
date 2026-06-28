using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shape = System.Windows.Shapes.Shape;
using ShapePath = System.Windows.Shapes.Path;

namespace Clipboard;

internal sealed class ClipboardHistoryWindow : Window
{
	private const int SearchFilterDelayMilliseconds = 150;
	private const int HistoryPageSize = 100;
	private const int SearchHistoryPageSize = 100;
	private const int SearchResultBatchSize = 16;
	private const int HoverPreviewDelayMilliseconds = 180;
	private const int HoverPreviewHideDelayMilliseconds = 120;
	private const int HoverPreviewMaxTextLength = 5000;
	private const double LoadMoreHistoryScrollThreshold = 48;
	private const double MaxTerminalTextInputBoundsHeight = 120;
	private const double HoverPreviewWidth = 560;
	private const double HoverPreviewMaxHeight = 520;
	private const double HoverPreviewImageMaxWidth = 560;
	private const double HoverPreviewImageMaxHeight = 420;
	private const int HoverPreviewImageMaxPixelWidth = 1680;
	private const int HoverPreviewImageMaxPixelHeight = 1260;
	private readonly TextBox _searchBox;
	private readonly ScrollViewer _scrollViewer;
	private readonly StackPanel _listPanel;
	private readonly Border _selectionFooter;
	private readonly TextBlock _selectionCountLabel;
	private readonly Button _paintSelectedButton;
	private readonly List<ClipboardHistoryEntry> _historyEntries = new();
	private readonly NativeMethods.LowLevelMouseProc _outsideClickProc;
	private CancellationTokenSource? _loadHistoryCancellation;
	private CancellationTokenSource? _loadMoreHistoryCancellation;
	private CancellationTokenSource? _filterHistoryCancellation;
	private IntPtr _outsideClickHook;
	private IntPtr _targetWindow;
	private IntPtr _windowHandle;
	private IntPtr _suppressedOutsideMouseButtonUpMessage;
	private int _selectedItemIndex = -1;
	private DateTime? _lastDisplayedHistoryDate;
	private bool _isLoadingHistory;
	private bool _isLoadingMoreHistory;
	private bool _hasLoadedHistory;
	private bool _hasMoreHistory;
	private bool _allowClose;
	private bool _suppressOutsideMouseButtonUp;
	private bool _targetUsesTerminalInput;
	private IntPtr _lastTerminalTargetWindow;
	private Rect _lastTerminalAnchorBoundsDevice = Rect.Empty;

	public bool IsClosed { get; private set; }

	public ClipboardHistoryWindow()
	{
		_outsideClickProc = OnOutsideClickMouseHook;
		Title = "クリップボード履歴";
		WindowStartupLocation = WindowStartupLocation.Manual;
		Width = 520;
		Height = 640;
		MinWidth = 380;
		MinHeight = 320;
		ShowInTaskbar = false;
		ShowActivated = false;
		Focusable = false;
		UseLayoutRounding = true;
		SnapsToDevicePixels = true;
		Icon = LoadIcon();
		AppTheme.ApplyWindow(this);
		SourceInitialized += (_, _) =>
		{
			_windowHandle = new WindowInteropHelper(this).Handle;
			HideMinimizeAndMaximizeButtons();
		};

		var rootPanel = new DockPanel();
		rootPanel.SetResourceReference(Panel.BackgroundProperty, AppTheme.WindowBackgroundBrushKey);

		var searchPanel = new Grid
		{
			Margin = new Thickness(10, 10, 10, 0)
		};
		searchPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		searchPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

		var searchLabel = new TextBlock
		{
			Text = "検索",
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 8, 0),
			FontWeight = FontWeights.Bold
		};
		searchLabel.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		searchPanel.Children.Add(searchLabel);

		_searchBox = new TextBox
		{
			MinHeight = 32,
			Padding = new Thickness(8, 5, 8, 5),
			VerticalContentAlignment = VerticalAlignment.Center,
			ToolTip = "履歴を検索"
		};
		AppTheme.ApplyTextBox(_searchBox);
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

		_selectionFooter = CreateSelectionFooter(out _selectionCountLabel, out _paintSelectedButton);
		DockPanel.SetDock(_selectionFooter, Dock.Bottom);
		rootPanel.Children.Add(_selectionFooter);

		_listPanel = new StackPanel
		{
			Margin = new Thickness(10)
		};

		_scrollViewer = new ScrollViewer
		{
			Content = _listPanel,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};
		_scrollViewer.SetResourceReference(Control.BackgroundProperty, AppTheme.WindowBackgroundBrushKey);
		_scrollViewer.ScrollChanged += (_, _) => TryBeginLoadMoreHistory();
		rootPanel.Children.Add(_scrollViewer);
		Content = rootPanel;
		IsVisibleChanged += (_, _) =>
		{
			if (!IsVisible)
			{
				CloseOpenPreviews();
				// 外側クリック時は背後アプリへ Click を成立させないため、対応する MouseUp までフックを残す。
				if (!_suppressOutsideMouseButtonUp)
				{
					StopDismissOnOutsideClick();
				}

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
		bool positionedFromTextInput = MoveNearTextInput(targetWindow);
		if (!IsVisible && _searchBox.Text.Length > 0)
		{
			_searchBox.Clear();
		}

		Topmost = true;
		Show();
		if (!positionedFromTextInput)
		{
			MoveNearTextInput(targetWindow, allowMouseFallback: false);
		}

		SelectFirstHistoryItem();
		Topmost = false;
		StopWindowFlash();
		Dispatcher.BeginInvoke((Action)StopWindowFlash);
		BeginDismissOnOutsideClick();

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

		StopDismissOnOutsideClick();
		CancelHistoryLoad();
		CancelHistoryFilter();
		IsClosed = true;
		base.OnClosing(e);
	}

	private Border CreateSelectionFooter(out TextBlock countLabel, out Button paintButton)
	{
		var grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		countLabel = new TextBlock
		{
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 8, 0)
		};
		countLabel.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		Grid.SetColumn(countLabel, 0);
		grid.Children.Add(countLabel);

		var buttonPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(buttonPanel, 1);
		grid.Children.Add(buttonPanel);

		paintButton = new Button
		{
			Content = "ペイント",
			MinHeight = 32,
			Padding = new Thickness(12, 5, 12, 5),
			Margin = new Thickness(0, 0, 8, 0),
			Focusable = false,
			Cursor = Cursors.Hand,
			ToolTip = "選択した画像を1つのキャンバスに並べてペイントを開きます"
		};
		AppTheme.ApplyButton(paintButton);
		paintButton.Click += (_, _) => OpenMultiImagePaintWindow();
		buttonPanel.Children.Add(paintButton);

		var deleteButton = new Button
		{
			Content = "選択したものを削除",
			MinHeight = 32,
			Padding = new Thickness(12, 5, 12, 5),
			Focusable = false,
			Cursor = Cursors.Hand
		};
		AppTheme.ApplyButton(deleteButton);
		deleteButton.Click += (_, _) => DeleteCheckedHistoryItems();
		buttonPanel.Children.Add(deleteButton);

		var footer = new Border
		{
			Child = grid,
			Padding = new Thickness(10, 8, 10, 10),
			BorderThickness = new Thickness(0, 1, 0, 0),
			Visibility = Visibility.Collapsed
		};
		footer.SetResourceReference(Border.BackgroundProperty, AppTheme.WindowBackgroundBrushKey);
		footer.SetResourceReference(Border.BorderBrushProperty, AppTheme.BorderBrushKey);
		return footer;
	}

	private void UpdateSelectionFooter()
	{
		var checkedItems = GetHistoryItems().Where(item => item.IsChecked).ToList();
		if (checkedItems.Count == 0)
		{
			_selectionFooter.Visibility = Visibility.Collapsed;
			return;
		}

		int checkedImageCount = checkedItems.Count(item => item.Kind == ClipboardHistoryKind.Image);
		_selectionCountLabel.Text = $"{checkedItems.Count}件を選択中";
		_paintSelectedButton.Visibility = checkedImageCount > 0 ? Visibility.Visible : Visibility.Collapsed;
		_selectionFooter.Visibility = Visibility.Visible;
	}

	// チェックが1つでも付いている選択モード中かどうか。フッターの表示状態と同期している。
	private bool IsSelectionModeActive()
	{
		return _selectionFooter.Visibility == Visibility.Visible;
	}

	private void DeleteCheckedHistoryItems()
	{
		var checkedItems = GetHistoryItems().Where(item => item.IsChecked).ToList();
		if (checkedItems.Count == 0)
		{
			UpdateSelectionFooter();
			return;
		}

		var checkedIds = checkedItems.Select(item => item.EntryId).ToList();
		try
		{
			ClipboardDatabase.DeleteHistory(checkedIds);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardHistoryWindow: 選択した履歴の削除に失敗しました。");
			MessageBox.Show(this, "選択した履歴を削除できませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
			return;
		}

		var checkedIdSet = checkedIds.ToHashSet();
		_historyEntries.RemoveAll(entry => checkedIdSet.Contains(entry.Id));
		foreach (var item in checkedItems)
		{
			item.ClosePreview();
			_listPanel.Children.Remove(item);
		}

		RemoveEmptyDateSeparators();
		UpdateSelectionFooter();
		RefreshSelectionAfterDeletion();
	}

	private void RemoveEmptyDateSeparators()
	{
		var children = _listPanel.Children;
		for (int i = children.Count - 1; i >= 0; i--)
		{
			if (children[i] is not HistoryDateSeparatorControl)
			{
				continue;
			}

			bool hasFollowingItem = i + 1 < children.Count && children[i + 1] is HistoryItemControl;
			if (!hasFollowingItem)
			{
				children.RemoveAt(i);
			}
		}

		_lastDisplayedHistoryDate = _listPanel.Children.OfType<HistoryItemControl>().LastOrDefault()?.CreatedDate;
	}

	private void RefreshSelectionAfterDeletion()
	{
		var items = GetHistoryItems();
		if (items.Count == 0)
		{
			_selectedItemIndex = -1;
			if (!_listPanel.Children.OfType<UIElement>().Any())
			{
				AddMessage(string.IsNullOrWhiteSpace(_searchBox.Text) ? "履歴がありません" : "一致する履歴がありません");
			}

			return;
		}

		int index = _selectedItemIndex < 0 ? 0 : Math.Min(_selectedItemIndex, items.Count - 1);
		_selectedItemIndex = -1;
		SelectHistoryItem(index, items);
	}

	private void BeginDismissOnOutsideClick()
	{
		StopDismissOnOutsideClick();
		_outsideClickHook = SetOutsideClickHook(_outsideClickProc);
		if (_outsideClickHook == IntPtr.Zero)
		{
			Logger.Warning($"ClipboardHistoryWindow: 外側クリック監視の開始に失敗しました。Win32Error={Marshal.GetLastWin32Error()}");
		}
	}

	private static IntPtr SetOutsideClickHook(NativeMethods.LowLevelMouseProc proc)
	{
		using var curProcess = Process.GetCurrentProcess();
		using var curModule = curProcess.MainModule;
		return NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, proc, NativeMethods.GetModuleHandle(curModule?.ModuleName), 0);
	}

	private void StopDismissOnOutsideClick()
	{
		_suppressOutsideMouseButtonUp = false;
		_suppressedOutsideMouseButtonUpMessage = IntPtr.Zero;
		if (_outsideClickHook == IntPtr.Zero)
		{
			return;
		}

		if (!NativeMethods.UnhookWindowsHookEx(_outsideClickHook))
		{
			Logger.Warning($"ClipboardHistoryWindow: 外側クリック監視の解除に失敗しました。Win32Error={Marshal.GetLastWin32Error()}");
		}

		_outsideClickHook = IntPtr.Zero;
	}

	private IntPtr OnOutsideClickMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
	{
		if (nCode >= 0)
		{
			if (_suppressOutsideMouseButtonUp && wParam == _suppressedOutsideMouseButtonUpMessage)
			{
				StopDismissOnOutsideClick();
				return (IntPtr)1;
			}

			if (IsMouseButtonDownMessage(wParam) || IsMouseWheelMessage(wParam))
			{
				var mouse = Marshal.PtrToStructure<NativeMethods.MouseLlHookStruct>(lParam);
				if (TryGetOutsideWindowAtPoint(mouse.Pt, out IntPtr outsideWindow))
				{
					if (IsMouseButtonDownMessage(wParam))
					{
						BeginSuppressOutsideMouseButtonUp(wParam);
						HideFromOutsideClick(mouse.Pt, outsideWindow);
					}

					return (IntPtr)1;
				}
			}
		}

		return NativeMethods.CallNextHookEx(_outsideClickHook, nCode, wParam, lParam);
	}

	private static bool IsMouseButtonDownMessage(IntPtr wParam)
	{
		return wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN ||
			wParam == (IntPtr)NativeMethods.WM_RBUTTONDOWN ||
			wParam == (IntPtr)NativeMethods.WM_MBUTTONDOWN ||
			wParam == (IntPtr)NativeMethods.WM_XBUTTONDOWN;
	}

	private static bool IsMouseWheelMessage(IntPtr wParam)
	{
		return wParam == (IntPtr)NativeMethods.WM_MOUSEWHEEL ||
			wParam == (IntPtr)NativeMethods.WM_MOUSEHWHEEL;
	}

	private static IntPtr GetMouseButtonUpMessage(IntPtr mouseButtonDownMessage)
	{
		if (mouseButtonDownMessage == (IntPtr)NativeMethods.WM_LBUTTONDOWN)
		{
			return (IntPtr)NativeMethods.WM_LBUTTONUP;
		}

		if (mouseButtonDownMessage == (IntPtr)NativeMethods.WM_RBUTTONDOWN)
		{
			return (IntPtr)NativeMethods.WM_RBUTTONUP;
		}

		if (mouseButtonDownMessage == (IntPtr)NativeMethods.WM_MBUTTONDOWN)
		{
			return (IntPtr)NativeMethods.WM_MBUTTONUP;
		}

		if (mouseButtonDownMessage == (IntPtr)NativeMethods.WM_XBUTTONDOWN)
		{
			return (IntPtr)NativeMethods.WM_XBUTTONUP;
		}

		return IntPtr.Zero;
	}

	private void BeginSuppressOutsideMouseButtonUp(IntPtr mouseButtonDownMessage)
	{
		_suppressOutsideMouseButtonUp = true;
		_suppressedOutsideMouseButtonUpMessage = GetMouseButtonUpMessage(mouseButtonDownMessage);
		_ = StopOutsideClickHookAfterMouseUpTimeoutAsync();
	}

	private async Task StopOutsideClickHookAfterMouseUpTimeoutAsync()
	{
		await Task.Delay(1000);
		if (!IsVisible && _suppressOutsideMouseButtonUp)
		{
			StopDismissOnOutsideClick();
		}
	}

	private bool TryGetOutsideWindowAtPoint(NativeMethods.NativePoint point, out IntPtr outsideWindow)
	{
		outsideWindow = IntPtr.Zero;
		if (!IsVisible || IsClosed)
		{
			return false;
		}

		if (IsPointInsideOpenPreview(point))
		{
			return false;
		}

		IntPtr windowHandle = GetWindowHandle();
		IntPtr windowAtPoint = NativeMethods.WindowFromPoint(point);
		if (IsSameRootWindow(windowAtPoint, windowHandle))
		{
			return false;
		}

		outsideWindow = windowAtPoint;
		return true;
	}

	private bool IsPointInsideOpenPreview(NativeMethods.NativePoint point)
	{
		return _listPanel.Children.OfType<HistoryItemControl>().Any(item => item.IsPointInsidePreview(point));
	}

	private void HideFromOutsideClick(NativeMethods.NativePoint point, IntPtr clickedWindow)
	{
		if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
		{
			return;
		}

		IntPtr windowHandle = GetWindowHandle();
		Logger.Debug($"ClipboardHistoryWindow: 履歴画面外がクリックされたため履歴画面を閉じます。Point=({point.X},{point.Y}) Window={FormatHandle(windowHandle)} ClickedWindow={FormatHandle(clickedWindow)}");
		Hide();
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

	private IntPtr GetWindowHandle()
	{
		if (_windowHandle == IntPtr.Zero)
		{
			_windowHandle = new WindowInteropHelper(this).EnsureHandle();
		}

		return _windowHandle;
	}

	private async void BeginLoadHistory(bool preserveExistingItems)
	{
		CancelHistoryLoad();
		_loadMoreHistoryCancellation = null;
		_isLoadingMoreHistory = false;
		_hasMoreHistory = false;
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
			entries = await Task.Run(
				() => LoadHistoryEntries(cancellationTokenSource.Token, maxEntryCount: HistoryPageSize),
				cancellationTokenSource.Token);
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
		_hasMoreHistory = entries.Count >= HistoryPageSize;
		BeginApplyHistoryFilter(delay: false);
	}

	private void TryBeginLoadMoreHistory()
	{
		if (!IsVisible ||
			!_hasLoadedHistory ||
			_isLoadingHistory ||
			_isLoadingMoreHistory ||
			!_hasMoreHistory ||
			_filterHistoryCancellation != null ||
			_historyEntries.Count == 0 ||
			!string.IsNullOrWhiteSpace(_searchBox.Text))
		{
			return;
		}

		if (_scrollViewer.ScrollableHeight <= 0 || _scrollViewer.VerticalOffset <= 0)
		{
			return;
		}

		if (_scrollViewer.VerticalOffset < Math.Max(0, _scrollViewer.ScrollableHeight - LoadMoreHistoryScrollThreshold))
		{
			return;
		}

		BeginLoadMoreHistory();
	}

	private async void BeginLoadMoreHistory()
	{
		if (_historyEntries.Count == 0 || _isLoadingMoreHistory || !_hasMoreHistory)
		{
			return;
		}

		var lastEntry = _historyEntries[^1];
		var cancellationTokenSource = new CancellationTokenSource();
		_loadMoreHistoryCancellation = cancellationTokenSource;
		_isLoadingMoreHistory = true;

		List<ClipboardHistoryEntry> entries;
		try
		{
			entries = await Task.Run(
				() => LoadHistoryPageEntries(
					cancellationTokenSource.Token,
					lastEntry.Id,
					HistoryPageSize),
				cancellationTokenSource.Token);
		}
		catch (OperationCanceledException)
		{
			if (_loadMoreHistoryCancellation == cancellationTokenSource)
			{
				_isLoadingMoreHistory = false;
			}

			return;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardHistoryWindow: 追加履歴の読み込みに失敗しました。");
			if (_loadMoreHistoryCancellation == cancellationTokenSource)
			{
				_isLoadingMoreHistory = false;
			}

			return;
		}

		if (cancellationTokenSource.IsCancellationRequested || _loadMoreHistoryCancellation != cancellationTokenSource)
		{
			if (_loadMoreHistoryCancellation == cancellationTokenSource)
			{
				_isLoadingMoreHistory = false;
			}

			return;
		}

		_isLoadingMoreHistory = false;
		_hasMoreHistory = entries.Count >= HistoryPageSize;
		if (entries.Count == 0)
		{
			return;
		}

		_historyEntries.AddRange(entries);
		if (!string.IsNullOrWhiteSpace(_searchBox.Text))
		{
			return;
		}

		if (_filterHistoryCancellation == null)
		{
			AddMatchedHistoryEntries(entries);
			return;
		}

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
			int matchedEntryCount;
			if (hasSearchText)
			{
				matchedEntryCount = await DisplaySearchHistoryEntriesProgressivelyAsync(searchText, cancellationTokenSource);
			}
			else
			{
				matchedEntryCount = await DisplayHistoryEntriesProgressivelyAsync(entriesSnapshot, cancellationTokenSource);
			}

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
			AddDateSeparatorIfNeeded(entry.CreatedAt.Date);
			var item = new HistoryItemControl(entry, LoadHistoryHoverPreviewAsync, IsSelectionModeActive);
			item.Activated += (_, _) => PasteEntry(entry);
			item.PaintRequested += (_, _) => OpenImagePaintWindow(entry);
			item.CheckedChanged += (_, _) => UpdateSelectionFooter();
			_listPanel.Children.Add(item);
		}

		if (shouldSelectFirstItem)
		{
			SelectFirstHistoryItem();
		}
	}

	private void AddDateSeparatorIfNeeded(DateTime entryDate)
	{
		DateTime? lastDisplayedDate = _lastDisplayedHistoryDate;
		if (!lastDisplayedDate.HasValue)
		{
			lastDisplayedDate = _listPanel.Children.OfType<HistoryItemControl>().LastOrDefault()?.CreatedDate;
		}

		if (lastDisplayedDate == entryDate)
		{
			_lastDisplayedHistoryDate = entryDate;
			return;
		}

		_listPanel.Children.Add(new HistoryDateSeparatorControl(entryDate));
		_lastDisplayedHistoryDate = entryDate;
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

	private async Task<int> DisplaySearchHistoryEntriesProgressivelyAsync(
		string searchText,
		CancellationTokenSource cancellationTokenSource)
	{
		CancellationToken cancellationToken = cancellationTokenSource.Token;
		long? beforeId = null;
		int matchedEntryCount = 0;

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			List<ClipboardHistoryEntry> entries = await Task.Run(
				() => LoadSearchHistoryEntries(
					cancellationToken,
					searchText,
					beforeId,
					SearchHistoryPageSize),
				cancellationToken);

			if (entries.Count == 0)
			{
				return matchedEntryCount;
			}

			matchedEntryCount += await DisplayHistoryEntriesProgressivelyAsync(entries, cancellationTokenSource);
			beforeId = entries[^1].Id;

			if (entries.Count < SearchHistoryPageSize)
			{
				return matchedEntryCount;
			}
		}
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
		ClipboardManager.PasteHistoryEntry(entry.Id, _targetWindow, _targetUsesTerminalInput);
	}

	private async void OpenImagePaintWindow(ClipboardHistoryEntry entry)
	{
		if (entry.Kind != ClipboardHistoryKind.Image)
		{
			return;
		}

		try
		{
			ClipboardStoredContent? content = await Task.Run(() => ClipboardDatabase.LoadContent(entry.Id));
			if (content == null || content.Kind != ClipboardHistoryKind.Image || content.Bytes.Length == 0)
			{
				throw new InvalidOperationException("画像履歴を読み込めませんでした。");
			}

			CloseOpenPreviews();
			Hide();
			ImagePaintWindow paintWindow = CreateImagePaintWindow(content);
			paintWindow.Show();
			paintWindow.Activate();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, $"ClipboardHistoryWindow: ペイント画面を開けませんでした。Id={entry.Id}");
			MessageBox.Show(this, "ペイント画面を開けませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private static ImagePaintWindow CreateImagePaintWindow(ClipboardStoredContent content)
	{
		if (ImagePaintSerializedState.TryDeserialize(content.PaintStateJson, out ImagePaintSerializedState? savedState) &&
			savedState != null)
		{
			try
			{
				return new ImagePaintWindow(savedState);
			}
			catch (Exception ex)
			{
				Logger.Warning($"ClipboardHistoryWindow: 保存されたペイント状態の復元に失敗したため画像として開きます。Error={ex.Message}");
			}
		}

		return new ImagePaintWindow(content.Bytes);
	}

	private async void OpenMultiImagePaintWindow()
	{
		var imageIds = GetHistoryItems()
			.Where(item => item.IsChecked && item.Kind == ClipboardHistoryKind.Image)
			.Select(item => item.EntryId)
			.ToList();
		if (imageIds.Count == 0)
		{
			MessageBox.Show(this, "ペイントを開くには画像を選択してください。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}

		try
		{
			List<byte[]> imageBytesList = await Task.Run(() => LoadImageContents(imageIds));
			if (imageBytesList.Count == 0)
			{
				throw new InvalidOperationException("画像履歴を読み込めませんでした。");
			}

			CloseOpenPreviews();
			Hide();
			var paintWindow = new ImagePaintWindow(imageBytesList);
			paintWindow.Show();
			paintWindow.Activate();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardHistoryWindow: 複数画像のペイント画面を開けませんでした。");
			MessageBox.Show(this, "ペイント画面を開けませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private static List<byte[]> LoadImageContents(IReadOnlyList<long> imageIds)
	{
		var imageBytesList = new List<byte[]>(imageIds.Count);
		foreach (long imageId in imageIds)
		{
			ClipboardStoredContent? content = ClipboardDatabase.LoadContent(imageId);
			if (content != null && content.Kind == ClipboardHistoryKind.Image && content.Bytes.Length > 0)
			{
				imageBytesList.Add(content.Bytes);
			}
		}

		return imageBytesList;
	}

	private void AddMessage(string text)
	{
		var message = new TextBlock
		{
			Text = text,
			Height = 80,
			TextAlignment = TextAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		message.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.MutedTextBrushKey);
		_listPanel.Children.Add(message);
	}

	private void ClearHistoryControls()
	{
		CloseOpenPreviews();
		_listPanel.Children.Clear();
		_selectedItemIndex = -1;
		_lastDisplayedHistoryDate = null;
		_scrollViewer.ScrollToTop();
		UpdateSelectionFooter();
	}

	private void CloseOpenPreviews()
	{
		foreach (var item in _listPanel.Children.OfType<HistoryItemControl>())
		{
			item.ClosePreview();
		}
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

		if (_loadMoreHistoryCancellation is { IsCancellationRequested: false })
		{
			_loadMoreHistoryCancellation.Cancel();
		}
	}

	private void CancelHistoryFilter()
	{
		if (_filterHistoryCancellation is { IsCancellationRequested: false })
		{
			_filterHistoryCancellation.Cancel();
		}
	}

	private bool MoveNearTextInput(IntPtr targetWindow, bool allowMouseFallback = true)
	{
		bool hasTextInputBounds = TryGetTextInputBounds(
			targetWindow,
			out Rect textInputBoundsDevice,
			out string textInputSource,
			out bool usesTerminalInput);
		_targetUsesTerminalInput = usesTerminalInput;
		Rect anchorBoundsDevice;
		bool positionedFromTextInput = true;
		if (hasTextInputBounds)
		{
			anchorBoundsDevice = textInputBoundsDevice;
			if (usesTerminalInput)
			{
				_lastTerminalTargetWindow = targetWindow;
				_lastTerminalAnchorBoundsDevice = textInputBoundsDevice;
			}

			Logger.Debug($"ClipboardHistoryWindow: テキスト入力座標を取得しました。Source={textInputSource} TargetWindow={FormatHandle(targetWindow)} BoundsDevice={anchorBoundsDevice}");
		}
		else if (usesTerminalInput &&
			_lastTerminalTargetWindow == targetWindow &&
			IsUsableRect(_lastTerminalAnchorBoundsDevice))
		{
			anchorBoundsDevice = _lastTerminalAnchorBoundsDevice;
			Logger.Debug($"ClipboardHistoryWindow: ターミナル入力座標を取得できなかったため直前の座標を使用します。TargetWindow={FormatHandle(targetWindow)} BoundsDevice={anchorBoundsDevice}");
		}
		else
		{
			if (!allowMouseFallback)
			{
				return false;
			}

			Point cursorPoint = GetCursorPoint();
			anchorBoundsDevice = new Rect(cursorPoint, new Size(0, 0));
			positionedFromTextInput = false;
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
		return positionedFromTextInput;
	}

	private static bool TryGetTextInputBounds(IntPtr targetWindow, out Rect bounds, out string source, out bool usesTerminalInput)
	{
		usesTerminalInput = false;
		if (TryGetWin32TextInputBounds(targetWindow, out bounds))
		{
			source = "Win32";
			return true;
		}

		if (TryGetAutomationTextInputBounds(out bounds, out usesTerminalInput))
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

	private static bool TryGetAutomationTextInputBounds(out Rect bounds, out bool usesTerminalInput)
	{
		bounds = Rect.Empty;
		usesTerminalInput = false;
		try
		{
			AutomationElement focusedElement = AutomationElement.FocusedElement;
			if (focusedElement == null)
			{
				Logger.Debug("ClipboardHistoryWindow: UI Automation テキスト入力座標の取得に失敗しました。FocusedElement が null です。");
				return false;
			}

			Logger.Debug($"ClipboardHistoryWindow: UI Automation focused element を取得しました。{FormatAutomationElement(focusedElement)}");
			usesTerminalInput = IsTerminalAutomationElement(focusedElement);
			if (usesTerminalInput)
			{
				Logger.Debug("ClipboardHistoryWindow: ターミナル入力要素として扱います。");
			}

			bool isTextInputElement = IsTextInputAutomationElement(focusedElement);
			if ((usesTerminalInput || isTextInputElement) &&
				TryGetAutomationTextPatternBounds(focusedElement, usesTerminalInput, out bounds))
			{
				return true;
			}

			if (usesTerminalInput)
			{
				Logger.Debug("ClipboardHistoryWindow: ターミナル入力の有効なキャレット矩形を取得できませんでした。");
				return false;
			}

			if (!isTextInputElement)
			{
				Logger.Debug("ClipboardHistoryWindow: UI Automation focused element はテキスト入力ではないため、要素全体の矩形は使用しません。");
				return false;
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

	private static bool IsTextInputAutomationElement(AutomationElement element)
	{
		ControlType controlType = element.Current.ControlType;
		return Equals(controlType, ControlType.Edit) ||
			Equals(controlType, ControlType.ComboBox);
	}

	private static bool IsTerminalAutomationElement(AutomationElement element)
	{
		string className = element.Current.ClassName ?? string.Empty;
		string automationId = element.Current.AutomationId ?? string.Empty;
		return ContainsOrdinalIgnoreCase(className, "xterm") ||
			ContainsOrdinalIgnoreCase(className, "terminal") ||
			ContainsOrdinalIgnoreCase(automationId, "terminal");
	}

	private static bool ContainsOrdinalIgnoreCase(string value, string searchText)
	{
		return value.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool TryGetAutomationTextPatternBounds(AutomationElement element, bool usesTerminalInput, out Rect bounds)
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
			if (TryGetTextPatternRangeBounds(range, usesTerminalInput, out bounds))
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

	private static bool TryGetTextPatternRangeBounds(TextPatternRange range, bool usesTerminalInput, out Rect bounds)
	{
		if (TryGetBoundingRectangle(range, usesTerminalInput, out bounds))
		{
			return true;
		}

		foreach (TextUnit textUnit in new[] { TextUnit.Character, TextUnit.Word, TextUnit.Line })
		{
			TextPatternRange expandedRange = range.Clone();
			expandedRange.ExpandToEnclosingUnit(textUnit);
			if (TryGetBoundingRectangle(expandedRange, usesTerminalInput, out bounds))
			{
				Logger.Debug($"ClipboardHistoryWindow: UI Automation range を {textUnit} に拡張して矩形を取得しました。Bounds={bounds}");
				return true;
			}
		}

		return false;
	}

	private static bool TryGetBoundingRectangle(TextPatternRange range, bool usesTerminalInput, out Rect bounds)
	{
		bounds = Rect.Empty;
		foreach (Rect rectangle in range.GetBoundingRectangles())
		{
			if (!IsUsableRect(rectangle))
			{
				continue;
			}

			if (usesTerminalInput && !IsUsableTerminalTextInputRect(rectangle))
			{
				Logger.Debug($"ClipboardHistoryWindow: ターミナル入力のキャレットとしては大きすぎる矩形を無視します。Bounds={rectangle}");
				continue;
			}

			bounds = rectangle;
			return true;
		}

		return false;
	}

	private static bool IsUsableTerminalTextInputRect(Rect rectangle)
	{
		return rectangle.Height <= MaxTerminalTextInputBoundsHeight;
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

	private static List<ClipboardHistoryEntry> LoadHistoryEntries(
		CancellationToken cancellationToken,
		string? searchText = null,
		int? maxEntryCount = null)
	{
		try
		{
			return ClipboardDatabase.LoadHistorySummaries(searchText, maxEntryCount, cancellationToken)
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

	private static List<ClipboardHistoryEntry> LoadSearchHistoryEntries(
		CancellationToken cancellationToken,
		string searchText,
		long? beforeId,
		int maxEntryCount)
	{
		try
		{
			return ClipboardDatabase.LoadHistorySearchSummaries(searchText, beforeId, maxEntryCount, cancellationToken)
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

	private static List<ClipboardHistoryEntry> LoadHistoryPageEntries(
		CancellationToken cancellationToken,
		long beforeId,
		int maxEntryCount)
	{
		try
		{
			return ClipboardDatabase.LoadHistoryPageSummaries(beforeId, maxEntryCount, cancellationToken)
				.Select(CreateHistoryEntry)
				.ToList();
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardHistoryWindow: 追加履歴の読み込みに失敗しました。");
			return new List<ClipboardHistoryEntry>();
		}
	}

	private static ClipboardHistoryEntry CreateHistoryEntry(ClipboardHistorySummary summary)
	{
		return new ClipboardHistoryEntry
		{
			Id = summary.Id,
			Kind = summary.Kind,
			CreatedAt = summary.CreatedAt,
			PreviewText = CreatePreviewText(summary),
			Thumbnail = CreateThumbnail(summary)
		};
	}

	private static string CreatePreviewText(ClipboardHistorySummary summary)
	{
		if (summary.Kind != ClipboardHistoryKind.Html)
		{
			return summary.PreviewText;
		}

		try
		{
			ClipboardStoredContent? content = ClipboardDatabase.LoadContent(summary.Id);
			if (content == null || content.Kind != ClipboardHistoryKind.Html)
			{
				return summary.PreviewText;
			}

			string plainText = ClipboardHistoryMetadata.CreateSearchableText(content.Bytes, content.Kind);
			return ClipboardHistoryMetadata.CreatePreviewText(plainText, summary.PreviewText);
		}
		catch (Exception ex)
		{
			Logger.Debug($"ClipboardHistoryWindow: HTML プレビューの再生成に失敗しました。Id={summary.Id} Error={ex.Message}");
			return summary.PreviewText;
		}
	}

	private static ImageSource? CreateThumbnail(ClipboardHistorySummary summary)
	{
		ImageSource? thumbnail = CreateThumbnail(summary.ThumbnailBytes);
		if (summary.Kind != ClipboardHistoryKind.Image || IsHighResolutionThumbnail(thumbnail))
		{
			return thumbnail;
		}

		return CreateThumbnailFromContent(summary.Id) ?? thumbnail;
	}

	private static bool IsHighResolutionThumbnail(ImageSource? thumbnail)
	{
		return thumbnail is BitmapSource bitmap &&
			(bitmap.PixelWidth >= ClipboardHistoryMetadata.ThumbnailLogicalWidth * 2 ||
				bitmap.PixelHeight >= ClipboardHistoryMetadata.ThumbnailLogicalHeight * 2);
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

	private static ImageSource? CreateThumbnailFromContent(long historyId)
	{
		try
		{
			ClipboardStoredContent? content = ClipboardDatabase.LoadContent(historyId);
			if (content == null || content.Kind != ClipboardHistoryKind.Image)
			{
				return null;
			}

			return CreateDecodedImage(
				content.Bytes,
				ClipboardHistoryMetadata.ThumbnailPixelWidth,
				ClipboardHistoryMetadata.ThumbnailPixelHeight);
		}
		catch (Exception ex)
		{
			Logger.Debug($"ClipboardHistoryWindow: 高解像度サムネイルの生成に失敗しました。Id={historyId} Error={ex.Message}");
			return null;
		}
	}

	private static Task<HistoryHoverPreview> LoadHistoryHoverPreviewAsync(
		ClipboardHistoryEntry entry,
		CancellationToken cancellationToken)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			ClipboardStoredContent? content = ClipboardDatabase.LoadContent(entry.Id);
			cancellationToken.ThrowIfCancellationRequested();
			if (content == null)
			{
				return new HistoryHoverPreview
				{
					Kind = entry.Kind,
					Text = entry.PreviewText
				};
			}

			if (content.Kind == ClipboardHistoryKind.Image)
			{
				return new HistoryHoverPreview
				{
					Kind = content.Kind,
					Text = entry.PreviewText,
					Image = CreateDecodedImage(content.Bytes, HoverPreviewImageMaxPixelWidth, HoverPreviewImageMaxPixelHeight)
				};
			}

			string text = ClipboardHistoryMetadata.CreateDisplayText(content.Bytes, content.Kind);
			if (string.IsNullOrWhiteSpace(text))
			{
				text = entry.PreviewText;
			}

			return new HistoryHoverPreview
			{
				Kind = content.Kind,
				Text = TrimHoverPreviewText(text)
			};
		}, cancellationToken);
	}

	private static string TrimHoverPreviewText(string text)
	{
		return text.Length <= HoverPreviewMaxTextLength
			? text
			: text[..HoverPreviewMaxTextLength].TrimEnd() + "\n...";
	}

	private static ImageSource? CreateDecodedImage(byte[] bytes, int maxPixelWidth, int maxPixelHeight)
	{
		if (bytes.Length == 0)
		{
			return null;
		}

		try
		{
			(int Width, int Height)? pixelSize = TryGetBitmapPixelSize(bytes);
			using var stream = new MemoryStream(bytes);
			var image = new BitmapImage();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.StreamSource = stream;
			ConfigureDecodePixelSize(image, pixelSize, maxPixelWidth, maxPixelHeight);
			image.EndInit();
			image.Freeze();
			return image;
		}
		catch
		{
			return null;
		}
	}

	private static (int Width, int Height)? TryGetBitmapPixelSize(byte[] bytes)
	{
		try
		{
			using var stream = new MemoryStream(bytes);
			BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
			BitmapFrame frame = decoder.Frames[0];
			return frame.PixelWidth > 0 && frame.PixelHeight > 0
				? (frame.PixelWidth, frame.PixelHeight)
				: null;
		}
		catch
		{
			return null;
		}
	}

	private static void ConfigureDecodePixelSize(
		BitmapImage image,
		(int Width, int Height)? pixelSize,
		int maxPixelWidth,
		int maxPixelHeight)
	{
		if (pixelSize is not { } size)
		{
			return;
		}

		double widthScale = (double)maxPixelWidth / size.Width;
		double heightScale = (double)maxPixelHeight / size.Height;
		double scale = Math.Min(widthScale, heightScale);
		if (!double.IsFinite(scale) || scale <= 0 || scale >= 1)
		{
			return;
		}

		if (widthScale < heightScale)
		{
			image.DecodePixelWidth = Math.Max(1, (int)Math.Round(size.Width * scale));
		}
		else
		{
			image.DecodePixelHeight = Math.Max(1, (int)Math.Round(size.Height * scale));
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
		public required DateTime CreatedAt { get; init; }
		public required string PreviewText { get; init; }
		public ImageSource? Thumbnail { get; init; }
	}

	private sealed class HistoryHoverPreview
	{
		public required ClipboardHistoryKind Kind { get; init; }
		public string? Text { get; init; }
		public ImageSource? Image { get; init; }
	}

	private sealed class HistoryDateSeparatorControl : Border
	{
		private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo("ja-JP");

		public HistoryDateSeparatorControl(DateTime date)
		{
			Margin = new Thickness(0, 8, 0, 6);
			Padding = new Thickness(2, 0, 2, 0);
			Focusable = false;
			var label = new TextBlock
			{
				Text = FormatDate(date),
				FontSize = 12,
				FontWeight = FontWeights.SemiBold
			};
			label.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.MutedTextBrushKey);
			Child = label;
		}

		private static string FormatDate(DateTime date)
		{
			DateTime today = DateTime.Today;
			if (date == today)
			{
				return $"今日 {date.ToString("M月d日 (ddd)", JapaneseCulture)}";
			}

			if (date == today.AddDays(-1))
			{
				return $"昨日 {date.ToString("M月d日 (ddd)", JapaneseCulture)}";
			}

			string format = date.Year == today.Year
				? "M月d日 (ddd)"
				: "yyyy年M月d日 (ddd)";
			return date.ToString(format, JapaneseCulture);
		}
	}

	private sealed class HistoryItemControl : Border
	{
		private readonly ClipboardHistoryEntry _entry;
		private readonly Func<ClipboardHistoryEntry, CancellationToken, Task<HistoryHoverPreview>> _previewLoader;
		private readonly Func<bool> _isSelectionModeActive;
		private readonly Popup _previewPopup;
		private readonly CheckBox _selectionCheckBox;
		private CancellationTokenSource? _previewCancellation;
		private bool _isSelected;
		private bool _isMouseOverPreview;
		private int _previewHideVersion;

		public event EventHandler? Activated;
		public event EventHandler? PaintRequested;
		public event EventHandler? CheckedChanged;

		public long EntryId => _entry.Id;

		public ClipboardHistoryKind Kind => _entry.Kind;

		public DateTime CreatedDate => _entry.CreatedAt.Date;

		public bool IsChecked => _selectionCheckBox.IsChecked == true;

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

		public HistoryItemControl(
			ClipboardHistoryEntry entry,
			Func<ClipboardHistoryEntry, CancellationToken, Task<HistoryHoverPreview>> previewLoader,
			Func<bool> isSelectionModeActive)
		{
			_entry = entry;
			_previewLoader = previewLoader;
			_isSelectionModeActive = isSelectionModeActive;
			Margin = new Thickness(0, 0, 0, 8);
			Padding = new Thickness(10);
			BorderThickness = new Thickness(1);
			Cursor = Cursors.Hand;
			Height = entry.Kind == ClipboardHistoryKind.Image
				? ClipboardHistoryMetadata.ThumbnailLogicalHeight + 22
				: 78;
			HorizontalAlignment = HorizontalAlignment.Stretch;
			Focusable = true;
			_selectionCheckBox = CreateSelectionCheckBox();
			Child = CreateRootContent(entry);
			_previewPopup = CreatePreviewPopup();
			UpdateVisualState();

			MouseEnter += (_, _) =>
			{
				UpdateVisualState();
				BeginShowPreview();
			};
			MouseLeave += (_, _) =>
			{
				UpdateVisualState();
				BeginHidePreview();
			};
			MouseLeftButtonUp += (_, _) => OnItemClicked();
			Unloaded += (_, _) => HidePreview();
		}

		public void Activate()
		{
			Activated?.Invoke(this, EventArgs.Empty);
		}

		private void OnItemClicked()
		{
			// チェックが1つでも付いている選択モード中は、アイテム本体のクリックを
			// 貼り付けではなくチェックのトグルとして扱い、誤って貼り付けてしまうのを防ぐ。
			if (_isSelectionModeActive())
			{
				_selectionCheckBox.IsChecked = !IsChecked;
				return;
			}

			Activate();
		}

		public void ClosePreview()
		{
			HidePreview();
		}

		public bool IsPointInsidePreview(NativeMethods.NativePoint point)
		{
			if (!_previewPopup.IsOpen ||
				_previewPopup.Child is not FrameworkElement child ||
				child.ActualWidth <= 0 ||
				child.ActualHeight <= 0)
			{
				return false;
			}

			try
			{
				Point topLeft = child.PointToScreen(new Point(0, 0));
				Point bottomRight = child.PointToScreen(new Point(child.ActualWidth, child.ActualHeight));
				return new Rect(topLeft, bottomRight).Contains(new Point(point.X, point.Y));
			}
			catch (InvalidOperationException)
			{
				return false;
			}
		}

		private void UpdateVisualState()
		{
			SetResourceReference(
				Border.BackgroundProperty,
				IsSelected || IsChecked ? AppTheme.SurfaceSelectedBrushKey : IsMouseOver ? AppTheme.SurfaceHoverBrushKey : AppTheme.SurfaceBrushKey);
			SetResourceReference(
				Border.BorderBrushProperty,
				IsSelected || IsChecked ? AppTheme.AccentBorderBrushKey : AppTheme.BorderBrushKey);
		}

		private Popup CreatePreviewPopup()
		{
			return new Popup
			{
				PlacementTarget = this,
				Placement = PlacementMode.Right,
				HorizontalOffset = 8,
				VerticalOffset = -6,
				AllowsTransparency = true,
				StaysOpen = true,
				PopupAnimation = PopupAnimation.None
			};
		}

		private async void BeginShowPreview()
		{
			_previewHideVersion++;
			if (_previewPopup.IsOpen && _previewPopup.Child != null)
			{
				return;
			}

			CancelPreviewLoad();
			var cancellationTokenSource = new CancellationTokenSource();
			_previewCancellation = cancellationTokenSource;

			try
			{
				await Task.Delay(HoverPreviewDelayMilliseconds, cancellationTokenSource.Token);
				if (!IsPreviewRequested(cancellationTokenSource))
				{
					return;
				}

				HistoryHoverPreview preview = await _previewLoader(_entry, cancellationTokenSource.Token);
				if (!IsPreviewRequested(cancellationTokenSource))
				{
					return;
				}

				ShowPreviewContent(CreatePreviewContent(preview));
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				Logger.Error(ex, $"ClipboardHistoryWindow: QuickView の表示に失敗しました。Id={_entry.Id}");
				if (IsPreviewRequested(cancellationTokenSource))
				{
					ShowPreviewContent(CreateTextPreviewContent("プレビューを表示できません"));
				}
			}
			finally
			{
				if (_previewCancellation == cancellationTokenSource)
				{
					_previewCancellation = null;
				}
			}
		}

		private void ShowPreviewContent(FrameworkElement content)
		{
			SetPreviewContent(content);
			_previewPopup.IsOpen = true;
		}

		private bool IsPreviewRequested(CancellationTokenSource cancellationTokenSource)
		{
			return _previewCancellation == cancellationTokenSource &&
				!cancellationTokenSource.IsCancellationRequested &&
				(IsMouseOver || _isMouseOverPreview);
		}

		private async void BeginHidePreview()
		{
			int hideVersion = ++_previewHideVersion;
			await Task.Delay(HoverPreviewHideDelayMilliseconds);
			if (hideVersion == _previewHideVersion && !IsMouseOver && !_isMouseOverPreview)
			{
				HidePreview();
			}
		}

		private void HidePreview()
		{
			CancelPreviewLoad();
			_isMouseOverPreview = false;
			_previewPopup.IsOpen = false;
			_previewPopup.Child = null;
		}

		private void CancelPreviewLoad()
		{
			if (_previewCancellation is { IsCancellationRequested: false })
			{
				_previewCancellation.Cancel();
			}

			_previewCancellation = null;
		}

		private void SetPreviewContent(FrameworkElement content)
		{
			content.MouseEnter += (_, _) =>
			{
				_isMouseOverPreview = true;
				_previewHideVersion++;
			};
			content.MouseLeave += (_, _) =>
			{
				_isMouseOverPreview = false;
				BeginHidePreview();
			};
			_previewPopup.Child = content;
		}

		private FrameworkElement CreatePreviewContent(HistoryHoverPreview preview)
		{
			if (preview.Kind == ClipboardHistoryKind.Image)
			{
				return CreateImagePreviewContent(preview);
			}

			return CreateTextPreviewContent(preview.Text);
		}

		private FrameworkElement CreateImagePreviewContent(HistoryHoverPreview preview)
		{
			var stackPanel = new StackPanel
			{
				MaxWidth = HoverPreviewWidth
			};

			if (preview.Image != null)
			{
				var image = new Image
				{
					Source = preview.Image,
					Stretch = Stretch.Uniform,
					MaxWidth = HoverPreviewImageMaxWidth,
					MaxHeight = HoverPreviewImageMaxHeight,
					SnapsToDevicePixels = true
				};
				RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
				stackPanel.Children.Add(image);
			}

			if (!string.IsNullOrWhiteSpace(preview.Text))
			{
				var textBlock = CreatePreviewTextBlock(preview.Text);
				textBlock.Margin = stackPanel.Children.Count == 0 ? new Thickness(0) : new Thickness(0, 8, 0, 0);
				stackPanel.Children.Add(textBlock);
			}

			if (stackPanel.Children.Count == 0)
			{
				stackPanel.Children.Add(CreatePreviewTextBlock("プレビューを表示できません"));
			}

			return CreatePreviewContainer(stackPanel);
		}

		private FrameworkElement CreateTextPreviewContent(string? text)
		{
			var textBlock = CreatePreviewTextBlock(text);
			var scrollViewer = new ScrollViewer
			{
				Content = textBlock,
				Width = HoverPreviewWidth,
				MaxHeight = HoverPreviewMaxHeight,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
			};
			scrollViewer.SetResourceReference(Control.BackgroundProperty, AppTheme.PreviewBackgroundBrushKey);

			return CreatePreviewContainer(scrollViewer);
		}

		private static TextBlock CreatePreviewTextBlock(string? text)
		{
			var textBlock = new TextBlock
			{
				Text = string.IsNullOrWhiteSpace(text) ? "内容を表示できません" : text,
				TextWrapping = TextWrapping.Wrap,
				LineHeight = 18,
				FontSize = 13
			};
			textBlock.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
			return textBlock;
		}

		private static Border CreatePreviewContainer(UIElement child)
		{
			var border = new Border
			{
				Child = child,
				Padding = new Thickness(10),
				MaxWidth = HoverPreviewWidth + 24,
				MaxHeight = HoverPreviewMaxHeight + 24,
				BorderThickness = new Thickness(1),
				SnapsToDevicePixels = true
			};
			border.SetResourceReference(Border.BackgroundProperty, AppTheme.PreviewBackgroundBrushKey);
			border.SetResourceReference(Border.BorderBrushProperty, AppTheme.PreviewBorderBrushKey);
			return border;
		}

		private Grid CreateRootContent(ClipboardHistoryEntry entry)
		{
			var grid = new Grid();
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

			Grid.SetColumn(_selectionCheckBox, 0);
			grid.Children.Add(_selectionCheckBox);

			Grid content = CreateContent(entry);
			Grid.SetColumn(content, 1);
			grid.Children.Add(content);
			return grid;
		}

		private CheckBox CreateSelectionCheckBox()
		{
			var checkBox = new CheckBox
			{
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 0, 10, 0),
				Focusable = false,
				Cursor = Cursors.Hand,
				ToolTip = "選択"
			};
			checkBox.SetResourceReference(Control.ForegroundProperty, AppTheme.TextBrushKey);
			// ButtonBase がマウスイベントを処理するため、チェックボックスのクリックで
			// 履歴の貼り付け(MouseLeftButtonUp -> Activate)は発生しない。
			checkBox.Checked += (_, _) =>
			{
				UpdateVisualState();
				CheckedChanged?.Invoke(this, EventArgs.Empty);
			};
			checkBox.Unchecked += (_, _) =>
			{
				UpdateVisualState();
				CheckedChanged?.Invoke(this, EventArgs.Empty);
			};
			return checkBox;
		}

		private Grid CreateContent(ClipboardHistoryEntry entry)
		{
			var grid = new Grid();
			if (entry.Kind == ClipboardHistoryKind.Image)
			{
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ClipboardHistoryMetadata.ThumbnailLogicalWidth + 12) });
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

				Image? thumbnailImage = null;
				if (entry.Thumbnail != null)
				{
					thumbnailImage = new Image
					{
						Source = entry.Thumbnail,
						Stretch = Stretch.Uniform,
						SnapsToDevicePixels = true
					};
					RenderOptions.SetBitmapScalingMode(thumbnailImage, BitmapScalingMode.HighQuality);
				}

				var thumbnailBox = new Border
				{
					Width = ClipboardHistoryMetadata.ThumbnailLogicalWidth,
					Height = ClipboardHistoryMetadata.ThumbnailLogicalHeight,
					Child = thumbnailImage
				};
				thumbnailBox.SetResourceReference(Border.BackgroundProperty, AppTheme.ThumbnailBackgroundBrushKey);
				Grid.SetColumn(thumbnailBox, 0);
				grid.Children.Add(thumbnailBox);

				var paintButton = CreatePaintButton();
				Grid.SetColumn(paintButton, 2);
				grid.Children.Add(paintButton);
			}
			else
			{
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			}

			var previewLabel = new TextBlock
			{
				Text = entry.PreviewText,
				TextWrapping = TextWrapping.Wrap,
				TextTrimming = TextTrimming.CharacterEllipsis,
				VerticalAlignment = VerticalAlignment.Stretch,
				Margin = entry.Kind == ClipboardHistoryKind.Image ? new Thickness(0, 0, 8, 0) : new Thickness(0)
			};
			previewLabel.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);

			Grid.SetColumn(previewLabel, entry.Kind == ClipboardHistoryKind.Image ? 1 : 0);
			grid.Children.Add(previewLabel);
			return grid;
		}

		private Button CreatePaintButton()
		{
			var button = new Button
			{
				Content = CreatePaintIcon(),
				Width = 40,
				Height = 32,
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Right,
				Focusable = false,
				Cursor = Cursors.Hand
			};
			AppTheme.ApplyButton(button);
			button.Click += (_, e) =>
			{
				e.Handled = true;
				PaintRequested?.Invoke(this, EventArgs.Empty);
			};
			return button;
		}

		private static Viewbox CreatePaintIcon()
		{
			var canvas = new Canvas
			{
				Width = 20,
				Height = 20
			};

			canvas.Children.Add(CreateIconPath("M3.5,5.5 L14.5,5.5 L14.5,16.5 L3.5,16.5 Z", 1.6));
			canvas.Children.Add(CreateIconPath("M6,13.5 C7.5,10.5 10.5,10.5 12,7.5", 1.8));
			canvas.Children.Add(CreateIconPath("M16,3.5 L16,8.5 M13.5,6 L18.5,6", 1.8));

			return new Viewbox
			{
				Child = canvas,
				Width = 18,
				Height = 18,
				Stretch = Stretch.Uniform
			};
		}

		private static ShapePath CreateIconPath(string pathData, double strokeThickness)
		{
			var path = new ShapePath
			{
				Data = Geometry.Parse(pathData),
				Fill = Brushes.Transparent,
				StrokeThickness = strokeThickness,
				StrokeStartLineCap = PenLineCap.Round,
				StrokeEndLineCap = PenLineCap.Round,
				StrokeLineJoin = PenLineJoin.Round,
				SnapsToDevicePixels = true
			};
			path.SetResourceReference(Shape.StrokeProperty, AppTheme.TextBrushKey);
			return path;
		}
	}
}
