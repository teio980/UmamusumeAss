using System.Diagnostics;

namespace UmamusumeWpfGui.Helper;

/// <summary>
/// Real implementation of <see cref="IProcessEnumerator"/> that
/// wraps <see cref="Process.GetProcesses()"/>.
/// Inaccessible processes (e.g. elevated) produce an entry with null <see cref="ProcessEntry.MainModulePath"/>.
/// </summary>
public sealed class ProcessEnumerator : IProcessEnumerator
{
    public ProcessEntry[] GetProcesses()
    {
        var processes = Process.GetProcesses();
        var entries = new List<ProcessEntry>(processes.Length);

        foreach (var process in processes)
        {
            string? mainModulePath = null;

            try
            {
                mainModulePath = process.MainModule?.FileName;
            }
            catch
            {
                // Process is inaccessible (e.g. elevated or system process).
                // Leave mainModulePath as null.
            }
            finally
            {
                process.Dispose();
            }

            entries.Add(new ProcessEntry(process.ProcessName, mainModulePath));
        }

        return [.. entries];
    }
}
