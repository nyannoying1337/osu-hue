using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OsuHue;

internal sealed class HueCertificateValidator
{
    private static readonly X509Certificate2Collection Roots = LoadRoots();
    private readonly string? expectedId;
    internal string? VerifiedId
    {
        get; private set;
    }

    internal HueCertificateValidator(string? expectedId = null)
    {
        if (expectedId is not null && !IsBridgeId(expectedId))
            throw new ArgumentException("Invalid Hue Bridge identity.", nameof(expectedId));
        this.expectedId = expectedId;
    }

    internal static bool IsBridgeId(string value) => value.Length == 16 && value.All(Uri.IsHexDigit);

    private static X509Certificate2Collection LoadRoots()
    {
        using var stream = typeof(HueCertificateValidator).Assembly.GetManifestResourceStream("OsuHue.Certificates.hue-roots.pem")
            ?? throw new InvalidOperationException("Hue trust roots are missing.");
        using var reader = new StreamReader(stream);
        var roots = new X509Certificate2Collection();
        roots.ImportFromPem(reader.ReadToEnd());
        return roots;
    }

    internal static X509ChainPolicy CreatePolicy(X509Certificate2Collection? roots = null)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true,
            VerificationFlags = X509VerificationFlags.NoFlag
        };
        policy.CustomTrustStore.AddRange(roots ?? Roots);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return policy;
    }

    internal bool Validate(X509Certificate2? certificate, X509ChainPolicy? policy = null)
    {
        VerifiedId = null;
        if (certificate is null)
            return false;
        using var chain = new X509Chain();
        chain.ChainPolicy = policy ?? CreatePolicy();
        if (!chain.Build(certificate))
            return false;
        var id = certificate.GetNameInfo(X509NameType.SimpleName, false);
        if (!IsBridgeId(id) || (expectedId is not null && !id.Equals(expectedId, StringComparison.OrdinalIgnoreCase)))
            return false;
        VerifiedId = id.ToLowerInvariant();
        return true;
    }

    internal HttpClient CreateClient(string ip, string? key)
    {
        HueSession.ValidateIp(ip);
        // Hue certifies the Bridge ID, not its DHCP address. Ignore only that name
        // mismatch: trust, validity, purpose and (after pairing) identity are checked.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = CreatePolicy(),
                RemoteCertificateValidationCallback = (_, cert, _, _) => cert is X509Certificate2 leaf && Validate(leaf)
            }
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri($"https://{ip}/"), Timeout = TimeSpan.FromSeconds(6), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        if (key is not null)
            http.DefaultRequestHeaders.Add("hue-application-key", key);
        return http;
    }
}
