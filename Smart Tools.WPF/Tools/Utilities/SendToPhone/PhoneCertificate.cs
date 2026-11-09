using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The certificate for one Send to Phone session: made here, valid for 24 hours, held in memory only and
    /// never added to any Windows or browser trust store. The phone shows a "not trusted" warning once (it has
    /// no way to know this certificate), which is why the real protection is the key in the QR code and the
    /// SHA-256 fingerprint shown on the PC to compare.
    /// </summary>
    public static class PhoneCertificate
    {
        public sealed record Made(X509Certificate2 Certificate, string Fingerprint);

        public static Made Create(IPAddress address)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Smart Tools Send to Phone", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));   // server authentication
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(address);
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());

            using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(24));

            // Round trip through a PFX with a throwaway password so Windows' TLS stack can use the private key (in memory only).
            string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            byte[] pfx = selfSigned.Export(X509ContentType.Pfx, password);
            var usable = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.Exportable);
            CryptographicOperations.ZeroMemory(pfx);
            return new Made(usable, Convert.ToHexString(usable.GetCertHash(HashAlgorithmName.SHA256)));
        }

        /// <summary>"AB:CD:EF..." in groups, easier to compare by eye.</summary>
        public static string Pretty(string hex)
        {
            var parts = new List<string>();
            for (int i = 0; i + 1 < hex.Length; i += 2) parts.Add(hex.Substring(i, 2));
            return string.Join(":", parts);
        }
    }
}
