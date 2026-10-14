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
	private readonly Grid _contentPanel;

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
		Content = _contentPanel;
	}

	public void ShowContent(byte[] bytes, ClipboardHistoryKind kind, string? plainText)
	{
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
		Activate();
		content.Focus();
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			e.Handled = true;
			Close();
			return;
		}

		base.OnPreviewKeyDown(e);
	}

	private static FrameworkElement CreateTextContent(string text)
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
			FontSize = 15,
			Padding = new Thickness(0),
			BorderThickness = new Thickness(0),
			Template = CreateTextBoxTemplate()
		};
		AppTheme.ApplyTextBox(textBox);
		var scrollViewerStyle = new Style(typeof(ScrollViewer));
		scrollViewerStyle.Setters.Add(new Setter(Control.TemplateProperty, CreateTextScrollViewerTemplate()));
		textBox.Resources[typeof(ScrollViewer)] = scrollViewerStyle;
		textBox.PreviewMouseWheel += TextBox_PreviewMouseWheel;
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

	private static ControlTemplate CreateTextScrollViewerTemplate()
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

	private static FrameworkElement CreateImageContent(byte[] bytes)
	{
		using var stream = new MemoryStream(bytes);
		BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
		BitmapSource source = ClipboardImageAlphaNormalizer.NormalizeInvalidAlpha(decoder.Frames[0]);
		source.Freeze();
		var image = new Image
		{
			Source = source,
			Stretch = Stretch.Uniform,
			SnapsToDevicePixels = true,
			ToolTip = $"{source.PixelWidth} × {source.PixelHeight}"
		};
		RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
		var border = new Border
		{
			Child = image
		};
		border.SetResourceReference(Border.BackgroundProperty, AppTheme.ThumbnailBackgroundBrushKey);
		return border;
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
