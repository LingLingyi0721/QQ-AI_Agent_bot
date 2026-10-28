using System.Runtime.InteropServices;

namespace QQAgent.Gui.Interop;

/// <summary>
/// DWM 外观相关的极小封装：深色标题栏 / 圆角 / Mica。全部失败静默，不影响功能。
/// </summary>
internal static class Win32
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_MAINWINDOW = 2; // Mica

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static bool TrySet(IntPtr hwnd, int attr, int value)
    {
        try
        {
            return DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试给窗口套上深色标题栏、圆角与 Mica 背景。任一步失败都静默降级为纯色背景。
    /// </summary>
    /// <returns>Mica 是否生效（供调用方决定是否保留纯色兜底背景）。</returns>
    public static bool TryApply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        TrySet(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        TrySet(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        return TrySet(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW);
    }
}