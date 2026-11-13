using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HomeBridge.Core;

public static class Identity
{
    public static X509Certificate2 Load(StateStore store)
    {
        var pfx = store.ReadSecret("identity.dpapi");
        if (pfx is not null)
        {
            // Schannel requires a user key container; do not use EphemeralKeySet on Windows TLS.
            try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=HomeBridge", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            store.WriteSecret("identity.dpapi", pfx);
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    public static string Fingerprint(X509Certificate certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);
    public static bool IsValid(X509Certificate certificate)
    {
        using var copy = new X509Certificate2(certificate);
        return DateTime.UtcNow >= copy.NotBefore.ToUniversalTime() && DateTime.UtcNow < copy.NotAfter.ToUniversalTime();
    }
    public static bool Matches(X509Certificate? certificate, string pin) =>
        certificate is not null && IsValid(certificate) && string.Equals(Fingerprint(certificate), pin, StringComparison.Ordinal);
}
