using System.Drawing;
using System.Runtime.InteropServices;
namespace BmwBenchmarkAutomator;

public static class Interop
{
    /// Физические пиксели для захвата экрана и координат кликов (иначе на масштабе 125/150% клики «поедут»).
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
}

public static class MouseHelper
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint LEFTDOWN = 0x0002;
    private const uint LEFTUP = 0x0004;

    public static void LeftClick(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(150);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(70);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
}

public static class ScreenCapture
{
    public static Bitmap Capture()
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var bmp = new Bitmap(b.Width, b.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size);
        return bmp;
    }
}

