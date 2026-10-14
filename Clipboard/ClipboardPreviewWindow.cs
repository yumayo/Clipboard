using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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
		AppTheme.ApplyWindow(this);

		var root = new DockPanel { Margin = new Thickness(12) };
		var hint = new TextBlock
		{
			Text = "Windows + C で更新  /  Esc で閉じる",
			Margin = new Thickness(0, 10, 0, 0)
		};
		hint.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.MutedTextBrushKey);
		DockPanel.SetDock(hint, Dock.Bottom);
		root.Children.Add(hint);

		_contentPanel = new Grid();
		root.Children.Add(_contentPanel);
		Content = root;
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
			Padding = new Thickness(10)
		};
		AppTheme.ApplyTextBox(textBox);
		return textBox;
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
			Child = image,
			Padding = new Thickness(10),
			BorderThickness = new Thickness(1)
		};
		border.SetResourceReference(Border.BackgroundProperty, AppTheme.ThumbnailBackgroundBrushKey);
		border.SetResourceReference(Border.BorderBrushProperty, AppTheme.BorderBrushKey);
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
}
