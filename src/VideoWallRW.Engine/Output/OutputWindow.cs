using System.Runtime.InteropServices;
using Serilog;

namespace VideoWallRW.Engine.Output;

/// <summary>
/// Janela de saída borderless em Win32 puro (nunca WPF, nunca exclusive-fullscreen).
/// Criar, destruir e bombear mensagens SEMPRE na render thread.
/// </summary>
public sealed class OutputWindow : IDisposable
{
    private const string WindowClassName = "VideoWallRWOutputWindow";

    // O delegate precisa ficar vivo enquanto a classe da janela existir.
    private static readonly WndProcDelegate s_wndProc = WindowProc;
    private static bool s_classRegistered;

    public nint Hwnd { get; private set; }
    public MonitorInfo Monitor { get; }

    public OutputWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        RegisterWindowClassOnce();
        Hwnd = CreateWindowExW(0, WindowClassName, $"VideoWallRW — {monitor.DeviceName}",
            WS_POPUP, monitor.X, monitor.Y, monitor.Width, monitor.Height,
            0, 0, GetModuleHandleW(null), 0);
        if (Hwnd == 0)
            throw new InvalidOperationException(
                $"CreateWindowExW falhou para {monitor.DeviceName} (erro {Marshal.GetLastPInvokeError()})");
        // SW_SHOWNA: mostra sem roubar o foco do operador.
        ShowWindow(Hwnd, SW_SHOWNA);
    }

    /// <summary>Bombeia as mensagens da thread atual. Chamar 1x por frame na render thread.</summary>
    public static void PumpMessages()
    {
        while (PeekMessageW(out NativeMessage msg, 0, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    public void Dispose()
    {
        if (Hwnd != 0)
        {
            DestroyWindow(Hwnd);
            Hwnd = 0;
        }
    }

    private static void RegisterWindowClassOnce()
    {
        if (s_classRegistered)
            return;
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = WindowClassName,
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassExW falhou (erro {Marshal.GetLastPInvokeError()})");
        s_classRegistered = true;
    }

    private static nint WindowProc(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_CLOSE:
                // Saídas só fecham por comando do engine, nunca por Alt+F4/close.
                Log.Debug("WM_CLOSE ignorado na janela de saída");
                return 0;
            case WM_ERASEBKGND:
                return 1; // evitamos flicker; o swapchain cobre tudo
            case WM_SETCURSOR:
                SetCursor(0); // cursor oculto sobre a saída
                return 1;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    #region Win32 interop

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nuint wParam, nint lParam);

    private const uint WS_POPUP = 0x80000000;
    private const uint CS_HREDRAW = 0x0002;
    private const uint CS_VREDRAW = 0x0001;
    private const int SW_SHOWNA = 8;
    private const uint PM_REMOVE = 1;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_SETCURSOR = 0x0020;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wndClass);

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32")]
    private static extern bool ShowWindow(nint hWnd, int cmdShow);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(out NativeMessage msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg);

    [DllImport("user32")]
    private static extern bool TranslateMessage(ref NativeMessage msg);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref NativeMessage msg);

    [DllImport("user32")]
    private static extern nint SetCursor(nint cursor);

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    #endregion
}
