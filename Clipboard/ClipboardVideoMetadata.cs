using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Clipboard;

internal static class ClipboardVideoMetadata
{
	private static readonly Guid ImageFactoryId = typeof(IShellItemImageFactory).GUID;
	private static readonly Guid PropertyStoreId = typeof(IPropertyStore).GUID;
	// System.Video.FrameRate: 1/1000 fpsを単位とするVT_UI4。
	private static readonly PropertyKey FrameRateKey = new(new Guid("64440491-4c8b-11d1-8b70-080036b11a03"), 6);

	public static BitmapSource? ReadThumbnail(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}
		IShellItemImageFactory? factory = null;
		IntPtr bitmap = IntPtr.Zero;
		try
		{
			Guid id = ImageFactoryId;
			Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref id, out factory));
			// SIIGBF_THUMBNAILONLY: サムネイルがなければファイルのアイコンで代用しない。
			Marshal.ThrowExceptionForHR(factory.GetImage(new NativeSize(640, 480), 0x8, out bitmap));
			BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero,
				Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			source.Freeze();
			return ClipboardImageAlphaNormalizer.NormalizeInvalidAlpha(source);
		}
		catch (Exception ex) when (ex is COMException or ArgumentException or IOException)
		{
			Logger.Debug($"ClipboardVideoMetadata: 動画サムネイルを取得できませんでした。Path={path} Error={ex.Message}");
			return null;
		}
		finally
		{
			if (bitmap != IntPtr.Zero)
			{
				DeleteObject(bitmap);
			}
			if (factory != null)
			{
				Marshal.ReleaseComObject(factory);
			}
		}
	}

	public static double? ReadFrameRate(string path)
	{
		IPropertyStore? store = null;
		var value = new PropVariant();
		try
		{
			Guid id = PropertyStoreId;
			// GPS_BESTEFFORT: 一部のプロパティを取得できなくても処理を継続する。
			Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0x40, ref id, out store));
			PropertyKey key = FrameRateKey;
			Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
			double fps = value.Type == 19 ? value.UInt32Value / 1000.0 : 0;
			return double.IsFinite(fps) && fps > 0 && fps <= 1000 ? fps : null;
		}
		catch (Exception ex) when (ex is COMException or ArgumentException or IOException)
		{
			Logger.Debug($"ClipboardVideoMetadata: 動画のFPSを取得できませんでした。Path={path} Error={ex.Message}");
			return null;
		}
		finally
		{
			PropVariantClear(ref value);
			if (store != null)
			{
				Marshal.ReleaseComObject(store);
			}
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeSize(int width, int height)
	{
		public int Width = width;
		public int Height = height;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PropertyKey(Guid formatId, uint propertyId)
	{
		public Guid FormatId = formatId;
		public uint PropertyId = propertyId;
	}

	// PROPVARIANTは64bitで24bytes、32bitで16bytes。大きい方を確保する。
	[StructLayout(LayoutKind.Explicit, Size = 24)]
	private struct PropVariant
	{
		[FieldOffset(0)] public ushort Type;
		[FieldOffset(8)] public uint UInt32Value;
	}

	[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItemImageFactory
	{
		[PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
	}

	[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPropertyStore
	{
		[PreserveSig] int GetCount(out uint count);
		[PreserveSig] int GetAt(uint index, out PropertyKey key);
		[PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
		[PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
		[PreserveSig] int Commit();
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, out IShellItemImageFactory factory);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, uint flags, ref Guid interfaceId, out IPropertyStore store);

	[DllImport("ole32.dll", ExactSpelling = true)]
	private static extern int PropVariantClear(ref PropVariant value);

	[DllImport("gdi32.dll", ExactSpelling = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeleteObject(IntPtr bitmap);
}
