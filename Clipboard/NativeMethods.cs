using System;
using System.Runtime.InteropServices;

namespace Clipboard;

internal static class NativeMethods
{
	// クリップボード関連
	public const int WM_CLIPBOARDUPDATE = 0x031D;

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool AddClipboardFormatListener(IntPtr hwnd);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

	// キーボードフック関連
	public const int WH_KEYBOARD_LL = 13;
	public const int WM_KEYDOWN = 0x0100;
	public const int WM_KEYUP = 0x0101;
	public const int VK_LCONTROL = 0xA2;  // 162
	public const int VK_RCONTROL = 0xA3;  // 163

	public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool UnhookWindowsHookEx(IntPtr hhk);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	public static extern IntPtr GetModuleHandle(string? lpModuleName);
}
