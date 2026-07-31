namespace UmamusumeWpfGui.Helper;





public interface IProcessEnumerator
{





    ProcessEntry[] GetProcesses();
}






public sealed record ProcessEntry(string Name, string? MainModulePath);
