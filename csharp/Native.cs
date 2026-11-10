using System;
using System.Runtime.InteropServices;

namespace PikaOp {
internal static class Native {
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X,Y; public uint Data,Flags,Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit,Size=40)] internal struct Input {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
    }
    internal delegate bool EnumProc(IntPtr hwnd,IntPtr param);
    [DllImport("kernel32.dll",SetLastError=true)] internal static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool ReadProcessMemory(IntPtr process,IntPtr address,byte[] bytes,IntPtr size,out IntPtr read);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr hwnd,ref Point point);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback,IntPtr parameter);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd,out int pid);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("winmm.dll")] internal static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] internal static extern uint timeEndPeriod(uint period);
    internal static bool Down(int key) { return (GetAsyncKeyState(key)&0x8000)!=0; }
    internal static void Move(int x,int y) {
        if(x==0 && y==0)return;
        var input=new Input { Type=0,Mouse=new MouseInput { X=x,Y=y,Flags=1 } };
        if(SendInput(1,new[]{input},40)!=1)throw new InvalidOperationException("Mouse input was rejected. Tracking stopped.");
    }
    internal static IntPtr FindWindow(int pid) {
        IntPtr selected=IntPtr.Zero; long area=0;
        EnumWindows(delegate(IntPtr hwnd,IntPtr ignored) {
            int owner; GetWindowThreadProcessId(hwnd,out owner); Rect r;
            if(owner==pid && IsWindowVisible(hwnd) && GetClientRect(hwnd,out r)) {
                long a=(long)(r.Right-r.Left)*(r.Bottom-r.Top);
                if(a>area){area=a;selected=hwnd;}
            } return true;
        },IntPtr.Zero);
        return selected;
    }
}
}
