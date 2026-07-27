namespace Umamusume.CoreBridge.Tests;

internal sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("UMA_NATIVE_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set UMA_NATIVE_INTEGRATION=1 after staging native inputs.";
        }
    }
}
