using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

internal static class PeerCertificate
{
    public static IPAddress? FindWifiAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
            adapter.OperationalStatus == OperationalStatus.Up)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
        .Select(entry => entry.Address)
        .FirstOrDefault(address =>
        {
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168));
        });

    public static X509Certificate2 Create(IPAddress address)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={address}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var bytes = generated.Export(X509ContentType.Pkcs12);
        try { return X509CertificateLoader.LoadPkcs12(bytes, null); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
