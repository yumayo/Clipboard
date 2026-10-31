using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clipboard;

internal sealed class ClipboardStackCountWindow : Window
{
	private readonly TextBlock _countText;
	private readonly DispatcherTimer _positionTimer;
	private IntPtr _handle;

	public ClipboardStackCountWindow()
	{
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
		UseLayoutRounding = true;
		SnapsToDevicePixels = true;

		_countText = new TextBlock
		{
			FontSize = 13,
			FontWeight = FontWeights.SemiBold
		};
		_countText.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		var border = new Border
		{
			CornerRadius = new CornerRadius(6),
			BorderThickness = new Thickness(1),
			Padding = new Thickness(10, 5, 10, 5),
			Child = _countText
		};
		border.SetResourceReference(Border.BackgroundProperty, AppTheme.SurfaceBrushKey);
		border.SetResourceReference(Border.BorderBrushProperty, AppTheme.AccentBorderBrushKey);
		Content = border;

		_positionTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(40)
		};
		_positionTimer.Tick += (_, _) => PositionNearCursor();
		IsVisibleChanged += (_, _) =>
		{
			if (!IsVisible)
			{
				_positionTimer.Stop();
			}
		};
		Closed += (_, _) => _positionTimer.Stop();
	}

	public void ShowCount(int count)
	{
		_countText.Text = $"積み上げ {count} 件";
		if (!IsVisible)
		{
			Show();
		}
		UpdateLayout();
		PositionNearCursor();
		_positionTimer.Start();
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_handle = new WindowInteropHelper(this).Handle;
		int extendedStyle = NativeMethods.GetWindowLong(_handle, NativeMethods.GWL_EXSTYLE);
		NativeMethods.SetWindowLong(_handle, NativeMethods.GWL_EXSTYLE,
			extendedStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW);
	}

	private void PositionNearCursor()
	{
		if (_handle == IntPtr.Zero || !NativeMethods.GetCursorPos(out var cursor))
		{
			return;
		}

		// Win32の画面座標で配置し、負の座標や異なるDPIのモニターにも追従する。
		Matrix transform = HwndSource.FromHwnd(_handle)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
		int width = (int)Math.Ceiling(ActualWidth * transform.M11);
		int height = (int)Math.Ceiling(ActualHeight * transform.M22);
		int x = cursor.X + (int)Math.Round(16 * transform.M11);
		int y = cursor.Y - height - (int)Math.Round(12 * transform.M22);
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

		NativeMethods.SetWindowPos(_handle, new IntPtr(-1), x, y, 0, 0,
			NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
	}
}
