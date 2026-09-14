namespace UmamusumeWpfGui.Services.Update;

internal static class UpdateCachePaths
{
    internal static string GetUpdatesRoot(string? localAppData = null)
    {
        var root = localAppData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.GetFullPath(Path.Combine(root, "UmamusumeAss", "updates"));
    }

    internal static string GetOperationRoot(string operationId, string? updatesRoot = null)
    {
        if (!Guid.TryParseExact(operationId, "N", out _))
            throw new ArgumentException("The update operation ID is invalid.", nameof(operationId));

        return Path.Combine(
            Path.GetFullPath(updatesRoot ?? GetUpdatesRoot()),
            operationId);
    }
}
