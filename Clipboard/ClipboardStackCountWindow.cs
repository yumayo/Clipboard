using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Clipboard;

internal sealed class ClipboardStackCountWindow : Window
{
	private const double BadgeDiameter = 24;
	private const double CursorHorizontalOffset = -32;
	private const double CursorVerticalGap = 4;
	private readonly TextBlock _countText;
	private readonly Border _badgeBorder;
	private readonly NativeMethods.LowLevelMouseProc _mouseProc;
	private IntPtr _handle;
	private IntPtr _mouseHook;
	private bool _isTrackingPosition;
	private NativeMethods.NativePoint? _lastPosition;
	private Point? _cursorBadgeOffset;

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
		_badgeBorder = new Border
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
		_badgeBorder.SetResourceReference(Border.BackgroundProperty, AppTheme.SurfaceBrushKey);
		_badgeBorder.SetResourceReference(Border.BorderBrushProperty, AppTheme.TextBrushKey);
		RenderOptions.SetBitmapScalingMode(_badgeBorder, BitmapScalingMode.HighQuality);
		Content = _badgeBorder;

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
		FitWindowToBadge();
		PositionNearCursor();
	}

	private void FitWindowToBadge()
	{
		if (_cursorBadgeOffset != null)
		{
			return;
		}

		// 余白を取り除く前の描画位置を保存し、マウスとの相対位置を保つ。
		Rect badgeBounds = _badgeBorder.TransformToAncestor(this).TransformBounds(new Rect(_badgeBorder.RenderSize));
		_cursorBadgeOffset = new Point(
			CursorHorizontalOffset + badgeBounds.Left,
			badgeBounds.Top - ActualHeight - CursorVerticalGap);

		SizeToContent = SizeToContent.Manual;
		// OSの最小ウィンドウサイズで余白が増えないよう、上限も丸数字と同じ大きさにする。
		MinWidth = MaxWidth = Width = BadgeDiameter;
		MinHeight = MaxHeight = Height = BadgeDiameter;
		UpdateLayout();
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
		if (_handle == IntPtr.Zero || _cursorBadgeOffset is not { } offset)
		{
			return;
		}

		// Win32の画面座標で配置し、負の座標や異なるDPIのモニターにも追従する。
		Matrix transform = HwndSource.FromHwnd(_handle)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
		int x = cursor.X + (int)Math.Round(offset.X * transform.M11);
		int y = cursor.Y + (int)Math.Round(offset.Y * transform.M22);
		IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
		var info = new NativeMethods.MonitorInfo
		{
			CbSize = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>()
		};
		if (NativeMethods.GetMonitorInfo(monitor, ref info))
		{
			// 透明な余白を除き、丸数字の描画範囲だけで右端・上端を補正する。
			Rect badgeBounds = _badgeBorder.TransformToAncestor(this).TransformBounds(new Rect(_badgeBorder.RenderSize));
			int badgeRightOffset = (int)Math.Ceiling(badgeBounds.Right * transform.M11);
			int badgeTopOffset = (int)Math.Floor(badgeBounds.Top * transform.M22);
			x = Math.Min(x, info.RcMonitor.Right - badgeRightOffset);
			y = Math.Max(y, info.RcMonitor.Top - badgeTopOffset);
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
