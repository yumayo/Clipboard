using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clipboard;

internal sealed class ClipboardPreviewWindow : Window
{
	private const double BaseTextFontSize = 15;
	private const double MinZoom = 0.1;
	private const double MaxZoom = 10;
	private const double ZoomStep = 1.1;
	private readonly Grid _contentPanel;
	private TextBox? _textBox;
	private Image? _image;
	private ScrollViewer? _imageScrollViewer;
	private double _zoom = 1;
	private bool _fitImageToWindow;

	public ClipboardPreviewWindow()
	{
		Title = "クリップボードの内容";
		Width = 720;
		Height = 520;
		MinWidth = 320;
		MinHeight = 220;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Topmost = true;
		ShowInTaskbar = false;
		UseLayoutRounding = true;
		SnapsToDevicePixels = true;
		Icon = LoadIcon();
		AppTheme.ApplyWindow(this);

		_contentPanel = new Grid();
		_contentPanel.SizeChanged += (_, _) => FitImageToWindow();
		Content = _contentPanel;
	}

	public void ShowContent(byte[] bytes, ClipboardHistoryKind kind, string? plainText)
	{
		_textBox = null;
		_image = null;
		_imageScrollViewer = null;
		_zoom = 1;
		_fitImageToWindow = kind == ClipboardHistoryKind.Image;
		FrameworkElement content = kind switch
		{
			ClipboardHistoryKind.Image => CreateImageContent(bytes),
			ClipboardHistoryKind.Text or ClipboardHistoryKind.Html or ClipboardHistoryKind.Rtf =>
				CreateTextContent(GetDisplayText(bytes, kind, plainText)),
			_ => CreateMessageContent("クリップボードが空か、表示できない形式です。")
		};
		_contentPanel.Children.Clear();
		_contentPanel.Children.Add(content);

		if (WindowState == WindowState.Minimized)
		{
			WindowState = WindowState.Normal;
		}

		Show();
		FitImageToWindow();
		Activate();
		content.Focus();
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.D0 || e.Key == Key.NumPad0))
		{
			SetZoom(1);
			e.Handled = true;
			return;
		}

		if (e.Key == Key.Escape)
		{
			e.Handled = true;
			Close();
			return;
		}

		base.OnPreviewKeyDown(e);
	}

	protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
	{
		if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (_textBox != null || _image != null))
		{
			ScrollViewer? scrollViewer = GetContentScrollViewer();
			Point? anchor = scrollViewer == null ? null : e.GetPosition(scrollViewer);
			SetZoom(_zoom * Math.Pow(ZoomStep, e.Delta / 120.0), anchor);
			e.Handled = true;
			return;
		}

		base.OnPreviewMouseWheel(e);
	}

	private void SetZoom(double zoom, Point? anchor = null)
	{
		if (_textBox == null && _image == null)
		{
			return;
		}

		_fitImageToWindow = false;
		// 初期の画像フィット率が範囲外でも、ホイールの方向と逆に拡縮しないようにする。
		double nextZoom = Math.Clamp(zoom, Math.Min(MinZoom, _zoom), Math.Max(MaxZoom, _zoom));
		if (Math.Abs(nextZoom - _zoom) < 0.0001)
		{
			return;
		}

		ScrollViewer? scrollViewer = GetContentScrollViewer();
		Point anchorPoint = anchor ?? new Point(
			(scrollViewer?.ViewportWidth ?? 0) / 2,
			(scrollViewer?.ViewportHeight ?? 0) / 2);
		double zoomRatio = nextZoom / _zoom;
		double horizontalOffset = ((scrollViewer?.HorizontalOffset ?? 0) + anchorPoint.X) * zoomRatio - anchorPoint.X;
		double verticalOffset = ((scrollViewer?.VerticalOffset ?? 0) + anchorPoint.Y) * zoomRatio - anchorPoint.Y;

		_zoom = nextZoom;
		UpdateZoomedContent();
		if (scrollViewer != null)
		{
			scrollViewer.UpdateLayout();
			scrollViewer.ScrollToHorizontalOffset(Math.Clamp(horizontalOffset, 0, scrollViewer.ScrollableWidth));
			scrollViewer.ScrollToVerticalOffset(Math.Clamp(verticalOffset, 0, scrollViewer.ScrollableHeight));
		}
	}

	private ScrollViewer? GetContentScrollViewer()
	{
		return _imageScrollViewer ?? _textBox?.Template.FindName("PART_ContentHost", _textBox) as ScrollViewer;
	}

	private void FitImageToWindow()
	{
		if (!_fitImageToWindow || _image?.Source is not BitmapSource source ||
			_contentPanel.ActualWidth <= 0 || _contentPanel.ActualHeight <= 0)
		{
			return;
		}

		_zoom = Math.Min(_contentPanel.ActualWidth / source.Width, _contentPanel.ActualHeight / source.Height);
		UpdateZoomedContent();
	}

	private void UpdateZoomedContent()
	{
		if (_textBox != null)
		{
			_textBox.FontSize = BaseTextFontSize * _zoom;
		}

		if (_image?.Source is BitmapSource source)
		{
			_image.Width = source.Width * _zoom;
			_image.Height = source.Height * _zoom;
		}
	}

	private FrameworkElement CreateTextContent(string text)
	{
		if (text.Length == 0)
		{
			return CreateMessageContent("クリップボードに表示する内容がありません。");
		}

		var textBox = new TextBox
		{
			Text = text,
			IsReadOnly = true,
			AcceptsReturn = true,
			AcceptsTab = true,
			TextWrapping = TextWrapping.NoWrap,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			FontSize = BaseTextFontSize,
			Padding = new Thickness(0),
			BorderThickness = new Thickness(0),
			Template = CreateTextBoxTemplate()
		};
		AppTheme.ApplyTextBox(textBox);
		ApplyScrollViewerStyle(textBox);
		textBox.PreviewMouseWheel += TextBox_PreviewMouseWheel;
		_textBox = textBox;
		return textBox;
	}

	private static ControlTemplate CreateTextBoxTemplate()
	{
		return (ControlTemplate)XamlReader.Parse(
			"""
			<ControlTemplate
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
				TargetType="{x:Type TextBox}">
				<ScrollViewer x:Name="PART_ContentHost"
					Background="{TemplateBinding Background}"
					Focusable="False"
					CanContentScroll="True"
					HorizontalScrollBarVisibility="{TemplateBinding HorizontalScrollBarVisibility}"
					VerticalScrollBarVisibility="{TemplateBinding VerticalScrollBarVisibility}" />
			</ControlTemplate>
			""");
	}

	private static void TextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || sender is not TextBox textBox)
		{
			return;
		}

		textBox.ScrollToHorizontalOffset(Math.Max(0, textBox.HorizontalOffset - e.Delta));
		e.Handled = true;
	}

	private static void ApplyScrollViewerStyle(FrameworkElement element)
	{
		var style = new Style(typeof(ScrollViewer));
		style.Setters.Add(new Setter(Control.TemplateProperty, CreateScrollViewerTemplate()));
		element.Resources[typeof(ScrollViewer)] = style;
	}

	private static ControlTemplate CreateScrollViewerTemplate()
	{
		// 標準テンプレートの右下の交差部分はOSのコントロール色を使うため、
		// スクロールバーと同じアプリのテーマ色を明示する。
		return (ControlTemplate)XamlReader.Parse(
			"""
			<ControlTemplate
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
				TargetType="{x:Type ScrollViewer}">
				<Grid Background="{TemplateBinding Background}" SnapsToDevicePixels="True">
					<Grid.ColumnDefinitions>
						<ColumnDefinition Width="*" />
						<ColumnDefinition Width="Auto" />
					</Grid.ColumnDefinitions>
					<Grid.RowDefinitions>
						<RowDefinition Height="*" />
						<RowDefinition Height="Auto" />
					</Grid.RowDefinitions>
					<ScrollContentPresenter x:Name="PART_ScrollContentPresenter"
						Margin="{TemplateBinding Padding}"
						Content="{TemplateBinding Content}"
						ContentTemplate="{TemplateBinding ContentTemplate}"
						CanContentScroll="{TemplateBinding CanContentScroll}" />
					<ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1"
						Orientation="Vertical"
						Value="{TemplateBinding VerticalOffset}"
						Maximum="{TemplateBinding ScrollableHeight}"
						ViewportSize="{TemplateBinding ViewportHeight}"
						Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}" />
					<ScrollBar x:Name="PART_HorizontalScrollBar" Grid.Row="1"
						Orientation="Horizontal"
						Value="{TemplateBinding HorizontalOffset}"
						Maximum="{TemplateBinding ScrollableWidth}"
						ViewportSize="{TemplateBinding ViewportWidth}"
						Visibility="{TemplateBinding ComputedHorizontalScrollBarVisibility}" />
					<Border Grid.Column="1" Grid.Row="1"
						Background="{DynamicResource AppTheme.ScrollBarTrackBrush}" />
				</Grid>
			</ControlTemplate>
			""");
	}

	private FrameworkElement CreateImageContent(byte[] bytes)
	{
		using var stream = new MemoryStream(bytes);
		BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
		BitmapSource source = ClipboardImageAlphaNormalizer.NormalizeInvalidAlpha(decoder.Frames[0]);
		source.Freeze();
		var image = new Image
		{
			Source = source,
			Width = source.Width,
			Height = source.Height,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Stretch = Stretch.Uniform,
			SnapsToDevicePixels = true,
			ToolTip = $"{source.PixelWidth} × {source.PixelHeight}"
		};
		RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
		var scrollViewer = new ScrollViewer
		{
			Content = image,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Focusable = true
		};
		scrollViewer.SetResourceReference(Control.BackgroundProperty, AppTheme.ThumbnailBackgroundBrushKey);
		ApplyScrollViewerStyle(scrollViewer);
		scrollViewer.PreviewMouseWheel += ImageScrollViewer_PreviewMouseWheel;
		_image = image;
		_imageScrollViewer = scrollViewer;
		return scrollViewer;
	}

	private static void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || sender is not ScrollViewer scrollViewer)
		{
			return;
		}

		scrollViewer.ScrollToHorizontalOffset(Math.Max(0, scrollViewer.HorizontalOffset - e.Delta));
		e.Handled = true;
	}

	private static string GetDisplayText(byte[] bytes, ClipboardHistoryKind kind, string? plainText)
	{
		if (plainText != null)
		{
			return plainText;
		}

		string text = Encoding.UTF8.GetString(bytes);
		if (kind == ClipboardHistoryKind.Html)
		{
			return ClipboardHtmlTextConverter.FallbackConvertToPlainText(text);
		}

		if (kind == ClipboardHistoryKind.Rtf)
		{
			var document = new FlowDocument();
			var range = new TextRange(document.ContentStart, document.ContentEnd);
			using var stream = new MemoryStream(bytes);
			range.Load(stream, DataFormats.Rtf);
			return range.Text;
		}

		return text;
	}

	private static FrameworkElement CreateMessageContent(string message)
	{
		var label = new TextBlock
		{
			Text = message,
			TextWrapping = TextWrapping.Wrap,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(12)
		};
		label.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.MutedTextBrushKey);
		return label;
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
}
