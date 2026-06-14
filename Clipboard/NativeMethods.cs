using System.Runtime.InteropServices;

namespace Clipboard;

internal static class NativeMethods
{
	public const int WM_CLIPBOARDUPDATE = 0x031D;
	public const int WM_HOTKEY = 0x0312;
	public const int WM_QUIT = 0x0012;
	public const int WM_APP = 0x8000;
	public const int WM_USER = 0x0400;
	public const int WM_CONTEXTMENU = 0x007B;
	public const int WM_LBUTTONUP = 0x0202;
	public const int WM_RBUTTONUP = 0x0205;
	public const uint PM_NOREMOVE = 0x0000;

	public const int WH_KEYBOARD_LL = 13;
	public const int LLKHF_INJECTED = 0x00000010;
	public const int WM_KEYDOWN = 0x0100;
	public const int WM_KEYUP = 0x0101;
	public const int WM_SYSKEYDOWN = 0x0104;
	public const int WM_SYSKEYUP = 0x0105;
	public const int VK_LCONTROL = 0xA2;
	public const int VK_RCONTROL = 0xA3;
	public const int VK_LWIN = 0x5B;
	public const int VK_RWIN = 0x5C;
	public const int VK_V = 0x56;
	public const int VK_CONTROL = 0x11;
	public const int VK_SHIFT = 0x10;
	public const int VK_MENU = 0x12;
	public const int VK_UP = 0x26;
	public const int VK_DOWN = 0x28;

	public const int HOTKEY_ID_WIN_V = 0x5601;
	public const uint MOD_WIN = 0x0008;
	public const uint MOD_NOREPEAT = 0x4000;

	public const int INPUT_KEYBOARD = 1;
	public const uint KEYEVENTF_KEYUP = 0x0002;
	public const uint FLASHW_STOP = 0;

	public const uint NIM_ADD = 0x00000000;
	public const uint NIM_DELETE = 0x00000002;
	public const uint NIM_SETVERSION = 0x00000004;
	public const uint NIF_MESSAGE = 0x00000001;
	public const uint NIF_ICON = 0x00000002;
	public const uint NIF_TIP = 0x00000004;
	public const uint NOTIFYICON_VERSION_4 = 4;
	public const int NIN_SELECT = WM_USER;
	public const int NIN_KEYSELECT = WM_USER + 1;

	public const uint IMAGE_ICON = 1;
	public const uint LR_LOADFROMFILE = 0x00000010;
	public const uint LR_DEFAULTSIZE = 0x00000040;

	public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
	public const int GWL_STYLE = -16;
	public const int GWL_EXSTYLE = -20;
	public const int WS_MAXIMIZEBOX = 0x00010000;
	public const int WS_MINIMIZEBOX = 0x00020000;
	public const int WS_EX_NOACTIVATE = 0x08000000;
	public const uint SWP_NOSIZE = 0x0001;
	public const uint SWP_NOMOVE = 0x0002;
	public const uint SWP_NOZORDER = 0x0004;
	public const uint SWP_NOACTIVATE = 0x0010;
	public const uint SWP_FRAMECHANGED = 0x0020;

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
		public MouseInput Mi;

		[FieldOffset(0)]
		public KeyboardInput Ki;

		[FieldOffset(0)]
		public HardwareInput Hi;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct MouseInput
	{
		public int Dx;
		public int Dy;
		public uint MouseData;
		public uint DwFlags;
		public uint Time;
		public UIntPtr DwExtraInfo;
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

	[StructLayout(LayoutKind.Sequential)]
	public struct HardwareInput
	{
		public uint UMsg;
		public ushort WParamL;
		public ushort WParamH;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct NativePoint
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct NativeMessage
	{
		public IntPtr HWnd;
		public uint Message;
		public UIntPtr WParam;
		public IntPtr LParam;
		public uint Time;
		public NativePoint Point;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct NativeRect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct GuiThreadInfo
	{
		public uint CbSize;
		public uint Flags;
		public IntPtr HWndActive;
		public IntPtr HWndFocus;
		public IntPtr HWndCapture;
		public IntPtr HWndMenuOwner;
		public IntPtr HWndMoveSize;
		public IntPtr HWndCaret;
		public NativeRect RcCaret;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct FlashWindowInfo
	{
		public uint CbSize;
		public IntPtr HWnd;
		public uint DwFlags;
		public uint UCount;
		public uint DwTimeout;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct NotifyIconData
	{
		public uint CbSize;
		public IntPtr HWnd;
		public uint UID;
		public uint UFlags;
		public uint UCallbackMessage;
		public IntPtr HIcon;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string SzTip;
		public uint DwState;
		public uint DwStateMask;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string SzInfo;
		public uint UTimeoutOrVersion;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string SzInfoTitle;
		public uint DwInfoFlags;
		public Guid GuidItem;
		public IntPtr HBalloonIcon;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct MonitorInfo
	{
		public uint CbSize;
		public NativeRect RcMonitor;
		public NativeRect RcWork;
		public uint DwFlags;
	}

	public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool AddClipboardFormatListener(IntPtr hwnd);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

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

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsWindow(IntPtr hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

	[DllImport("user32.dll")]
	public static extern short GetAsyncKeyState(int vKey);

	[DllImport("kernel32.dll")]
	public static extern uint GetCurrentThreadId();

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool PeekMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool FlashWindowEx(ref FlashWindowInfo pfwi);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo lpgui);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetCursorPos(out NativePoint lpPoint);

	[DllImport("user32.dll")]
	public static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool DestroyIcon(IntPtr hIcon);
}
