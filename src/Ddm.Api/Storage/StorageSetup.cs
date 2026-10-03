using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Storage;

public static class StorageSetup
{
    public static IServiceCollection AddDdmStorage(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<S3Options>(config.GetSection("Storage"));
        services.AddSingleton<IAmazonS3>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<S3Options>>().Value;
            return new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), new AmazonS3Config
            {
                ServiceURL = o.ServiceUrl,
                ForcePathStyle = o.ForcePathStyle,
                AuthenticationRegion = o.Region,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
        });
        services.AddSingleton<IBlobStore>(sp =>
            new S3BlobStore(sp.GetRequiredService<IAmazonS3>(), sp.GetRequiredService<IOptions<S3Options>>().Value.Bucket));
        if (config.GetValue<bool>("Storage:CreateBucket")) services.AddHostedService<BucketInitializer>();
        return services;
    }

    private sealed class BucketInitializer(IAmazonS3 s3, IOptions<S3Options> options) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => S3BlobStore.EnsureBucketAsync(s3, options.Value.Bucket, ct);
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
