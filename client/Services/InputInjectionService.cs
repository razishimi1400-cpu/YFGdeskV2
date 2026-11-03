#if NET10_0_WINDOWS
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YfgDesk.Client.Services;

/// <summary>
/// Mouse / keyboard input injection.
///
/// <para>
/// On Windows: uses <c>User32!SendInput</c> for native hardware-level injection.
/// </para>
/// <para>
/// On Linux: stubbed — throws <see cref="PlatformNotSupportedException"/>.
/// (Linux does not support remote-control injection in v2.0; the viewer can
/// still see the remote screen.)
/// </para>
/// <para>
/// Input events arrive over the input <c>RTCDataChannel</c> from the remote
/// peer; the host-side <see cref="PeerConnection"/> dispatches them to
/// <see cref="InjectAsync"/>.
/// </para>
/// </summary>
public sealed class InputInjectionService
{
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Inject a single input event (mouse or keyboard) described by a JSON
    /// element. Recognized shapes:
    /// <list type="bullet">
    ///   <item><c>{ type:"mouseMove", x:int, y:int }</c></item>
    ///   <item><c>{ type:"mouseDown", button:"left"|"right"|"middle" }</c></item>
    ///   <item><c>{ type:"mouseUp", button:"left"|"right"|"middle" }</c></item>
    ///   <item><c>{ type:"mouseWheel", delta:int }</c></item>
    ///   <item><c>{ type:"keyDown", vk:int }</c></item>
    ///   <item><c>{ type:"keyUp", vk:int }</c></item>
    /// </list>
    /// </summary>
    public Task InjectAsync(JsonElement evt, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Input injection requires Windows.");

        if (!evt.TryGetProperty("type", out var typeProp)) return Task.CompletedTask;
        var type = typeProp.GetString() ?? "";

        switch (type)
        {
            case "mouseMove":
                {
                    var x = evt.GetProperty("x").GetInt32();
                    var y = evt.GetProperty("y").GetInt32();
                    SendMouseMove(x, y);
                    break;
                }
            case "mouseDown":
                {
                    var btn = evt.GetProperty("button").GetString() ?? "left";
                    SendMouseDown(MapButton(btn));
                    break;
                }
            case "mouseUp":
                {
                    var btn = evt.GetProperty("button").GetString() ?? "left";
                    SendMouseUp(MapButton(btn));
                    break;
                }
            case "mouseWheel":
                {
                    var delta = evt.GetProperty("delta").GetInt32();
                    SendMouseWheel(delta);
                    break;
                }
            case "keyDown":
                {
                    var vk = evt.GetProperty("vk").GetInt32();
                    SendKeyDown((ushort)vk);
                    break;
                }
            case "keyUp":
                {
                    var vk = evt.GetProperty("vk").GetInt32();
                    SendKeyUp((ushort)vk);
                    break;
                }
        }
        return Task.CompletedTask;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private static void SendMouseMove(int x, int y)
    {
        // Normalize to 0..65535 range as required by MOUSEEVENTF_ABSOLUTE.
        // Caller passes screen-space pixels; we use the primary monitor's bounds.
        var bounds = System.Windows.SystemParameters.PrimaryScreenWidth;
        var boundsY = System.Windows.SystemParameters.PrimaryScreenHeight;
        var dx = (int)((double)x / Math.Max(1, bounds) * 65535.0);
        var dy = (int)((double)y / Math.Max(1, boundsY) * 65535.0);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = dx, dy = dy, mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                    time = 0, dwExtraInfo = IntPtr.Zero,
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseDown(uint flag)
    {
        var input = new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flag } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseUp(uint flag)
    {
        var input = new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flag } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseWheel(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    mouseData = (uint)delta,
                    dwFlags = MOUSEEVENTF_WHEEL,
                    time = 0, dwExtraInfo = IntPtr.Zero,
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyDown(ushort vk)
    {
        var input = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyUp(ushort vk)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static uint MapButton(string btn) => btn switch
    {
        "left" => MOUSEEVENTF_LEFTDOWN,
        "right" => MOUSEEVENTF_RIGHTDOWN,
        "middle" => MOUSEEVENTF_MIDDLEDOWN,
        _ => MOUSEEVENTF_LEFTDOWN,
    };
}
#endif
