using System.Diagnostics;

namespace UmamusumeWpfGui.Helper;






public sealed class ProcessEnumerator : IProcessEnumerator
{
    public ProcessEntry[] GetProcesses()
    {
        var processes = Process.GetProcesses();
        var entries = new List<ProcessEntry>(processes.Length);

        foreach (var process in processes)
        {
            string? mainModulePath = null;
            string processName;

            try
            {
                processName = process.ProcessName;
                mainModulePath = process.MainModule?.FileName;
            }
            catch
            {


                processName = string.Empty;
            }
            finally
            {
                process.Dispose();
            }

            entries.Add(new ProcessEntry(processName, mainModulePath));
        }

        return [.. entries];
    }
}
