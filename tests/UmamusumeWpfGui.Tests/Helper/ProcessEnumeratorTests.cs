using System.Diagnostics;
using UmamusumeWpfGui.Helper;

namespace UmamusumeWpfGui.Tests.Helper;

public sealed class ProcessEnumeratorTests
{
    [Fact]
    public void GetProcesses_ReturnsCurrentProcessWithItsName()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var enumerator = new ProcessEnumerator();

        var processes = enumerator.GetProcesses();

        Assert.Contains(processes, process =>
            process.Name == currentProcess.ProcessName);
    }
}
