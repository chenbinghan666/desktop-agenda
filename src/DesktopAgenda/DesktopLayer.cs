using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

internal sealed class DesktopLayer : IDisposable
{
    private const int ExStyle = -20;
    private const int Style = -16;
    private const long ToolWindow = 0x80, AppWindow = 0x40000, Topmost = 0x8;
    private const long MinimizeBox = 0x20000;
    private const uint PositionOnly = 0x13;
    private const uint Above = 3, Below = 2;
    private static readonly IntPtr Bottom = new(1), TopmostBand = new(-1);
    private readonly Window widget;
    private readonly IntPtr handle;
    private readonly HwndSource reference;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(75) };
    private readonly ForegroundCallback foregroundCallback;
    private readonly IntPtr foregroundHook;
    private bool onShownDesktop;
    internal bool DialogOpen { get; set; }

    internal DesktopLayer(Window widget)
    {
        this.widget = widget;
        handle = new WindowInteropHelper(widget).Handle;
        long styles = GetWindowLongPtr(handle, ExStyle).ToInt64();
        SetWindowLongPtr(handle, ExStyle, new IntPtr((styles | ToolWindow) & ~AppWindow));
        long normalStyles = GetWindowLongPtr(handle, Style).ToInt64();
        SetWindowLongPtr(handle, Style, new IntPtr(normalStyles & ~MinimizeBox));
        int enabled = 1;
        DwmSetWindowAttribute(handle, 12, ref enabled, sizeof(int));
        reference = new HwndSource(new HwndSourceParameters("Desktop Agenda z-order reference")
        {
            WindowStyle = unchecked((int)0x88000000),
            ExtendedWindowStyle = (int)ToolWindow,
            Width = 1, Height = 1
        });
        Place(reference.Handle, Bottom);
        foregroundCallback = (_, _, _, _, _, _, _) => Update(false);
        // Out-of-context callbacks arrive on the UI thread that registered the hook.
        foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundCallback, 0, 0, 2);
        timer.Tick += (_, _) => Update(false);
        timer.Start();
    }

    internal void Lower() => Update(true);

    private void Update(bool force)
    {
        if (DialogOpen) return;
        var shell = DesktopIconsHost();
        // Explorer raises the desktop icons host over ordinary windows when showing the desktop.
        // The hidden reference stays in the normal band and distinguishes that from ordinary focus changes.
        bool showing = shell != IntPtr.Zero && IsWindowVisible(shell) && IsAboveReference(shell);
        if (!force && showing == onShownDesktop) return;
        onShownDesktop = showing;
        if (!showing)
        {
            Place(handle, Bottom);
            return;
        }
        if (widget.WindowState == WindowState.Minimized) widget.WindowState = WindowState.Normal;
        var previous = GetWindow(shell, Above);
        while (previous == handle) previous = GetWindow(previous, Above);
        if (previous != IntPtr.Zero && (GetWindowLongPtr(previous, ExStyle).ToInt64() & Topmost) != 0)
            Place(handle, previous);
        else
            Place(handle, TopmostBand);
    }

    private bool IsAboveReference(IntPtr shell)
    {
        for (var window = GetWindow(shell, Below); window != IntPtr.Zero; window = GetWindow(window, Below))
            if (window == reference.Handle) return true;
        return false;
    }

    private static IntPtr DesktopIconsHost()
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero || FindWindowEx(shell, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            return shell;
        for (var worker = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "WorkerW", null); worker != IntPtr.Zero;
             worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null))
            if (FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return worker;
        return shell;
    }

    private static void Place(IntPtr window, IntPtr after) =>
        SetWindowPos(window, after, 0, 0, 0, 0, PositionOnly);

    public void Dispose()
    {
        timer.Stop();
        if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
        reference.Dispose();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    private delegate void ForegroundCallback(IntPtr hook, uint eventId, IntPtr window,
        int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module,
        ForegroundCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
