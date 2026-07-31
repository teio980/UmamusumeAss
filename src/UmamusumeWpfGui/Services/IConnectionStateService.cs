using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;





public interface IConnectionStateService
{

    ConnectionState State { get; }


    LastVerifiedConnection? LastVerifiedConnection { get; }


    ControlSessionSnapshot? ControlSession { get; }


    event EventHandler? StateChanged;


    void SetState(ConnectionState newState);


    void UpdateLastVerified(LastVerifiedConnection record);


    void ClearLastVerified();
}
