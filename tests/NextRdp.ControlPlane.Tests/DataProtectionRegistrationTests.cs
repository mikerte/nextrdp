using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NextRdp.ControlPlane;

namespace NextRdp.ControlPlane.Tests;

public sealed class DataProtectionRegistrationTests
{
    [Fact]
    public void DevelopmentCanUseExplicitLocalOnlyKeyRing()
    {
        using var directory = new TemporaryDirectory();
        var configuration = CreateConfiguration(directory.Path, new Dictionary<string, string?>());

        using var provider = CreateProvider(configuration, Environments.Development);
        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("NextRDP.Tests.Development");

        Assert.Equal("development-value", protector.Unprotect(protector.Protect("development-value")));
    }

    [Fact]
    public void ProductionRequiresCertificateProtectedKeyRing()
    {
        using var directory = new TemporaryDirectory();
        var configuration = CreateConfiguration(directory.Path, new Dictionary<string, string?>());
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddNextRdpPlatformServices(configuration, new TestHostEnvironment(Environments.Production)));

        Assert.Contains("Data Protection certificate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CertificatePasswordWithoutPathIsRejectedInEveryEnvironment()
    {
        using var directory = new TemporaryDirectory();
        var configuration = CreateConfiguration(directory.Path, new Dictionary<string, string?>
        {
            ["Security:DataProtectionCertificatePassword"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
        });
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddNextRdpPlatformServices(configuration, new TestHostEnvironment(Environments.Development)));

        Assert.Contains("without a certificate path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsNonRsaProtectionCertificate()
    {
        using var directory = new TemporaryDirectory();
        var certificateProtectionValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var certificatePath = CreateEcdsaCertificate(directory.Path, "non-rsa.pfx", certificateProtectionValue);
        var configuration = CreateConfiguration(directory.Path, new Dictionary<string, string?>
        {
            ["Security:DataProtectionCertificatePath"] = certificatePath,
            ["Security:DataProtectionCertificatePassword"] = certificateProtectionValue
        });
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddNextRdpPlatformServices(configuration, new TestHostEnvironment(Environments.Production)));

        Assert.Contains("valid RSA certificate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistedProductionKeysAreEncryptedAndReadableAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var certificateProtectionValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var certificatePath = CreateCertificate(directory.Path, "active.pfx", certificateProtectionValue);
        var settings = new Dictionary<string, string?>
        {
            ["Security:DataProtectionCertificatePath"] = certificatePath,
            ["Security:DataProtectionCertificatePassword"] = certificateProtectionValue
        };
        var configuration = CreateConfiguration(directory.Path, settings);
        string protectedPayload;

        using (var provider = CreateProvider(configuration))
        {
            protectedPayload = provider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("NextRDP.Tests.DataProtection")
                .Protect("phase-two-protected-value");
        }

        var keyFiles = Directory.GetFiles(Path.Combine(directory.Path, "keys"), "key-*.xml");
        var keyFile = Assert.Single(keyFiles);
        var keyXml = File.ReadAllText(keyFile);
        Assert.Contains("encryptedSecret", keyXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phase-two-protected-value", keyXml, StringComparison.Ordinal);

        using var restartedProvider = CreateProvider(configuration);
        var unprotected = restartedProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("NextRDP.Tests.DataProtection")
            .Unprotect(protectedPayload);
        Assert.Equal("phase-two-protected-value", unprotected);
    }

    [Fact]
    public void RotationCertificateCanReadExistingKeyRing()
    {
        using var directory = new TemporaryDirectory();
        var oldCertificateProtectionValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var newCertificateProtectionValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var oldPath = CreateCertificate(directory.Path, "old.pfx", oldCertificateProtectionValue);
        var newPath = CreateCertificate(directory.Path, "new.pfx", newCertificateProtectionValue);
        var oldConfiguration = CreateConfiguration(directory.Path, new Dictionary<string, string?>
        {
            ["Security:DataProtectionCertificatePath"] = oldPath,
            ["Security:DataProtectionCertificatePassword"] = oldCertificateProtectionValue
        });
        string protectedPayload;
        using (var provider = CreateProvider(oldConfiguration))
        {
            protectedPayload = provider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("NextRDP.Tests.Rotation")
                .Protect("rotation-value");
        }

        var rotatedConfiguration = CreateConfiguration(directory.Path, new Dictionary<string, string?>
        {
            ["Security:DataProtectionCertificatePath"] = newPath,
            ["Security:DataProtectionCertificatePassword"] = newCertificateProtectionValue,
            ["Security:DataProtectionUnprotectingCertificates:0:Path"] = oldPath,
            ["Security:DataProtectionUnprotectingCertificates:0:Password"] = oldCertificateProtectionValue
        });
        using var rotatedProvider = CreateProvider(rotatedConfiguration);
        var unprotected = rotatedProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("NextRDP.Tests.Rotation")
            .Unprotect(protectedPayload);

        Assert.Equal("rotation-value", unprotected);
    }

    private static ServiceProvider CreateProvider(IConfiguration configuration, string environmentName = "Production")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNextRdpPlatformServices(configuration, new TestHostEnvironment(environmentName));
        return services.BuildServiceProvider();
    }

    private static IConfiguration CreateConfiguration(string directory, IReadOnlyDictionary<string, string?> settings)
    {
        var values = new Dictionary<string, string?>(settings, StringComparer.Ordinal)
        {
            ["Security:DataProtectionKeysPath"] = Path.Combine(directory, "keys"),
            ["Backup:Pbkdf2Iterations"] = "600000"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string CreateCertificate(string directory, string fileName, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=NextRDP Data Protection Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    private static string CreateEcdsaCertificate(string directory, string fileName, string password)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=NextRDP Non-RSA Test", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "NextRdp.ControlPlane.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nextrdp-data-protection-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
