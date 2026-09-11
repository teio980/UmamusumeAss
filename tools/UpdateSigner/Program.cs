using System.Security.Cryptography;
using System.Text;

const string expectedPublicKey =
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE1JYRq0FJhM8DOCRjp1/iu61WAh1k" +
    "sO7RrbvAjrF6ApeTdMr93ttNG0gmhv2h52d+KgEZ6TQx+RVOKh23z2g00g==";

if (args.Length >= 1 && args[0].Equals("--generate-key", StringComparison.Ordinal))
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("Usage: UpdateSigner --generate-key <private.pem> <public-spki-base64.txt>");
        return 2;
    }
    try
    {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(args[1], generated.ExportECPrivateKeyPem(), new UTF8Encoding(false));
        File.WriteAllText(args[2], Convert.ToBase64String(generated.ExportSubjectPublicKeyInfo()), new UTF8Encoding(false));
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 5;
    }
}

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: UpdateSigner <manifest.json> <manifest.sig> | --generate-key <private.pem> <public-spki-base64.txt>");
    return 2;
}

var pem = Environment.GetEnvironmentVariable("UMA_UPDATE_SIGNING_KEY_PEM");
if (string.IsNullOrWhiteSpace(pem))
{
    Console.Error.WriteLine("UMA_UPDATE_SIGNING_KEY_PEM is required in the protected release environment.");
    return 3;
}

try
{
    var manifest = File.ReadAllBytes(args[0]);
    using var signer = ECDsa.Create();
    signer.ImportFromPem(pem);
    var actualPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actualPublicKey), Encoding.ASCII.GetBytes(expectedPublicKey)))
    {
        Console.Error.WriteLine("Signing key does not match the embedded verifier key.");
        return 4;
    }

    var signature = signer.SignData(
        manifest,
        HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    if (signature.Length != 64) throw new CryptographicException("Unexpected P1363 signature length.");
    File.WriteAllText(args[1], Convert.ToBase64String(signature), new UTF8Encoding(false));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 5;
}
