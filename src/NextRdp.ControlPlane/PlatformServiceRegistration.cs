using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using NextRdp.Platform.Application;
using NextRdp.Platform.Infrastructure;

namespace NextRdp.ControlPlane;

public static class PlatformServiceRegistration
{
    public static IServiceCollection AddNextRdpAuthorization(this IServiceCollection services, IConfiguration configuration)
    {
        var openFgaSection = configuration.GetSection("Authorization:OpenFga");
        var openFga = new OpenFgaOptions(
            new Uri(openFgaSection["BaseAddress"] ?? throw new InvalidOperationException("Authorization:OpenFga:BaseAddress is required."), UriKind.Absolute),
            openFgaSection["StoreId"] ?? string.Empty,
            EmptyToNull(openFgaSection["AuthorizationModelId"]));
        var opaSection = configuration.GetSection("Authorization:Opa");
        var opa = new OpaOptions(
            new Uri(opaSection["BaseAddress"] ?? throw new InvalidOperationException("Authorization:Opa:BaseAddress is required."), UriKind.Absolute),
            opaSection["DecisionPath"] ?? "v1/data/nextrdp/phase2/decision");
        services.AddSingleton(openFga);
        services.AddSingleton(opa);
        services.AddHttpClient<OpenFgaAuthorizationService>(client =>
        {
            client.BaseAddress = openFga.BaseAddress;
            client.Timeout = TimeSpan.FromSeconds(3);
        });
        services.AddHttpClient<OpenFgaBootstrapper>(client =>
        {
            client.BaseAddress = openFga.BaseAddress;
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient<OpenFgaRelationshipService>(client =>
        {
            client.BaseAddress = openFga.BaseAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddHttpClient<OpaAttributePolicyService>(client =>
        {
            client.BaseAddress = opa.BaseAddress;
            client.Timeout = TimeSpan.FromSeconds(3);
        });
        services.AddScoped<IResourceAuthorizationService>(provider => provider.GetRequiredService<OpenFgaAuthorizationService>());
        services.AddScoped<IResourceRelationshipService>(provider => provider.GetRequiredService<OpenFgaRelationshipService>());
        services.AddScoped<IAttributePolicyService>(provider => provider.GetRequiredService<OpaAttributePolicyService>());
        return services;
    }

    public static IServiceCollection AddNextRdpPlatformServices(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var keysPath = configuration["Security:DataProtectionKeysPath"] ?? "data-protection-keys";
        var resolvedKeysPath = Path.IsPathRooted(keysPath) ? keysPath : Path.Combine(AppContext.BaseDirectory, keysPath);
        Directory.CreateDirectory(resolvedKeysPath);
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("NextRDP.Phase2")
            .PersistKeysToFileSystem(new DirectoryInfo(resolvedKeysPath));
        ConfigureDataProtectionKeyEncryption(dataProtection, configuration, environment);
        services.AddSingleton<ISyncCursorCodec, DataProtectionSyncCursorCodec>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IPlatformManagementService, PlatformManagementService>();
        services.AddScoped<IPlatformSessionRevocationService, PlatformSessionRevocationService>();
        services.AddScoped<IServiceAccountCredentialStore, OpenIddictServiceAccountCredentialStore>();
        services.AddScoped<IAdministrativeBackupService, AdministrativeBackupService>();
        services.AddSingleton(new BackupEncryptionOptions(configuration.GetValue("Backup:Pbkdf2Iterations", 600000)));
        services.AddScoped<IFederatedUserProvisioner, FederatedUserProvisioner>();
        services.AddScoped<IPlatformActorAccessor, PlatformActorAccessor>();
        services.AddScoped<CookieAntiforgeryFilter>();
        services.AddHostedService<OutboxWorker>();
        services.AddHttpContextAccessor();
        return services;
    }

    private static void ConfigureDataProtectionKeyEncryption(IDataProtectionBuilder builder, IConfiguration configuration, IHostEnvironment environment)
    {
        var certificatePath = configuration["Security:DataProtectionCertificatePath"];
        var certificatePassword = configuration["Security:DataProtectionCertificatePassword"];
        if (string.IsNullOrWhiteSpace(certificatePath))
        {
            if (!string.IsNullOrWhiteSpace(certificatePassword))
                throw new InvalidOperationException("A Data Protection certificate password was configured without a certificate path.");
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("A production Data Protection certificate path and password are required to encrypt the persisted key ring.");
            return;
        }

        var activeCertificate = LoadDataProtectionCertificate(certificatePath, certificatePassword, "active");
        builder.ProtectKeysWithCertificate(activeCertificate);

        var previousCertificates = configuration.GetSection("Security:DataProtectionUnprotectingCertificates")
            .GetChildren()
            .Select((section, index) => LoadDataProtectionCertificate(section["Path"], section["Password"], $"previous rotation slot {index}"))
            .ToArray();
        if (previousCertificates.Length > 0)
            builder.UnprotectKeysWithAnyCertificate(previousCertificates);
    }

    private static X509Certificate2 LoadDataProtectionCertificate(string? path, string? password, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException($"The {purpose} Data Protection certificate path and password are required.");
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        if (!File.Exists(resolvedPath))
            throw new InvalidOperationException($"The {purpose} Data Protection certificate file was not found.");

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(resolvedPath, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The {purpose} Data Protection certificate could not be loaded.", exception);
        }

        using var rsa = certificate.GetRSAPrivateKey();
        if (!certificate.HasPrivateKey || rsa is null || DateTimeOffset.UtcNow < certificate.NotBefore || DateTimeOffset.UtcNow >= certificate.NotAfter)
        {
            certificate.Dispose();
            throw new InvalidOperationException($"The {purpose} Data Protection certificate must be a currently valid RSA certificate with a private key.");
        }
        return certificate;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed class DataProtectionSyncCursorCodec(IDataProtectionProvider provider) : ISyncCursorCodec
{
    private readonly IDataProtector protector = provider.CreateProtector("NextRDP", "Phase2", "SyncCursor", "v1");

    public string Encode(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        return protector.Protect(sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public long Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        if (cursor.Length > 2048) throw PlatformProblemException.Validation(["Synchronization cursor is invalid."]);
        try
        {
            var value = protector.Unprotect(cursor);
            if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sequence) || sequence < 0)
                throw new FormatException();
            return sequence;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException)
        {
            throw PlatformProblemException.Validation(["Synchronization cursor is invalid."]);
        }
    }
}
