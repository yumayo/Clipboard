using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Clipboard;

internal enum AppColorTheme
{
	Light,
	Dark
}

internal static class AppTheme
{
	public const string WindowBackgroundBrushKey = "AppTheme.WindowBackgroundBrush";
	public const string SurfaceBrushKey = "AppTheme.SurfaceBrush";
	public const string SurfaceHoverBrushKey = "AppTheme.SurfaceHoverBrush";
	public const string SurfaceSelectedBrushKey = "AppTheme.SurfaceSelectedBrush";
	public const string BorderBrushKey = "AppTheme.BorderBrush";
	public const string AccentBorderBrushKey = "AppTheme.AccentBorderBrush";
	public const string TextBrushKey = "AppTheme.TextBrush";
	public const string MutedTextBrushKey = "AppTheme.MutedTextBrush";
	public const string InputBackgroundBrushKey = "AppTheme.InputBackgroundBrush";
	public const string InputBorderBrushKey = "AppTheme.InputBorderBrush";
	public const string ButtonBackgroundBrushKey = "AppTheme.ButtonBackgroundBrush";
	public const string ButtonHoverBackgroundBrushKey = "AppTheme.ButtonHoverBackgroundBrush";
	public const string ButtonPressedBackgroundBrushKey = "AppTheme.ButtonPressedBackgroundBrush";
	public const string ButtonDisabledBackgroundBrushKey = "AppTheme.ButtonDisabledBackgroundBrush";
	public const string DisabledTextBrushKey = "AppTheme.DisabledTextBrush";
	public const string PreviewBackgroundBrushKey = "AppTheme.PreviewBackgroundBrush";
	public const string PreviewBorderBrushKey = "AppTheme.PreviewBorderBrush";
	public const string ThumbnailBackgroundBrushKey = "AppTheme.ThumbnailBackgroundBrush";
	public const string SelectionBrushKey = "AppTheme.SelectionBrush";
	public const string ScrollBarTrackBrushKey = "AppTheme.ScrollBarTrackBrush";
	public const string ScrollBarThumbBrushKey = "AppTheme.ScrollBarThumbBrush";
	public const string ScrollBarThumbHoverBrushKey = "AppTheme.ScrollBarThumbHoverBrush";
	public const string ScrollBarThumbPressedBrushKey = "AppTheme.ScrollBarThumbPressedBrush";
	public const string ButtonStyleKey = "AppTheme.ButtonStyle";

	private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
	private const string AppsUseLightThemeValueName = "AppsUseLightTheme";
	private static readonly Palette LightPalette = new(
		WindowBackground: ColorValue(245, 246, 248),
		Surface: ColorValue(255, 255, 255),
		SurfaceHover: ColorValue(232, 240, 254),
		SurfaceSelected: ColorValue(220, 232, 255),
		Border: ColorValue(216, 220, 226),
		AccentBorder: ColorValue(70, 116, 218),
		Text: ColorValue(48, 48, 48),
		MutedText: ColorValue(96, 96, 96),
		InputBackground: ColorValue(255, 255, 255),
		InputBorder: ColorValue(188, 194, 204),
		ButtonBackground: ColorValue(248, 249, 251),
		ButtonHoverBackground: ColorValue(238, 242, 248),
		ButtonPressedBackground: ColorValue(225, 230, 239),
		ButtonDisabledBackground: ColorValue(238, 239, 242),
		DisabledText: ColorValue(142, 148, 158),
		PreviewBackground: ColorValue(255, 255, 255),
		PreviewBorder: ColorValue(176, 184, 198),
		ThumbnailBackground: ColorValue(238, 238, 238),
		Selection: ColorValue(92, 142, 255),
		ScrollBarTrack: ColorValue(245, 246, 248),
		ScrollBarThumb: ColorValue(194, 200, 210),
		ScrollBarThumbHover: ColorValue(170, 178, 192),
		ScrollBarThumbPressed: ColorValue(142, 152, 170),
		MenuBackground: DrawingColor(255, 255, 255),
		MenuHoverBackground: DrawingColor(232, 240, 254),
		MenuText: DrawingColor(32, 32, 32),
		MenuBorder: DrawingColor(216, 220, 226));
	private static readonly Palette DarkPalette = new(
		WindowBackground: ColorValue(30, 31, 34),
		Surface: ColorValue(43, 45, 49),
		SurfaceHover: ColorValue(53, 58, 66),
		SurfaceSelected: ColorValue(45, 63, 99),
		Border: ColorValue(70, 74, 82),
		AccentBorder: ColorValue(143, 177, 255),
		Text: ColorValue(241, 243, 246),
		MutedText: ColorValue(181, 186, 196),
		InputBackground: ColorValue(37, 39, 43),
		InputBorder: ColorValue(91, 96, 106),
		ButtonBackground: ColorValue(48, 50, 54),
		ButtonHoverBackground: ColorValue(59, 63, 70),
		ButtonPressedBackground: ColorValue(37, 39, 43),
		ButtonDisabledBackground: ColorValue(39, 41, 45),
		DisabledText: ColorValue(122, 127, 137),
		PreviewBackground: ColorValue(37, 39, 43),
		PreviewBorder: ColorValue(91, 96, 106),
		ThumbnailBackground: ColorValue(24, 26, 29),
		Selection: ColorValue(73, 111, 184),
		ScrollBarTrack: ColorValue(30, 31, 34),
		ScrollBarThumb: ColorValue(78, 83, 92),
		ScrollBarThumbHover: ColorValue(102, 108, 120),
		ScrollBarThumbPressed: ColorValue(130, 137, 151),
		MenuBackground: DrawingColor(32, 33, 36),
		MenuHoverBackground: DrawingColor(53, 58, 66),
		MenuText: DrawingColor(241, 243, 246),
		MenuBorder: DrawingColor(70, 74, 82));

	private static AppColorTheme _current = ReadOsTheme();
	private static bool _isInitialized;

	public static event EventHandler? ThemeChanged;

	public static AppColorTheme Current => _current;

	public static bool IsDark => _current == AppColorTheme.Dark;

	public static void Initialize(Application application)
	{
		if (_isInitialized)
		{
			return;
		}

		_isInitialized = true;
		ApplyResources(application.Resources, _current);
		SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
		application.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
	}

	public static void ApplyWindow(Window window)
	{
		window.SetResourceReference(Window.BackgroundProperty, WindowBackgroundBrushKey);
		window.SetResourceReference(Control.ForegroundProperty, TextBrushKey);
		window.SourceInitialized += (_, _) => ApplyTitleBar(window);
		ApplyTitleBar(window);
	}

	public static void ApplyTextBox(TextBox textBox)
	{
		textBox.SetResourceReference(Control.ForegroundProperty, TextBrushKey);
		textBox.SetResourceReference(Control.BackgroundProperty, InputBackgroundBrushKey);
		textBox.SetResourceReference(Control.BorderBrushProperty, InputBorderBrushKey);
		textBox.SetResourceReference(TextBoxBase.CaretBrushProperty, TextBrushKey);
		textBox.SetResourceReference(TextBoxBase.SelectionBrushProperty, SelectionBrushKey);
	}

	public static void ApplyButton(Button button)
	{
		button.SetResourceReference(FrameworkElement.StyleProperty, ButtonStyleKey);
	}

	public static void ApplyProgressBar(ProgressBar progressBar)
	{
		progressBar.SetResourceReference(Control.ForegroundProperty, AccentBorderBrushKey);
		progressBar.SetResourceReference(Control.BackgroundProperty, ButtonPressedBackgroundBrushKey);
	}

	public static void ApplyContextMenu(WinForms.ContextMenuStrip menu)
	{
		Palette palette = CurrentPalette;
		menu.BackColor = palette.MenuBackground;
		menu.ForeColor = palette.MenuText;
		menu.Renderer = IsDark
			? new WinForms.ToolStripProfessionalRenderer(new DarkToolStripColorTable(palette))
			: new WinForms.ToolStripProfessionalRenderer();

		foreach (WinForms.ToolStripItem item in menu.Items)
		{
			item.BackColor = palette.MenuBackground;
			item.ForeColor = palette.MenuText;
		}
	}

	public static void ApplyTitleBar(Window window)
	{
		IntPtr handle = new WindowInteropHelper(window).Handle;
		if (handle == IntPtr.Zero)
		{
			return;
		}

		try
		{
			int value = IsDark ? 1 : 0;
			int result = NativeMethods.DwmSetWindowAttribute(
				handle,
				NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
				ref value,
				sizeof(int));
			if (result != 0)
			{
				NativeMethods.DwmSetWindowAttribute(
					handle,
					NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1,
					ref value,
					sizeof(int));
			}
		}
		catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
		{
			Logger.Debug($"AppTheme: タイトルバーのテーマ適用をスキップしました。{ex.Message}");
		}
	}

	private static Palette CurrentPalette => _current == AppColorTheme.Dark ? DarkPalette : LightPalette;

	private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
	{
		if (e.Category != UserPreferenceCategory.Color &&
			e.Category != UserPreferenceCategory.General &&
			e.Category != UserPreferenceCategory.VisualStyle &&
			e.Category != UserPreferenceCategory.Window)
		{
			return;
		}

		Application? application = Application.Current;
		if (application == null)
		{
			return;
		}

		application.Dispatcher.BeginInvoke((Action)(() =>
		{
			AppColorTheme theme = ReadOsTheme();
			if (theme == _current)
			{
				return;
			}

			_current = theme;
			ApplyResources(application.Resources, _current);
			foreach (Window window in application.Windows)
			{
				ApplyTitleBar(window);
			}

			ThemeChanged?.Invoke(null, EventArgs.Empty);
		}));
	}

	private static void ApplyResources(ResourceDictionary resources, AppColorTheme theme)
	{
		Palette palette = theme == AppColorTheme.Dark ? DarkPalette : LightPalette;
		resources[WindowBackgroundBrushKey] = CreateBrush(palette.WindowBackground);
		resources[SurfaceBrushKey] = CreateBrush(palette.Surface);
		resources[SurfaceHoverBrushKey] = CreateBrush(palette.SurfaceHover);
		resources[SurfaceSelectedBrushKey] = CreateBrush(palette.SurfaceSelected);
		resources[BorderBrushKey] = CreateBrush(palette.Border);
		resources[AccentBorderBrushKey] = CreateBrush(palette.AccentBorder);
		resources[TextBrushKey] = CreateBrush(palette.Text);
		resources[MutedTextBrushKey] = CreateBrush(palette.MutedText);
		resources[InputBackgroundBrushKey] = CreateBrush(palette.InputBackground);
		resources[InputBorderBrushKey] = CreateBrush(palette.InputBorder);
		resources[ButtonBackgroundBrushKey] = CreateBrush(palette.ButtonBackground);
		resources[ButtonHoverBackgroundBrushKey] = CreateBrush(palette.ButtonHoverBackground);
		resources[ButtonPressedBackgroundBrushKey] = CreateBrush(palette.ButtonPressedBackground);
		resources[ButtonDisabledBackgroundBrushKey] = CreateBrush(palette.ButtonDisabledBackground);
		resources[DisabledTextBrushKey] = CreateBrush(palette.DisabledText);
		resources[PreviewBackgroundBrushKey] = CreateBrush(palette.PreviewBackground);
		resources[PreviewBorderBrushKey] = CreateBrush(palette.PreviewBorder);
		resources[ThumbnailBackgroundBrushKey] = CreateBrush(palette.ThumbnailBackground);
		resources[SelectionBrushKey] = CreateBrush(palette.Selection);
		resources[ScrollBarTrackBrushKey] = CreateBrush(palette.ScrollBarTrack);
		resources[ScrollBarThumbBrushKey] = CreateBrush(palette.ScrollBarThumb);
		resources[ScrollBarThumbHoverBrushKey] = CreateBrush(palette.ScrollBarThumbHover);
		resources[ScrollBarThumbPressedBrushKey] = CreateBrush(palette.ScrollBarThumbPressed);
		resources[ButtonStyleKey] = CreateButtonStyle();
		resources[typeof(ScrollBar)] = CreateSafeScrollBarStyle();
	}

	private static Style CreateSafeScrollBarStyle()
	{
		try
		{
			return CreateScrollBarStyle();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "AppTheme: スクロールバーのテーマ作成に失敗したため簡易スタイルを使用します。");
			return CreateFallbackScrollBarStyle();
		}
	}

	private static Style CreateButtonStyle()
	{
		var style = new Style(typeof(Button));
		style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(TextBrushKey)));
		style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ButtonBackgroundBrushKey)));
		style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(InputBorderBrushKey)));
		style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
		style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
		style.Setters.Add(new Setter(Control.TemplateProperty, CreateButtonTemplate()));

		var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
		hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ButtonHoverBackgroundBrushKey)));
		style.Triggers.Add(hoverTrigger);

		var pressedTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
		pressedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ButtonPressedBackgroundBrushKey)));
		style.Triggers.Add(pressedTrigger);

		var focusedTrigger = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
		focusedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(AccentBorderBrushKey)));
		style.Triggers.Add(focusedTrigger);

		var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
		disabledTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(DisabledTextBrushKey)));
		disabledTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ButtonDisabledBackgroundBrushKey)));
		style.Triggers.Add(disabledTrigger);

		return style;
	}

	private static ControlTemplate CreateButtonTemplate()
	{
		var border = new FrameworkElementFactory(typeof(Border));
		border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
		border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
		border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
		border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
		border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

		var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
		presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
		presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
		presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
		border.AppendChild(presenter);

		return new ControlTemplate(typeof(Button))
		{
			VisualTree = border
		};
	}

	private static Style CreateScrollBarStyle()
	{
		var style = new Style(typeof(ScrollBar));
		style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ScrollBarTrackBrushKey)));
		style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ScrollBarThumbBrushKey)));
		style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));

		var verticalTrigger = new Trigger { Property = ScrollBar.OrientationProperty, Value = Orientation.Vertical };
		verticalTrigger.Setters.Add(new Setter(FrameworkElement.WidthProperty, 12.0));
		verticalTrigger.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 12.0));
		verticalTrigger.Setters.Add(new Setter(Control.TemplateProperty, CreateScrollBarTemplate(Orientation.Vertical)));
		style.Triggers.Add(verticalTrigger);

		var horizontalTrigger = new Trigger { Property = ScrollBar.OrientationProperty, Value = Orientation.Horizontal };
		horizontalTrigger.Setters.Add(new Setter(FrameworkElement.HeightProperty, 12.0));
		horizontalTrigger.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 12.0));
		horizontalTrigger.Setters.Add(new Setter(Control.TemplateProperty, CreateScrollBarTemplate(Orientation.Horizontal)));
		style.Triggers.Add(horizontalTrigger);

		return style;
	}

	private static Style CreateFallbackScrollBarStyle()
	{
		var style = new Style(typeof(ScrollBar));
		style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ScrollBarTrackBrushKey)));
		style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ScrollBarThumbBrushKey)));
		style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));

		var verticalTrigger = new Trigger { Property = ScrollBar.OrientationProperty, Value = Orientation.Vertical };
		verticalTrigger.Setters.Add(new Setter(FrameworkElement.WidthProperty, 12.0));
		verticalTrigger.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 12.0));
		style.Triggers.Add(verticalTrigger);

		var horizontalTrigger = new Trigger { Property = ScrollBar.OrientationProperty, Value = Orientation.Horizontal };
		horizontalTrigger.Setters.Add(new Setter(FrameworkElement.HeightProperty, 12.0));
		horizontalTrigger.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 12.0));
		style.Triggers.Add(horizontalTrigger);

		return style;
	}

	private static ControlTemplate CreateScrollBarTemplate(Orientation orientation)
	{
		return orientation == Orientation.Vertical
			? CreateVerticalScrollBarTemplate()
			: CreateHorizontalScrollBarTemplate();
	}

	private static ControlTemplate CreateVerticalScrollBarTemplate()
	{
		return (ControlTemplate)XamlReader.Parse(
			"""
			<ControlTemplate
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
				xmlns:primitives="clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework"
				TargetType="{x:Type primitives:ScrollBar}">
				<Grid Background="{DynamicResource AppTheme.ScrollBarTrackBrush}" SnapsToDevicePixels="True">
					<primitives:Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True" Margin="2,0,2,0">
						<primitives:Track.DecreaseRepeatButton>
							<primitives:RepeatButton Command="{x:Static primitives:ScrollBar.PageUpCommand}" Focusable="False">
								<primitives:RepeatButton.Template>
									<ControlTemplate TargetType="{x:Type primitives:RepeatButton}">
										<Border Background="{DynamicResource AppTheme.ScrollBarTrackBrush}" />
									</ControlTemplate>
								</primitives:RepeatButton.Template>
							</primitives:RepeatButton>
						</primitives:Track.DecreaseRepeatButton>
						<primitives:Track.IncreaseRepeatButton>
							<primitives:RepeatButton Command="{x:Static primitives:ScrollBar.PageDownCommand}" Focusable="False">
								<primitives:RepeatButton.Template>
									<ControlTemplate TargetType="{x:Type primitives:RepeatButton}">
										<Border Background="{DynamicResource AppTheme.ScrollBarTrackBrush}" />
									</ControlTemplate>
								</primitives:RepeatButton.Template>
							</primitives:RepeatButton>
						</primitives:Track.IncreaseRepeatButton>
						<primitives:Track.Thumb>
							<primitives:Thumb MinWidth="8" MinHeight="32">
								<primitives:Thumb.Style>
									<Style TargetType="{x:Type primitives:Thumb}">
										<Setter Property="Background" Value="{DynamicResource AppTheme.ScrollBarThumbBrush}" />
										<Setter Property="Template">
											<Setter.Value>
												<ControlTemplate TargetType="{x:Type primitives:Thumb}">
													<Border Background="{TemplateBinding Background}" CornerRadius="4" SnapsToDevicePixels="True" />
												</ControlTemplate>
											</Setter.Value>
										</Setter>
										<Style.Triggers>
											<Trigger Property="IsMouseOver" Value="True">
												<Setter Property="Background" Value="{DynamicResource AppTheme.ScrollBarThumbHoverBrush}" />
											</Trigger>
											<Trigger Property="IsDragging" Value="True">
												<Setter Property="Background" Value="{DynamicResource AppTheme.ScrollBarThumbPressedBrush}" />
											</Trigger>
										</Style.Triggers>
									</Style>
								</primitives:Thumb.Style>
							</primitives:Thumb>
						</primitives:Track.Thumb>
					</primitives:Track>
				</Grid>
			</ControlTemplate>
			""");
	}

	private static ControlTemplate CreateHorizontalScrollBarTemplate()
	{
		return (ControlTemplate)XamlReader.Parse(
			"""
			<ControlTemplate
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
				xmlns:primitives="clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework"
				TargetType="{x:Type primitives:ScrollBar}">
				<Grid Background="{DynamicResource AppTheme.ScrollBarTrackBrush}" SnapsToDevicePixels="True">
					<primitives:Track x:Name="PART_Track" Orientation="Horizontal" Margin="0,2,0,2">
						<primitives:Track.DecreaseRepeatButton>
							<primitives:RepeatButton Command="{x:Static primitives:ScrollBar.PageLeftCommand}" Focusable="False">
								<primitives:RepeatButton.Template>
									<ControlTemplate TargetType="{x:Type primitives:RepeatButton}">
										<Border Background="{DynamicResource AppTheme.ScrollBarTrackBrush}" />
									</ControlTemplate>
								</primitives:RepeatButton.Template>
							</primitives:RepeatButton>
						</primitives:Track.DecreaseRepeatButton>
						<primitives:Track.IncreaseRepeatButton>
							<primitives:RepeatButton Command="{x:Static primitives:ScrollBar.PageRightCommand}" Focusable="False">
								<primitives:RepeatButton.Template>
									<ControlTemplate TargetType="{x:Type primitives:RepeatButton}">
										<Border Background="{DynamicResource AppTheme.ScrollBarTrackBrush}" />
									</ControlTemplate>
								</primitives:RepeatButton.Template>
							</primitives:RepeatButton>
						</primitives:Track.IncreaseRepeatButton>
						<primitives:Track.Thumb>
							<primitives:Thumb MinWidth="32" MinHeight="8">
								<primitives:Thumb.Style>
									<Style TargetType="{x:Type primitives:Thumb}">
										<Setter Property="Background" Value="{DynamicResource AppTheme.ScrollBarThumbBrush}" />
										<Setter Property="Template">
											<Setter.Value>
												<ControlTemplate TargetType="{x:Type primitives:Thumb}">
													<Border Background="{TemplateBinding Background}" CornerRadius="4" SnapsToDevicePixels="True" />
												</ControlTemplate>
											</Setter.Value>
										</Setter>
										<Style.Triggers>
											<Trigger Property="IsMouseOver" Value="True">
												<Setter Property="Background" Value="{DynamicResource AppTheme.ScrollBarThumbHoverBrush}" />
											</Trigger>
											<Trigger Property="IsDragging" Value="True">
												<Setter Property="Background" Value="{DynamicResource AppTheme.ScrollBarThumbPressedBrush}" />
											</Trigger>
										</Style.Triggers>
									</Style>
								</primitives:Thumb.Style>
							</primitives:Thumb>
						</primitives:Track.Thumb>
					</primitives:Track>
				</Grid>
			</ControlTemplate>
			""");
	}

	private static AppColorTheme ReadOsTheme()
	{
		try
		{
			object? value = Registry.CurrentUser
				.OpenSubKey(PersonalizeRegistryPath, writable: false)
				?.GetValue(AppsUseLightThemeValueName);
			return value is int appsUseLightTheme && appsUseLightTheme == 0
				? AppColorTheme.Dark
				: AppColorTheme.Light;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
		{
			Logger.Debug($"AppTheme: OSテーマの取得に失敗したためライトテーマを使用します。{ex.Message}");
			return AppColorTheme.Light;
		}
	}

	private static SolidColorBrush CreateBrush(System.Windows.Media.Color color)
	{
		var brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private static System.Windows.Media.Color ColorValue(byte red, byte green, byte blue)
	{
		return System.Windows.Media.Color.FromRgb(red, green, blue);
	}

	private static Drawing.Color DrawingColor(byte red, byte green, byte blue)
	{
		return Drawing.Color.FromArgb(red, green, blue);
	}

	private sealed record Palette(
		System.Windows.Media.Color WindowBackground,
		System.Windows.Media.Color Surface,
		System.Windows.Media.Color SurfaceHover,
		System.Windows.Media.Color SurfaceSelected,
		System.Windows.Media.Color Border,
		System.Windows.Media.Color AccentBorder,
		System.Windows.Media.Color Text,
		System.Windows.Media.Color MutedText,
		System.Windows.Media.Color InputBackground,
		System.Windows.Media.Color InputBorder,
		System.Windows.Media.Color ButtonBackground,
		System.Windows.Media.Color ButtonHoverBackground,
		System.Windows.Media.Color ButtonPressedBackground,
		System.Windows.Media.Color ButtonDisabledBackground,
		System.Windows.Media.Color DisabledText,
		System.Windows.Media.Color PreviewBackground,
		System.Windows.Media.Color PreviewBorder,
		System.Windows.Media.Color ThumbnailBackground,
		System.Windows.Media.Color Selection,
		System.Windows.Media.Color ScrollBarTrack,
		System.Windows.Media.Color ScrollBarThumb,
		System.Windows.Media.Color ScrollBarThumbHover,
		System.Windows.Media.Color ScrollBarThumbPressed,
		Drawing.Color MenuBackground,
		Drawing.Color MenuHoverBackground,
		Drawing.Color MenuText,
		Drawing.Color MenuBorder);

	private sealed class DarkToolStripColorTable : WinForms.ProfessionalColorTable
	{
		private readonly Palette _palette;

		public DarkToolStripColorTable(Palette palette)
		{
			_palette = palette;
		}

		public override Drawing.Color ToolStripDropDownBackground => _palette.MenuBackground;
		public override Drawing.Color ImageMarginGradientBegin => _palette.MenuBackground;
		public override Drawing.Color ImageMarginGradientMiddle => _palette.MenuBackground;
		public override Drawing.Color ImageMarginGradientEnd => _palette.MenuBackground;
		public override Drawing.Color MenuBorder => _palette.MenuBorder;
		public override Drawing.Color MenuItemBorder => _palette.AccentBorder.ToDrawingColor();
		public override Drawing.Color MenuItemSelected => _palette.MenuHoverBackground;
		public override Drawing.Color MenuItemSelectedGradientBegin => _palette.MenuHoverBackground;
		public override Drawing.Color MenuItemSelectedGradientEnd => _palette.MenuHoverBackground;
		public override Drawing.Color MenuItemPressedGradientBegin => _palette.MenuHoverBackground;
		public override Drawing.Color MenuItemPressedGradientMiddle => _palette.MenuHoverBackground;
		public override Drawing.Color MenuItemPressedGradientEnd => _palette.MenuHoverBackground;
		public override Drawing.Color SeparatorDark => _palette.MenuBorder;
		public override Drawing.Color SeparatorLight => _palette.MenuBorder;
	}

	private static Drawing.Color ToDrawingColor(this System.Windows.Media.Color color)
	{
		return Drawing.Color.FromArgb(color.R, color.G, color.B);
	}
}
