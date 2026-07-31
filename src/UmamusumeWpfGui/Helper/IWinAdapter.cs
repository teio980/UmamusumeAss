using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Helper;






public interface IWinAdapter
{





    DiscoveryResult RefreshEmulatorsInfo();






    AdbDevicesResult GetAdbDevices(string adbPath);





    Task<AdbDevicesResult> GetAdbDevicesAsync(
        string adbPath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(GetAdbDevices(adbPath));

    EndpointResolutionResult ResolveEndpoints(string adbPath, string profileName, CancellationToken cancellationToken);

    Task<EndpointResolutionResult> ResolveEndpointsAsync(
        string adbPath,
        string profileName,
        CancellationToken cancellationToken) =>
        Task.FromResult(ResolveEndpoints(adbPath, profileName, cancellationToken));
}
