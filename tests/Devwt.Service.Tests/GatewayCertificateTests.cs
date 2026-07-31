using System.Security.AccessControl;

namespace Devwt.Service.Tests;

public sealed class GatewayCertificateTests
{
    [Fact]
    public async Task Gateway_certificate_store_serializes_first_use_across_workers()
    {
        using var temp = new TempDirectory();
        var firstStore = new DevwtGatewayCertificateStore(temp.Path);
        var secondStore = new DevwtGatewayCertificateStore(temp.Path);

        var certificates = await Task.WhenAll(
            Task.Run(firstStore.GetOrCreateServerCertificate),
            Task.Run(secondStore.GetOrCreateServerCertificate));

        try
        {
            Assert.Equal(certificates[0].Thumbprint, certificates[1].Thumbprint);
            Assert.False(File.Exists(Path.Combine(firstStore.CertificateDirectory, ".certificate.lock")));
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }
    }

    [Fact]
    public void Gateway_certificate_store_uses_devwt_state_and_does_not_touch_dotnet_dev_certs_paths()
    {
        using var temp = new TempDirectory();
        var store = new DevwtGatewayCertificateStore(temp.Path);

        using var certificate = store.GetOrCreateServerCertificate();

        Assert.True(certificate.HasPrivateKey);
        Assert.Contains("CN=localhost", certificate.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.Combine(temp.Path, "gateway-cert"), store.CertificateDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".aspnet", store.CertificateDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(store.RootCertificatePath));
        Assert.True(File.Exists(store.ServerCertificatePath));
        Assert.True(new FileInfo(store.RootPfxPath).GetAccessControl().AreAccessRulesProtected);
        Assert.True(new FileInfo(store.ServerCertificatePath).GetAccessControl().AreAccessRulesProtected);
    }
}
