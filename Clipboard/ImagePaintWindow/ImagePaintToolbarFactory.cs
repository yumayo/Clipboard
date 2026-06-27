using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Polygon = System.Windows.Shapes.Polygon;
using Polyline = System.Windows.Shapes.Polyline;
using Rectangle = System.Windows.Shapes.Rectangle;
using static Clipboard.ImagePaintMetrics;
using static Clipboard.ImagePaintSizeValue;

namespace Clipboard;

internal static class ImagePaintToolbarFactory
{
	internal static Button CreateModeButton(string text, UIElement icon)
	{
		var label = new TextBlock
		{
			Text = text,
			Margin = new Thickness(6, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center
		};
		label.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);

		var content = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		content.Children.Add(icon);
		content.Children.Add(label);

		var button = new Button
		{
			Content = content,
			Width = 124,
			Height = 30,
			Focusable = false
		};
		AppTheme.ApplyButton(button);
		return button;
	}

	internal static StackPanel CreateToolbarTextEditor(string labelText, TextBox textBox)
	{
		var label = new TextBlock
		{
			Text = labelText,
			Margin = new Thickness(12, 0, 6, 0),
			VerticalAlignment = VerticalAlignment.Center
		};
		label.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);

		var editor = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};
		editor.Children.Add(label);
		editor.Children.Add(textBox);
		return editor;
	}

	internal static CheckBox CreateOutlineNumberCheckBox(Action showOutlineNumbers, Action hideOutlineNumbers)
	{
		var checkBox = new CheckBox
		{
			Content = "数字の番号を付ける",
			IsChecked = true,
			Margin = new Thickness(12, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center
		};
		checkBox.SetResourceReference(Control.ForegroundProperty, AppTheme.TextBrushKey);
		checkBox.Checked += (_, _) => showOutlineNumbers();
		checkBox.Unchecked += (_, _) => hideOutlineNumbers();
		return checkBox;
	}

	internal static TextBox CreateToolbarTextBox(string text)
	{
		var textBox = new TextBox
		{
			Text = text,
			Width = 64,
			Height = 30,
			VerticalContentAlignment = VerticalAlignment.Center
		};
		AppTheme.ApplyTextBox(textBox);
		textBox.PreviewTextInput += IntegerTextBox_PreviewTextInput;
		DataObject.AddPastingHandler(textBox, IntegerTextBox_Pasting);
		return textBox;
	}

	internal static Ellipse CreateFillIcon()
	{
		return new Ellipse
		{
			Width = 13,
			Height = 13,
			Fill = Brushes.Black,
			Stroke = Brushes.Black,
			StrokeThickness = 1,
			VerticalAlignment = VerticalAlignment.Center
		};
	}

	internal static Ellipse CreateOutlineIcon()
	{
		return new Ellipse
		{
			Width = 13,
			Height = 13,
			Fill = Brushes.Transparent,
			Stroke = Brushes.Red,
			StrokeThickness = 2,
			VerticalAlignment = VerticalAlignment.Center
		};
	}

	internal static Canvas CreateMoveImageIcon()
	{
		var icon = new Canvas
		{
			Width = 16,
			Height = 16
		};

		var moveArrows = new Polyline
		{
			Stroke = ArrowBrush,
			StrokeThickness = 1.4,
			StrokeLineJoin = PenLineJoin.Round,
			Fill = Brushes.Transparent,
			Points = new PointCollection
			{
				new(8, 0), new(5.5, 2.5), new(10.5, 2.5), new(8, 0),
				new(8, 16), new(5.5, 13.5), new(10.5, 13.5), new(8, 16),
				new(8, 8),
				new(0, 8), new(2.5, 5.5), new(2.5, 10.5), new(0, 8),
				new(16, 8), new(13.5, 5.5), new(13.5, 10.5), new(16, 8)
			}
		};
		icon.Children.Add(moveArrows);
		return icon;
	}

	internal static Canvas CreateArrowTextRectangleIcon()
	{
		var icon = new Canvas
		{
			Width = 18,
			Height = 16
		};

		var arrowLine = new Polyline
		{
			Stroke = ArrowBrush,
			StrokeThickness = 1.6,
			StrokeLineJoin = PenLineJoin.Round,
			Points = new PointCollection
			{
				new(2, 2),
				new(2, 10),
				new(7, 10)
			}
		};
		var arrowHead = new Polygon
		{
			Fill = ArrowBrush,
			Points = new PointCollection
			{
				new(2, 2),
				new(0, 6),
				new(4, 6)
			}
		};
		var rectangle = new Rectangle
		{
			Width = 10,
			Height = 7,
			Fill = Brushes.White,
			Stroke = ArrowBrush,
			StrokeThickness = 1.4
		};
		Canvas.SetLeft(rectangle, 7);
		Canvas.SetTop(rectangle, 7);

		icon.Children.Add(arrowLine);
		icon.Children.Add(arrowHead);
		icon.Children.Add(rectangle);
		return icon;
	}

	internal static void UpdateModeButton(Button button, bool selected)
	{
		button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
		button.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
		button.SetResourceReference(
			Control.BorderBrushProperty,
			selected ? AppTheme.AccentBorderBrushKey : AppTheme.InputBorderBrushKey);
	}

	private static void IntegerTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
	{
		e.Handled = !IsIntegerText(e.Text);
	}

	private static void IntegerTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
	{
		if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) ||
			e.DataObject.GetData(DataFormats.UnicodeText) is not string text ||
			!IsIntegerText(text))
		{
			e.CancelCommand();
		}
	}
}
