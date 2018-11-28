using System.Runtime.InteropServices;

namespace Microsoft.Diagnostics.Runtime.ICorDebug
{
    [ComImport, InterfaceType(1), Guid("CC7BCAE8-8A68-11D2-983C-0000F808342D")]
    public interface ICorDebugBreakpoint
    {

        void Activate([In] int bActive);

        void IsActive([Out] out int pbActive);
    }
}