namespace TabTabGo.Storage.Aws;

public class S3Configuration
{
    public string AccessKey { get; set; }
    public string SecretKey { get; set; }
    public string Region { get; set; }
    public string BucketName { get; set; }

    public int ExpirationMinutes { get; set; } = 60;
}