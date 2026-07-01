using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shape = System.Windows.Shapes.Shape;
using ShapePath = System.Windows.Shapes.Path;

namespace Clipboard;

internal sealed class ClipboardImageGalleryWindow : Window
{
	private const int GalleryPageSize = 120;
	private const double LoadMoreScrollThreshold = 160;
	private const double DefaultImageSize = 180;
	private const double MinImageSize = 96;
	private const double MaxImageSize = 360;
	private const double ImageSizeStep = 20;
	private const int GalleryDecodeMaxPixelWidth = 900;
	private const int GalleryDecodeMaxPixelHeight = 900;
	private readonly ScrollViewer _scrollViewer;
	private readonly WrapPanel _galleryPanel;
	private readonly Border _selectionFooter;
	private readonly TextBlock _selectionCountLabel;
	private readonly List<ImageGalleryEntry> _imageEntries = new();
	private readonly NativeMethods.LowLevelMouseProc _outsideClickProc;
	private CancellationTokenSource? _loadImagesCancellation;
	private CancellationTokenSource? _loadMoreImagesCancellation;
	private IntPtr _outsideClickHook;
	private IntPtr _targetWindow;
	private IntPtr _windowHandle;
	private IntPtr _suppressedOutsideMouseButtonUpMessage;
	private bool _isLoadingImages;
	private bool _isLoadingMoreImages;
	private bool _hasLoadedImages;
	private bool _hasMoreImages;
	private bool _allowClose;
	private bool _suppressOutsideMouseButtonUp;
	private double _imageSize = DefaultImageSize;

	public ClipboardImageGalleryWindow()
	{
		_outsideClickProc = OnOutsideClickMouseHook;
		Title = "画像履歴";
		WindowStartupLocation = WindowStartupLocation.Manual;
		Width = 1040;
		Height = 680;
		MinWidth = 720;
		MinHeight = 420;
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

		_selectionFooter = CreateSelectionFooter(out _selectionCountLabel);
		DockPanel.SetDock(_selectionFooter, Dock.Bottom);
		rootPanel.Children.Add(_selectionFooter);

		_galleryPanel = new WrapPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(10)
		};

		_scrollViewer = new ScrollViewer
		{
			Content = _galleryPanel,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};
		_scrollViewer.SetResourceReference(Control.BackgroundProperty, AppTheme.WindowBackgroundBrushKey);
		_scrollViewer.ScrollChanged += (_, _) => TryBeginLoadMoreImages();
		_scrollViewer.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
		rootPanel.Children.Add(_scrollViewer);

		Content = rootPanel;
		IsVisibleChanged += (_, _) =>
		{
			if (!IsVisible)
			{
				if (!_suppressOutsideMouseButtonUp)
				{
					StopDismissOnOutsideClick();
				}

				CancelImageLoad();
			}

			VisibleStateChanged?.Invoke(IsVisible);
		};
	}

	public event Action<bool>? VisibleStateChanged;

	public bool IsClosed { get; private set; }

	public bool IsForegroundWindow()
	{
		return _windowHandle != IntPtr.Zero && NativeMethods.GetForegroundWindow() == _windowHandle;
	}

	public void ShowGallery(IntPtr targetWindow)
	{
		_targetWindow = targetWindow;
		PositionOnCurrentMonitor();
		Topmost = true;
		Show();
		Topmost = false;
		StopWindowFlash();
		Dispatcher.BeginInvoke((Action)StopWindowFlash);
		BeginDismissOnOutsideClick();

		if (_isLoadingImages && _loadImagesCancellation?.IsCancellationRequested == true)
		{
			_isLoadingImages = false;
		}

		if (!_isLoadingImages)
		{
			BeginLoadImages(_hasLoadedImages && _galleryPanel.Children.Count > 0);
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

	public void HideFromKeyboard()
	{
		if (IsVisible)
		{
			Hide();
		}
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			Hide();
			e.Handled = true;
			return;
		}

		base.OnPreviewKeyDown(e);
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
		CancelImageLoad();
		IsClosed = true;
		base.OnClosing(e);
	}

	private Border CreateSelectionFooter(out TextBlock countLabel)
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

		var paintButton = new Button
		{
			Content = "ペイント",
			MinHeight = 32,
			Padding = new Thickness(12, 5, 12, 5),
			Margin = new Thickness(0, 0, 8, 0),
			Focusable = false,
			Cursor = Cursors.Hand,
			ToolTip = "選択した画像をペイントで開きます"
		};
		AppTheme.ApplyButton(paintButton);
		paintButton.Click += (_, _) => OpenSelectedPaintWindow();
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
		deleteButton.Click += (_, _) => DeleteCheckedItems();
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
		int checkedCount = GetGalleryItems().Count(item => item.IsChecked);
		if (checkedCount == 0)
		{
			_selectionFooter.Visibility = Visibility.Collapsed;
			return;
		}

		_selectionCountLabel.Text = $"{checkedCount}件を選択中";
		_selectionFooter.Visibility = Visibility.Visible;
	}

	private async void BeginLoadImages(bool preserveExistingItems)
	{
		CancelImageLoad();
		_loadMoreImagesCancellation = null;
		_isLoadingMoreImages = false;
		_hasMoreImages = false;
		var cancellationTokenSource = new CancellationTokenSource();
		_loadImagesCancellation = cancellationTokenSource;
		_isLoadingImages = true;

		if (!preserveExistingItems)
		{
			ClearGalleryControls();
			AddMessage("読み込み中...");
		}

		List<ImageGalleryEntry> entries;
		try
		{
			entries = await Task.Run(
				() => LoadImageEntries(null, GalleryPageSize, cancellationTokenSource.Token),
				cancellationTokenSource.Token);
		}
		catch (OperationCanceledException)
		{
			if (_loadImagesCancellation == cancellationTokenSource)
			{
				_isLoadingImages = false;
			}

			return;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardImageGalleryWindow: 画像履歴の読み込みに失敗しました。");
			if (_loadImagesCancellation == cancellationTokenSource)
			{
				_isLoadingImages = false;
				if (!preserveExistingItems)
				{
					ClearGalleryControls();
					AddMessage("画像履歴を読み込めませんでした");
				}
			}

			return;
		}

		if (cancellationTokenSource.IsCancellationRequested || _loadImagesCancellation != cancellationTokenSource)
		{
			if (_loadImagesCancellation == cancellationTokenSource)
			{
				_isLoadingImages = false;
			}

			return;
		}

		_isLoadingImages = false;
		_hasLoadedImages = true;
		PopulateImages(entries);
	}

	private void PopulateImages(List<ImageGalleryEntry> entries)
	{
		_imageEntries.Clear();
		_imageEntries.AddRange(entries);
		_hasMoreImages = entries.Count >= GalleryPageSize;
		ClearGalleryControls();
		if (entries.Count == 0)
		{
			AddMessage("画像履歴がありません");
			return;
		}

		AddImageEntries(entries);
	}

	private void TryBeginLoadMoreImages()
	{
		if (!IsVisible ||
			!_hasLoadedImages ||
			_isLoadingImages ||
			_isLoadingMoreImages ||
			!_hasMoreImages ||
			_imageEntries.Count == 0)
		{
			return;
		}

		if (_scrollViewer.ScrollableHeight <= 0 || _scrollViewer.VerticalOffset <= 0)
		{
			return;
		}

		if (_scrollViewer.VerticalOffset < Math.Max(0, _scrollViewer.ScrollableHeight - LoadMoreScrollThreshold))
		{
			return;
		}

		BeginLoadMoreImages();
	}

	private async void BeginLoadMoreImages()
	{
		if (_imageEntries.Count == 0 || _isLoadingMoreImages || !_hasMoreImages)
		{
			return;
		}

		var lastEntry = _imageEntries[^1];
		var cancellationTokenSource = new CancellationTokenSource();
		_loadMoreImagesCancellation = cancellationTokenSource;
		_isLoadingMoreImages = true;

		List<ImageGalleryEntry> entries;
		try
		{
			entries = await Task.Run(
				() => LoadImageEntries(lastEntry.Id, GalleryPageSize, cancellationTokenSource.Token),
				cancellationTokenSource.Token);
		}
		catch (OperationCanceledException)
		{
			if (_loadMoreImagesCancellation == cancellationTokenSource)
			{
				_isLoadingMoreImages = false;
			}

			return;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardImageGalleryWindow: 追加画像履歴の読み込みに失敗しました。");
			if (_loadMoreImagesCancellation == cancellationTokenSource)
			{
				_isLoadingMoreImages = false;
			}

			return;
		}

		if (cancellationTokenSource.IsCancellationRequested || _loadMoreImagesCancellation != cancellationTokenSource)
		{
			if (_loadMoreImagesCancellation == cancellationTokenSource)
			{
				_isLoadingMoreImages = false;
			}

			return;
		}

		_isLoadingMoreImages = false;
		_hasMoreImages = entries.Count >= GalleryPageSize;
		if (entries.Count == 0)
		{
			return;
		}

		_imageEntries.AddRange(entries);
		AddImageEntries(entries);
	}

	private void AddImageEntries(List<ImageGalleryEntry> entries)
	{
		RemoveMessageControls();
		foreach (ImageGalleryEntry entry in entries)
		{
			var item = new ImageGalleryItemControl(entry, _imageSize);
			item.Activated += (_, _) => PasteEntry(entry);
			item.PaintRequested += (_, _) => OpenImagePaintWindow(entry);
			item.CheckedChanged += (_, _) => UpdateSelectionFooter();
			_galleryPanel.Children.Add(item);
		}
	}

	private void PasteEntry(ImageGalleryEntry entry)
	{
		Hide();
		ClipboardManager.PasteHistoryEntry(entry.Id, _targetWindow);
	}

	private async void OpenImagePaintWindow(ImageGalleryEntry entry)
	{
		try
		{
			ClipboardStoredContent? content = await Task.Run(() => ClipboardDatabase.LoadContent(entry.Id));
			if (content == null || content.Kind != ClipboardHistoryKind.Image || content.Bytes.Length == 0)
			{
				throw new InvalidOperationException("画像履歴を読み込めませんでした。");
			}

			Hide();
			ImagePaintWindow paintWindow = CreateImagePaintWindow(content);
			paintWindow.Show();
			paintWindow.Activate();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, $"ClipboardImageGalleryWindow: ペイント画面を開けませんでした。Id={entry.Id}");
			MessageBox.Show(this, "ペイント画面を開けませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private async void OpenSelectedPaintWindow()
	{
		var imageIds = GetGalleryItems()
			.Where(item => item.IsChecked)
			.Select(item => item.EntryId)
			.ToList();
		if (imageIds.Count == 0)
		{
			UpdateSelectionFooter();
			return;
		}

		try
		{
			if (imageIds.Count == 1)
			{
				ClipboardStoredContent? content = await Task.Run(() => ClipboardDatabase.LoadContent(imageIds[0]));
				if (content == null || content.Kind != ClipboardHistoryKind.Image || content.Bytes.Length == 0)
				{
					throw new InvalidOperationException("画像履歴を読み込めませんでした。");
				}

				Hide();
				ImagePaintWindow paintWindow = CreateImagePaintWindow(content);
				paintWindow.Show();
				paintWindow.Activate();
				return;
			}

			List<byte[]> imageBytesList = await Task.Run(() => LoadImageContents(imageIds));
			if (imageBytesList.Count == 0)
			{
				throw new InvalidOperationException("画像履歴を読み込めませんでした。");
			}

			Hide();
			var multiPaintWindow = new ImagePaintWindow(imageBytesList);
			multiPaintWindow.Show();
			multiPaintWindow.Activate();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardImageGalleryWindow: 選択画像のペイント画面を開けませんでした。");
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
				Logger.Warning($"ClipboardImageGalleryWindow: 保存されたペイント状態の復元に失敗したため画像として開きます。Error={ex.Message}");
			}
		}

		return new ImagePaintWindow(content.Bytes);
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

	private void DeleteCheckedItems()
	{
		var checkedItems = GetGalleryItems().Where(item => item.IsChecked).ToList();
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
			Logger.Error(ex, "ClipboardImageGalleryWindow: 選択した画像履歴の削除に失敗しました。");
			MessageBox.Show(this, "選択した画像を削除できませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
			return;
		}

		var checkedIdSet = checkedIds.ToHashSet();
		_imageEntries.RemoveAll(entry => checkedIdSet.Contains(entry.Id));
		foreach (ImageGalleryItemControl item in checkedItems)
		{
			_galleryPanel.Children.Remove(item);
		}

		UpdateSelectionFooter();
		if (!GetGalleryItems().Any())
		{
			if (_hasMoreImages)
			{
				BeginLoadImages(preserveExistingItems: false);
			}
			else
			{
				AddMessage("画像履歴がありません");
			}
		}
	}

	private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
		{
			return;
		}

		double nextSize = _imageSize + (e.Delta > 0 ? ImageSizeStep : -ImageSizeStep);
		nextSize = Math.Max(MinImageSize, Math.Min(MaxImageSize, nextSize));
		if (Math.Abs(nextSize - _imageSize) < 0.1)
		{
			e.Handled = true;
			return;
		}

		_imageSize = nextSize;
		foreach (ImageGalleryItemControl item in GetGalleryItems())
		{
			item.SetImageSize(_imageSize);
		}

		_galleryPanel.InvalidateMeasure();
		e.Handled = true;
	}

	private void AddMessage(string text)
	{
		RemoveMessageControls();
		var message = new GalleryMessageControl
		{
			Text = text,
			Width = 360,
			Height = 80,
			TextAlignment = TextAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		message.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.MutedTextBrushKey);
		_galleryPanel.Children.Add(message);
	}

	private void RemoveMessageControls()
	{
		for (int i = _galleryPanel.Children.Count - 1; i >= 0; i--)
		{
			if (_galleryPanel.Children[i] is GalleryMessageControl)
			{
				_galleryPanel.Children.RemoveAt(i);
			}
		}
	}

	private void ClearGalleryControls()
	{
		_galleryPanel.Children.Clear();
		_scrollViewer.ScrollToTop();
		UpdateSelectionFooter();
	}

	private List<ImageGalleryItemControl> GetGalleryItems()
	{
		return _galleryPanel.Children.OfType<ImageGalleryItemControl>().ToList();
	}

	private void CancelImageLoad()
	{
		if (_loadImagesCancellation is { IsCancellationRequested: false })
		{
			_loadImagesCancellation.Cancel();
		}

		if (_loadMoreImagesCancellation is { IsCancellationRequested: false })
		{
			_loadMoreImagesCancellation.Cancel();
		}
	}

	private void BeginDismissOnOutsideClick()
	{
		StopDismissOnOutsideClick();
		_outsideClickHook = SetOutsideClickHook(_outsideClickProc);
		if (_outsideClickHook == IntPtr.Zero)
		{
			Logger.Warning($"ClipboardImageGalleryWindow: 外側クリック監視の開始に失敗しました。Win32Error={Marshal.GetLastWin32Error()}");
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
			Logger.Warning($"ClipboardImageGalleryWindow: 外側クリック監視の解除に失敗しました。Win32Error={Marshal.GetLastWin32Error()}");
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

		IntPtr windowHandle = GetWindowHandle();
		IntPtr windowAtPoint = NativeMethods.WindowFromPoint(point);
		if (IsSameRootWindow(windowAtPoint, windowHandle))
		{
			return false;
		}

		outsideWindow = windowAtPoint;
		return true;
	}

	private void HideFromOutsideClick(NativeMethods.NativePoint point, IntPtr clickedWindow)
	{
		if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
		{
			return;
		}

		IntPtr windowHandle = GetWindowHandle();
		Logger.Debug($"ClipboardImageGalleryWindow: ギャラリー外がクリックされたため閉じます。Point=({point.X},{point.Y}) Window={FormatHandle(windowHandle)} ClickedWindow={FormatHandle(clickedWindow)}");
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

	private void PositionOnCurrentMonitor()
	{
		Point cursorPoint = GetCursorPoint();
		Rect workingArea = TryGetWorkingAreaDevice(cursorPoint, out Rect workingAreaDevice)
			? TransformRect(workingAreaDevice, GetTransformFromDevice())
			: SystemParameters.WorkArea;

		double maxWidth = Math.Max(320, workingArea.Width - 48);
		double maxHeight = Math.Max(240, workingArea.Height - 72);
		double windowWidth = Math.Min(ActualWidth > 0 ? ActualWidth : Width, maxWidth);
		double windowHeight = Math.Min(ActualHeight > 0 ? ActualHeight : Height, maxHeight);

		Width = windowWidth;
		Height = windowHeight;
		Left = workingArea.Left + Math.Max(0, (workingArea.Width - windowWidth) / 2);
		Top = workingArea.Top + Math.Max(0, (workingArea.Height - windowHeight) / 2);
		Logger.Debug($"ClipboardImageGalleryWindow: ギャラリーの表示位置を決定しました。WorkingArea={workingArea} Location=({Left},{Top}) Size=({windowWidth},{windowHeight})");
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
				CbSize = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>()
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

	private void StopWindowFlash()
	{
		IntPtr handle = new WindowInteropHelper(this).Handle;
		if (handle == IntPtr.Zero)
		{
			return;
		}

		var flashWindowInfo = new NativeMethods.FlashWindowInfo
		{
			CbSize = (uint)Marshal.SizeOf<NativeMethods.FlashWindowInfo>(),
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

	private static List<ImageGalleryEntry> LoadImageEntries(
		long? beforeId,
		int maxEntryCount,
		CancellationToken cancellationToken)
	{
		try
		{
			return ClipboardDatabase.LoadImageHistorySummaries(beforeId, maxEntryCount, cancellationToken)
				.Select(CreateImageEntry)
				.ToList();
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardImageGalleryWindow: 画像履歴の読み込みに失敗しました。");
			return new List<ImageGalleryEntry>();
		}
	}

	private static ImageGalleryEntry CreateImageEntry(ClipboardHistorySummary summary)
	{
		return new ImageGalleryEntry
		{
			Id = summary.Id,
			CreatedAt = summary.CreatedAt,
			PreviewText = summary.PreviewText,
			Thumbnail = CreateThumbnail(summary.ThumbnailBytes) ?? CreateThumbnailFromContent(summary.Id)
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

	private static ImageSource? CreateThumbnailFromContent(long historyId)
	{
		try
		{
			ClipboardStoredContent? content = ClipboardDatabase.LoadContent(historyId);
			if (content == null || content.Kind != ClipboardHistoryKind.Image)
			{
				return null;
			}

			return CreateDecodedImage(content.Bytes, GalleryDecodeMaxPixelWidth, GalleryDecodeMaxPixelHeight);
		}
		catch (Exception ex)
		{
			Logger.Debug($"ClipboardImageGalleryWindow: 画像サムネイルの生成に失敗しました。Id={historyId} Error={ex.Message}");
			return null;
		}
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

	private static string FormatHandle(IntPtr handle)
	{
		return $"0x{handle.ToInt64():X}";
	}

	private sealed class ImageGalleryEntry
	{
		public required long Id { get; init; }
		public required DateTime CreatedAt { get; init; }
		public required string PreviewText { get; init; }
		public ImageSource? Thumbnail { get; init; }
	}

	private sealed class GalleryMessageControl : TextBlock
	{
	}

	private sealed class ImageGalleryItemControl : Border
	{
		private readonly ImageGalleryEntry _entry;
		private readonly CheckBox _selectionCheckBox;
		private bool _isMouseInside;

		public ImageGalleryItemControl(ImageGalleryEntry entry, double imageSize)
		{
			_entry = entry;
			Margin = new Thickness(0, 0, 10, 10);
			BorderThickness = new Thickness(1);
			Cursor = Cursors.Hand;
			Focusable = false;
			ToolTip = entry.PreviewText;
			_selectionCheckBox = CreateSelectionCheckBox();
			Child = CreateContent(entry);
			SetImageSize(imageSize);
			UpdateVisualState();

			MouseEnter += (_, _) =>
			{
				_isMouseInside = true;
				UpdateVisualState();
			};
			MouseLeave += (_, _) =>
			{
				_isMouseInside = false;
				UpdateVisualState();
			};
			MouseLeftButtonUp += (_, e) => OnItemClicked(e);
		}

		public event EventHandler? Activated;
		public event EventHandler? PaintRequested;
		public event EventHandler? CheckedChanged;

		public long EntryId => _entry.Id;

		public bool IsChecked => _selectionCheckBox.IsChecked == true;

		public void SetImageSize(double imageSize)
		{
			Width = imageSize;
			Height = imageSize;
		}

		private void OnItemClicked(MouseButtonEventArgs e)
		{
			if (e.Handled)
			{
				return;
			}

			Activated?.Invoke(this, EventArgs.Empty);
			e.Handled = true;
		}

		private Grid CreateContent(ImageGalleryEntry entry)
		{
			var grid = new Grid();
			var imageHost = new Border
			{
				Child = CreateImage(entry.Thumbnail),
				SnapsToDevicePixels = true
			};
			imageHost.SetResourceReference(Border.BackgroundProperty, AppTheme.ThumbnailBackgroundBrushKey);
			grid.Children.Add(imageHost);

			Grid.SetZIndex(_selectionCheckBox, 1);
			grid.Children.Add(_selectionCheckBox);

			var paintButton = CreatePaintButton();
			Grid.SetZIndex(paintButton, 1);
			grid.Children.Add(paintButton);

			return grid;
		}

		private static Image? CreateImage(ImageSource? source)
		{
			if (source == null)
			{
				return null;
			}

			var image = new Image
			{
				Source = source,
				Stretch = Stretch.Uniform,
				SnapsToDevicePixels = true
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
			return image;
		}

		private CheckBox CreateSelectionCheckBox()
		{
			var checkBox = new CheckBox
			{
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(6),
				Focusable = false,
				Cursor = Cursors.Hand,
				ToolTip = "選択"
			};
			checkBox.SetResourceReference(Control.ForegroundProperty, AppTheme.TextBrushKey);
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

		private Button CreatePaintButton()
		{
			var button = new Button
			{
				Content = CreatePaintIcon(),
				Width = 34,
				Height = 30,
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(6),
				Focusable = false,
				Cursor = Cursors.Hand,
				ToolTip = "ペイント"
			};
			AppTheme.ApplyButton(button);
			button.Click += (_, e) =>
			{
				e.Handled = true;
				PaintRequested?.Invoke(this, EventArgs.Empty);
			};
			return button;
		}

		private void UpdateVisualState()
		{
			SetResourceReference(
				Border.BackgroundProperty,
				IsChecked ? AppTheme.SurfaceSelectedBrushKey : _isMouseInside ? AppTheme.SurfaceHoverBrushKey : AppTheme.SurfaceBrushKey);
			SetResourceReference(
				Border.BorderBrushProperty,
				IsChecked || _isMouseInside ? AppTheme.AccentBorderBrushKey : AppTheme.BorderBrushKey);
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
