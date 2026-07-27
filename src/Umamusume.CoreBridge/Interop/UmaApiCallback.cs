using System.Runtime.InteropServices;

namespace Umamusume.CoreBridge;

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void UmaApiCallback(int message, IntPtr detailsJson, IntPtr customArg);
