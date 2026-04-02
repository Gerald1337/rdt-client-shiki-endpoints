using System;
using System.Threading.RateLimiting;

namespace RdtClient.Service.Services;

public static class GlobalDownloadSpeedLimiter
{
    private static readonly Object Sync = new();
    private static TokenBucketRateLimiter? _rateLimiter;
    private static Int64 _limitBytesPerSecond;

    public static void Throttle(Int64 bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        var limitMB = Settings.Get.DownloadClient.GlobalMaxSpeed;
        var limitBytes = limitMB <= 0 ? 0 : limitMB * 1024L * 1024L;

        EnsureLimiter(limitBytes);

        var limiter = _rateLimiter;

        if (limiter == null)
        {
            return;
        }

        var requestBytes = (Int32)Math.Min(bytes, Int32.MaxValue);
        using var lease = limiter.AcquireAsync(requestBytes).GetAwaiter().GetResult();
    }

    private static void EnsureLimiter(Int64 limitBytes)
    {
        lock (Sync)
        {
            if (_limitBytesPerSecond == limitBytes && (_rateLimiter != null || limitBytes == 0))
            {
                return;
            }

            _rateLimiter?.Dispose();
            _limitBytesPerSecond = limitBytes;

            if (_limitBytesPerSecond > 0)
            {
                var tokenLimit = (Int32)Math.Min(_limitBytesPerSecond, Int32.MaxValue);
                _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = tokenLimit,
                    TokensPerPeriod = tokenLimit,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = Int32.MaxValue,
                    AutoReplenishment = true
                });
            }
            else
            {
                _rateLimiter = null;
            }
        }
    }
}
