using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EnergyTracker.Api.Tests;

// Throw-away self-signed certificates for the Data Protection tests (Story 11.6). Generated per call
// and never written to disk: no certificate or private key is ever committed to the repository.
internal static class DataProtectionTestCertificates
{
    public const string Password = "test-only-password";

    public static string CreateSelfSignedPkcs12Base64()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=energy-tracker-data-protection-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, Password));
    }
}
