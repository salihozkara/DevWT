using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using Devwt.Core;

namespace Devwt.Service;

public sealed class DevwtGatewayCertificateStore(string stateRoot)
{
    private const string RootSubject = "CN=DevWT Local Gateway Root";
    private const string ServerSubject = "CN=localhost";
    private const string PfxPassword = "";

    private string CertificateLockPath => Path.Combine(CertificateDirectory, ".certificate.lock");

    public string CertificateDirectory { get; } = Path.Combine(DevwtStateDefaults.ResolveStateRoot(stateRoot), "gateway-cert");

    public string RootCertificatePath => Path.Combine(CertificateDirectory, "DevWT-Gateway-Root.cer");

    public string RootPfxPath => Path.Combine(CertificateDirectory, "DevWT-Gateway-Root.pfx");

    public string ServerCertificatePath => Path.Combine(CertificateDirectory, "DevWT-Gateway-Localhost-v2.pfx");

    public X509Certificate2 GetOrCreateServerCertificate()
    {
        Directory.CreateDirectory(CertificateDirectory);
        using var certificateLock = AcquireCertificateLock();
        return GetOrCreateServerCertificateCore();
    }

    private X509Certificate2 GetOrCreateServerCertificateCore()
    {
        if (TryLoadValidServerCertificate() is { } existing)
        {
            return existing;
        }

        using var root = GetOrCreateRootCertificateCore();
        using var serverKey = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName(ServerSubject),
            serverKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            critical: false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        foreach (var address in LocalCertificateAddresses())
        {
            san.AddIpAddress(address);
        }
        request.CertificateExtensions.Add(san.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddYears(2);
        using var publicOnly = request.Create(root, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        using var withPrivateKey = publicOnly.CopyWithPrivateKey(serverKey);
        File.WriteAllBytes(ServerCertificatePath, withPrivateKey.Export(X509ContentType.Pfx, PfxPassword));
        RestrictPrivateKeyFile(ServerCertificatePath);
        return LoadCertificate(ServerCertificatePath);
    }

    public X509Certificate2 GetOrCreateRootCertificate()
    {
        Directory.CreateDirectory(CertificateDirectory);
        using var certificateLock = AcquireCertificateLock();
        return GetOrCreateRootCertificateCore();
    }

    private X509Certificate2 GetOrCreateRootCertificateCore()
    {
        if (File.Exists(RootPfxPath))
        {
            var existing = LoadCertificate(RootPfxPath);
            if (existing.NotAfter > DateTimeOffset.UtcNow.AddDays(30) && existing.HasPrivateKey)
            {
                EnsureRootCer(existing);
                return existing;
            }

            existing.Dispose();
        }

        using var rootKey = RSA.Create(3072);
        var request = new CertificateRequest(
            new X500DistinguishedName(RootSubject),
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        using var root = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));
        File.WriteAllBytes(RootPfxPath, root.Export(X509ContentType.Pfx, PfxPassword));
        RestrictPrivateKeyFile(RootPfxPath);
        EnsureRootCer(root);
        return LoadCertificate(RootPfxPath);
    }

    public void TrustRootForCurrentUser()
    {
        TrustRoot(StoreLocation.CurrentUser);
    }

    public void TrustRoot(StoreLocation location)
    {
        using var root = GetOrCreateRootCertificate();
#pragma warning disable SYSLIB0057
        using var publicRoot = new X509Certificate2(RootCertificatePath);
#pragma warning restore SYSLIB0057
        using var store = new X509Store(StoreName.Root, location);
        store.Open(OpenFlags.ReadWrite);
        if (!store.Certificates.Any(certificate =>
            certificate.Thumbprint?.Equals(publicRoot.Thumbprint, StringComparison.OrdinalIgnoreCase) == true))
        {
            store.Add(publicRoot);
        }
    }

    public bool IsRootTrusted(StoreLocation location)
    {
        using var root = GetOrCreateRootCertificate();
        using var store = new X509Store(StoreName.Root, location);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Any(certificate =>
            certificate.Thumbprint?.Equals(root.Thumbprint, StringComparison.OrdinalIgnoreCase) == true);
    }

    public string Status()
    {
        using var server = GetOrCreateServerCertificate();
        using var root = GetOrCreateRootCertificate();
        return string.Join(
            Environment.NewLine,
            [
                $"certificate directory: {CertificateDirectory}",
                $"root thumbprint: {root.Thumbprint}",
                $"server thumbprint: {server.Thumbprint}",
                $"server expires: {server.NotAfter:O}",
                $"root path: {RootCertificatePath}",
                $"server path: {ServerCertificatePath}"
            ]) + Environment.NewLine;
    }

    public void Clean()
    {
#pragma warning disable SYSLIB0057
        using var root = File.Exists(RootCertificatePath) ? new X509Certificate2(RootCertificatePath) : null;
#pragma warning restore SYSLIB0057
        if (root is not null)
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in store.Certificates
                         .Where(certificate => certificate.Thumbprint?.Equals(root.Thumbprint, StringComparison.OrdinalIgnoreCase) == true)
                         .ToArray())
            {
                store.Remove(certificate);
            }
        }

        if (Directory.Exists(CertificateDirectory))
        {
            Directory.Delete(CertificateDirectory, recursive: true);
        }
    }

    private X509Certificate2? TryLoadValidServerCertificate()
    {
        if (!File.Exists(ServerCertificatePath))
        {
            return null;
        }

        var certificate = LoadCertificate(ServerCertificatePath);
        if (certificate.HasPrivateKey && certificate.NotAfter > DateTimeOffset.UtcNow.AddDays(14))
        {
            return certificate;
        }

        certificate.Dispose();
        return null;
    }

    private void EnsureRootCer(X509Certificate2 root)
    {
        File.WriteAllBytes(RootCertificatePath, root.Export(X509ContentType.Cert));
    }

    private static X509Certificate2 LoadCertificate(string path)
    {
        RestrictPrivateKeyFile(path);
        return X509CertificateLoader.LoadPkcs12FromFile(
            path,
            PfxPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    private FileStream AcquireCertificateLock()
    {
        Directory.CreateDirectory(CertificateDirectory);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(
                    CertificateLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static IReadOnlyList<IPAddress> LocalCertificateAddresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        foreach (var address in NetworkInterface.GetAllNetworkInterfaces()
                     .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                     .Select(unicast => unicast.Address))
        {
            if (!address.IsIPv6Multicast && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
            {
                addresses.Add(address);
            }
        }

        return addresses.ToArray();
    }

    private static void RestrictPrivateKeyFile(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !File.Exists(path))
        {
            return;
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddAccess(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddAccess(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        if (WindowsIdentity.GetCurrent().User is { } currentUser)
        {
            AddAccess(security, currentUser);
        }

        new FileInfo(path).SetAccessControl(security);
    }

    private static void AddAccess(FileSecurity security, SecurityIdentifier identity) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
}
