using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScriptsManager.App.Infrastructure;

/// <summary>Мигание кнопки окна на панели задач, пока окно не станут активным.</summary>
public static class TaskbarFlash
{
    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    public static void Flash(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = FlashTray | FlashUntilForeground,
            Count = 5,
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
