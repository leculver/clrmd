using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Microsoft.Diagnostics.Runtime.ICorDebug
{
    [ComImport, ClassInterface((short)0), Guid("B76B17EF-16FA-43A3-BABF-DB6E59439EB0")]
    public class CorDebugManagerClass : ICorDebug, CorDebugManager
    {
        // Methods

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public virtual extern void CanLaunchOrAttach([In] uint dwProcessId, [In] int win32DebuggingEnabled);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public virtual extern void CreateProcess([In, MarshalAs(UnmanagedType.LPWStr)] string lpApplicationName, [In, MarshalAs(UnmanagedType.LPWStr)] string lpCommandLine, [In] SECURITY_ATTRIBUTES lpProcessAttributes, [In] SECURITY_ATTRIBUTES lpThreadAttributes, [In] int bInheritHandles, [In] uint dwCreationFlags, [In] IntPtr lpEnvironment, [In, MarshalAs(UnmanagedType.LPWStr)] string lpCurrentDirectory, [In, ComAliasName("Microsoft.Debugging.CorDebug.NativeApi.ULONG_PTR")] STARTUPINFO lpStartupInfo, [In, ComAliasName("Microsoft.Debugging.CorDebug.NativeApi.ULONG_PTR")] PROCESS_INFORMATION lpProcessInformation, [In] CorDebugCreateProcessFlags debuggingFlags, [Out, MarshalAs(UnmanagedType.Interface)] out ICorDebugProcess ppProcess);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public virtual extern void DebugActiveProcess([In] uint id, [In] int win32Attach, [Out, MarshalAs(UnmanagedType.Interface)] out ICorDebugProcess ppProcess);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public virtual extern void EnumerateProcesses([Out, MarshalAs(UnmanagedType.Interface)] out ICorDebugProcessEnum ppProcess);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public virtual extern void GetProcess([In] uint dwProcessId, [Out, MarshalAs(UnmanagedType.Interface)] out ICorDebugProcess ppProcess);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public virtual extern void Initialize();

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public virtual extern void SetManagedHandler([In, MarshalAs(UnmanagedType.Interface)] ICorDebugManagedCallback pCallback);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public virtual extern void SetUnmanagedHandler([In, MarshalAs(UnmanagedType.Interface)] ICorDebugUnmanagedCallback pCallback);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public virtual extern void Terminate();
    }
}