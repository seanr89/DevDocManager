namespace Ddm.Api.Assets;

public sealed class ContentOptions
{
    /// <summary>Origin that serves /content, separate from the app in production. Empty means same-origin relative links.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>HMAC key for content links; at least 32 characters.</summary>
    public string SigningKey { get; set; } = "";
}
