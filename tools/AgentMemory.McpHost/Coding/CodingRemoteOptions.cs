using System.Net;

namespace AgentMemory.McpHost.Coding;

/// <summary>One owner's resource server; an external OpenID Connect issuer owns sign-in and consent.</summary>
internal sealed record CodingRemoteOptions
{
    public required string Issuer { get; init; }
    public required string Subject { get; init; }
    public required string ListenAddress { get; init; }
    public required string CertificatePath { get; init; }
    public required string CertificatePasswordVariable { get; init; }
    /// <summary>Off hides and refuses mutation tools, even when a token has memory:write; no writes are queued or applied.</summary>
    public bool AllowWrites { get; init; }

    public void Validate(string url, bool browserUi)
    {
        if (!Https(url, out var resource) || resource!.AbsolutePath != "/" ||
            !Https(Issuer, out _) || string.IsNullOrWhiteSpace(Subject) || Subject.Length > 256 ||
            !IPAddress.TryParse(ListenAddress, out _) || !Path.IsPathFullyQualified(CertificatePath) ||
            string.IsNullOrWhiteSpace(CertificatePasswordVariable) || browserUi)
            throw new ArgumentException("Remote coding mode requires an HTTPS origin, HTTPS issuer, owner subject, explicit bind IP and absolute TLS certificate path; browser UI is local only.");
    }

    private static bool Https(string value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.Port is > 0 and <= 65535 && !value.Any(char.IsWhiteSpace);

    public static string Resource(CodingServiceOptions options, string project) =>
        new Uri(options.Url).GetLeftPart(UriPartial.Authority) + $"/projects/{project}/mcp";

    public static string Metadata(CodingServiceOptions options, string project) =>
        new Uri(options.Url).GetLeftPart(UriPartial.Authority) + $"/.well-known/oauth-protected-resource/projects/{project}/mcp";
}
