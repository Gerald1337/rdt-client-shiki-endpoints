namespace RdtClient.Data.Models.Internal;

public class PublicTorrentQueueItemDto
{
    public String Name { get; set; } = null!;
    public Int64 TotalSizeBytes { get; set; }
    public Double DownloadedPercent { get; set; }
    public Int64 CurrentDownloadSpeedBytesPerSecond { get; set; }
    public String RawStatus { get; set; } = null!;
    public Boolean TorrentIsCached { get; set; }
}
