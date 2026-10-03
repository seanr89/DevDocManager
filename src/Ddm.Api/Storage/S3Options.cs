namespace Ddm.Api.Storage;

public sealed class S3Options
{
    public string ServiceUrl { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "ddm";
    public string Region { get; set; } = "us-east-1";
    public bool ForcePathStyle { get; set; } = true;
    public bool CreateBucket { get; set; }
}
