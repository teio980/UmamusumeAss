namespace Umamusume.UpdateCore;

/// <summary>Public P-256 verifier key paired with the protected release signing secret.</summary>
public static class UpdateSigningKey
{
    public static string EmbeddedPublicKeyBase64 { get; } = LoadPublicKey();

    private static string LoadPublicKey()
    {
        using var stream = typeof(UpdateSigningKey).Assembly.GetManifestResourceStream(
            "Umamusume.UpdateCore.update-public.txt")
            ?? throw new InvalidOperationException("The embedded update public key is missing.");
        using var reader = new StreamReader(stream);
        string value = reader.ReadToEnd().Trim();
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(value), out _);
        return value;
    }
}
