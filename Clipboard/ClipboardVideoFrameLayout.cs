using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Clipboard;

internal readonly record struct ClipboardVideoFrameLayout(int X, int Y, int Width, int Height)
{
	public static bool TryReadAperture(ReadOnlySpan<byte> area, int frameWidth, int frameHeight, out ClipboardVideoFrameLayout layout)
	{
		layout = default;
		// MFVideoArea: MFOffset (16bit小数部 + 16bit整数部) × 2、SIZE (32bit整数) 。
		if (area.Length != 16) return false;
		double x = BinaryPrimitives.ReadInt16LittleEndian(area[2..]) + BinaryPrimitives.ReadUInt16LittleEndian(area) / 65536.0;
		double y = BinaryPrimitives.ReadInt16LittleEndian(area[6..]) + BinaryPrimitives.ReadUInt16LittleEndian(area[4..]) / 65536.0;
		int width = BinaryPrimitives.ReadInt32LittleEndian(area[8..]);
		int height = BinaryPrimitives.ReadInt32LittleEndian(area[12..]);
		if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > frameWidth || y + height > frameHeight) return false;
		int left = (int)Math.Ceiling(x);
		int top = (int)Math.Ceiling(y);
		int right = (int)Math.Floor(x + width);
		int bottom = (int)Math.Floor(y + height);
		if (right <= left || bottom <= top) return false;
		layout = new ClipboardVideoFrameLayout(left, top, right - left, bottom - top);
		return true;
	}

	public byte[] CopyBgr32(IntPtr scanline, int pitch)
	{
		if (scanline == IntPtr.Zero || X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
			Math.Abs((long)pitch) < ((long)X + Width) * 4)
			throw new ArgumentException("動画の画像サイズまたは行幅が不正です。");
		int rowSize = checked(Width * 4);
		byte[] pixels = new byte[checked(rowSize * Height)];
		for (int row = 0; row < Height; row++)
		{
			int offset = checked((Y + row) * pitch + X * 4);
			Marshal.Copy(IntPtr.Add(scanline, offset), pixels, row * rowSize, rowSize);
		}
		return pixels;
	}
}
