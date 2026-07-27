namespace Umamusume.CoreBridge;

internal enum ConnectionErrorCode : int
{
    Success = 0,
    AdbExecutableNotFound = 1,
    ProcessStartFailed = 2,
    CommandTimedOut = 3,
    DeviceUnauthorized = 4,
    DeviceOffline = 5,
    DeviceUnavailable = 6,
    CommandFailed = 7,
    InvalidDeviceResponse = 8,
    Canceled = 9,
    DeviceNotReady = 10,
    InvalidArgument = 11,
    Busy = 12,
    BootNotCompleted = 13,
    TargetGameNotInstalled = 14,
    DeviceDisconnected = 15,
}
