using System;
using System.Runtime.InteropServices;

namespace Clipboard;

internal static class NativeMethods
{
	// クリップボード関連
	public const int WM_CLIPBOARDUPDATE = 0x031D;
	public const int WM_HOTKEY = 0x0312;

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool AddClipboardFormatListener(IntPtr hwnd);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

	// キーボードフック関連
	public const int WH_KEYBOARD_LL = 13;
	public const int LLKHF_INJECTED = 0x00000010;
	public const int WM_KEYDOWN = 0x0100;
	public const int WM_KEYUP = 0x0101;
	public const int WM_SYSKEYDOWN = 0x0104;
	public const int WM_SYSKEYUP = 0x0105;
	public const int VK_LCONTROL = 0xA2;  // 162
	public const int VK_RCONTROL = 0xA3;  // 163
	public const int VK_LWIN = 0x5B;
	public const int VK_RWIN = 0x5C;
	public const int VK_V = 0x56;

	public const int HOTKEY_ID_WIN_V = 0x5601;
	public const uint MOD_WIN = 0x0008;
	public const uint MOD_NOREPEAT = 0x4000;

	public const int INPUT_KEYBOARD = 1;
	public const uint KEYEVENTF_KEYUP = 0x0002;

	[StructLayout(LayoutKind.Sequential)]
	public struct KbdLlHookStruct
	{
		public int VkCode;
		public int ScanCode;
		public int Flags;
		public int Time;
		public IntPtr DwExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct Input
	{
		public int Type;
		public InputUnion U;
	}

	[StructLayout(LayoutKind.Explicit)]
	public struct InputUnion
	{
		[FieldOffset(0)]
		public KeyboardInput Ki;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct KeyboardInput
	{
		public ushort WVk;
		public ushort WScan;
		public uint DwFlags;
		public uint Time;
		public UIntPtr DwExtraInfo;
	}

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

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

	[DllImport("user32.dll")]
	public static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsWindow(IntPtr hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
}
