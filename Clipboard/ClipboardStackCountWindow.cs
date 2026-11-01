using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Clipboard;

internal sealed class ClipboardStackCountWindow : Window
{
	private const double BadgeDiameter = 24;
	private const double CursorHorizontalOffset = -44;
	private const double CursorVerticalGap = 4;
	private readonly TextBlock _countText;
	private readonly NativeMethods.LowLevelMouseProc _mouseProc;
	private IntPtr _handle;
	private IntPtr _mouseHook;
	private bool _isTrackingPosition;
	private NativeMethods.NativePoint? _lastPosition;

	public ClipboardStackCountWindow()
	{
		_mouseProc = OnMouseHook;
		Title = "クリップボードの積み上げ件数";
		WindowStyle = WindowStyle.None;
		ResizeMode = ResizeMode.NoResize;
		SizeToContent = SizeToContent.WidthAndHeight;
		WindowStartupLocation = WindowStartupLocation.Manual;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		Topmost = true;
		ShowInTaskbar = false;
		ShowActivated = false;
		Focusable = false;
		IsHitTestVisible = false;
		UseLayoutRounding = false;
		SnapsToDevicePixels = false;

		_countText = new TextBlock
		{
			FontSize = 13
		};
		_countText.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		TextOptions.SetTextFormattingMode(_countText, TextFormattingMode.Ideal);
		TextOptions.SetTextRenderingMode(_countText, TextRenderingMode.Grayscale);
		var border = new Border
		{
			Width = BadgeDiameter,
			Height = BadgeDiameter,
			CornerRadius = new CornerRadius(BadgeDiameter / 2),
			BorderThickness = new Thickness(1),
			Padding = new Thickness(3),
			// 丸枠と数字を2倍の解像度で描画して縮小し、輪郭を滑らかにする。
			CacheMode = new BitmapCache
			{
				RenderAtScale = 2,
				EnableClearType = false,
				SnapsToDevicePixels = false
			},
			Child = new Viewbox
			{
				Stretch = Stretch.Uniform,
				StretchDirection = StretchDirection.DownOnly,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Child = _countText
			}
		};
		border.SetResourceReference(Border.BackgroundProperty, AppTheme.SurfaceBrushKey);
		border.SetResourceReference(Border.BorderBrushProperty, AppTheme.TextBrushKey);
		RenderOptions.SetBitmapScalingMode(border, BitmapScalingMode.HighQuality);
		Content = border;

		IsVisibleChanged += (_, _) =>
		{
			if (IsVisible)
			{
				StartPositionTracking();
			}
			else
			{
				StopPositionTracking();
			}
		};
		Closed += (_, _) => StopPositionTracking();
	}

	public void ShowCount(int count)
	{
		_countText.Text = count.ToString();
		if (!IsVisible)
		{
			Show();
		}
		UpdateLayout();
		PositionNearCursor();
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_handle = new WindowInteropHelper(this).Handle;
		int extendedStyle = NativeMethods.GetWindowLong(_handle, NativeMethods.GWL_EXSTYLE);
		NativeMethods.SetWindowLong(_handle, NativeMethods.GWL_EXSTYLE,
			extendedStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW);
	}

	private void StartPositionTracking()
	{
		if (_isTrackingPosition)
		{
			return;
		}

		_lastPosition = null;
		_mouseHook = NativeMethods.SetWindowsHookEx(
			NativeMethods.WH_MOUSE_LL, _mouseProc, NativeMethods.GetModuleHandle(null), 0);
		if (_mouseHook == IntPtr.Zero)
		{
			Logger.Warning($"ClipboardStackCountWindow: マウス移動の監視を開始できないため、描画時の位置更新を使用します。Win32Error={Marshal.GetLastWin32Error()}");
			CompositionTarget.Rendering += OnRendering;
		}
		_isTrackingPosition = true;
	}

	private void StopPositionTracking()
	{
		if (!_isTrackingPosition)
		{
			return;
		}

		_isTrackingPosition = false;
		CompositionTarget.Rendering -= OnRendering;
		if (_mouseHook != IntPtr.Zero)
		{
			if (!NativeMethods.UnhookWindowsHookEx(_mouseHook))
			{
				Logger.Warning($"ClipboardStackCountWindow: マウス移動の監視を解除できませんでした。Win32Error={Marshal.GetLastWin32Error()}");
			}
			_mouseHook = IntPtr.Zero;
		}
		_lastPosition = null;
	}

	private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
	{
		if (nCode >= 0 && wParam == (IntPtr)NativeMethods.WM_MOUSEMOVE && _isTrackingPosition)
		{
			try
			{
				// 通知された移動先をそのまま使い、描画タイミングやDispatcherへの再通知を待たない。
				var mouse = Marshal.PtrToStructure<NativeMethods.MouseLlHookStruct>(lParam);
				PositionNearCursor(mouse.Pt);
			}
			catch (Exception ex)
			{
				Logger.Error(ex, "ClipboardStackCountWindow: マウスへの追従に失敗しました。");
			}
		}

		return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
	}

	private void OnRendering(object? sender, EventArgs e)
	{
		PositionNearCursor();
	}

	private void PositionNearCursor()
	{
		if (NativeMethods.GetCursorPos(out var cursor))
		{
			PositionNearCursor(cursor);
		}
	}

	private void PositionNearCursor(NativeMethods.NativePoint cursor)
	{
		if (_handle == IntPtr.Zero)
		{
			return;
		}

		// Win32の画面座標で配置し、負の座標や異なるDPIのモニターにも追従する。
		Matrix transform = HwndSource.FromHwnd(_handle)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
		int width = (int)Math.Ceiling(ActualWidth * transform.M11);
		int height = (int)Math.Ceiling(ActualHeight * transform.M22);
		int x = cursor.X + (int)Math.Round(CursorHorizontalOffset * transform.M11);
		int y = cursor.Y - height - (int)Math.Round(CursorVerticalGap * transform.M22);
		IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
		var info = new NativeMethods.MonitorInfo
		{
			CbSize = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>()
		};
		if (NativeMethods.GetMonitorInfo(monitor, ref info))
		{
			x = Math.Max(info.RcWork.Left, Math.Min(x, info.RcWork.Right - width));
			y = Math.Max(info.RcWork.Top, Math.Min(y, info.RcWork.Bottom - height));
		}

		if (_lastPosition is { } lastPosition && lastPosition.X == x && lastPosition.Y == y)
		{
			return;
		}

		if (NativeMethods.SetWindowPos(_handle, IntPtr.Zero, x, y, 0, 0,
			NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
		{
			_lastPosition = new NativeMethods.NativePoint { X = x, Y = y };
		}
	}
}
