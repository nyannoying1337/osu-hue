using HueApi.ColorConverters;
using HueApi.Entertainment.Models;
using OsuHue;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

internal static class SecurityChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Test root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
        var roots = new X509Certificate2Collection(root);
        const string id = "0011223344556677";
        using var leaf = Issue(root, id);
        var validator = new HueCertificateValidator(id);
        check(validator.Validate(leaf, HueCertificateValidator.CreatePolicy(roots)) && validator.VerifiedId == id,
            "Hue certificate accepts a trusted, valid server with the paired Bridge ID");
        check(!validator.Validate(leaf), "Hue certificate rejects an attacker-owned root");
        check(!new HueCertificateValidator("1122334455667788").Validate(leaf, HueCertificateValidator.CreatePolicy(roots)),
            "Hue certificate rejects another Bridge even under a trusted root");
        using var expired = Issue(root, id, expired: true);
        check(!validator.Validate(expired, HueCertificateValidator.CreatePolicy(roots)), "Hue certificate rejects expiry");
        using var wrongPurpose = Issue(root, id, clientOnly: true);
        check(!validator.Validate(wrongPurpose, HueCertificateValidator.CreatePolicy(roots)), "Hue certificate rejects client-only certificates");
        using var wrongName = Issue(root, "example.com");
        check(!new HueCertificateValidator().Validate(wrongName, HueCertificateValidator.CreatePolicy(roots)),
            "initial Hue discovery rejects a certificate without a Bridge identity");
        check(!validator.Validate(null), "Hue certificate fails closed when absent");

        var area = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var group = new StreamingGroup(Enumerable.Range(0, 21).ToList());
        var layer = group.GetNewLayer(isBaseLayer: true);
        foreach (var light in layer)
        {
            light.State.SetRGBColor(new RGBColor(1, 0, .5));
            light.State.SetBrightness(.5);
        }
        var packets = HueStreamingTransport.EncodePackets(group, Encoding.ASCII.GetBytes(area.ToString("D"))).ToArray();
        check(packets.Length == 2 && packets[0].Length == 192 && packets[1].Length == 59,
            "HueStream v2 splits at twenty channels with exact packet lengths");
        check(Encoding.ASCII.GetString(packets[0], 0, 9) == "HueStream" &&
            packets[0].AsSpan(9, 7).SequenceEqual(new byte[] { 2, 0, 1, 0, 0, 0, 0 }) &&
            Encoding.ASCII.GetString(packets[0], 16, 36) == area.ToString("D"),
            "HueStream v2 carries the protocol header and selected Entertainment ID");
        check(packets[0].AsSpan(52, 7).SequenceEqual(new byte[] { 0, 127, 127, 0, 0, 63, 63 }) && packets[1][52] == 20,
            "HueStream v2 preserves channel IDs and RGB/brightness encoding");

        var temporary = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(temporary, "untrusted cached executable");
            check(!await TosuInstaller.IsVerifiedExecutableAsync(temporary, CancellationToken.None),
                "installer rejects a modified cached tosu executable");
        }
        finally { File.Delete(temporary); }
    }

    private static X509Certificate2 Issue(X509Certificate2 issuer, string id, bool expired = false, bool clientOnly = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + id, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(clientOnly ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        return request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1),
            expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
    }
}
