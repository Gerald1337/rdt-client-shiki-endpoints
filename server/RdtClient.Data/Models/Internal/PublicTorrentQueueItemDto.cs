namespace RdtClient.Data.Models.Internal;

public class PublicTorrentQueueItemDto
{
    public String Name { get; set; } = null!;
    public Int64 TotalSizeBytes { get; set; }
    public Double DownloadedPercent { get; set; }
    public Int64 CurrentDownloadSpeedBytesPerSecond { get; set; }
    public String RawStatus { get; set; } = null!;
    public String Status { get; set; } = null!;
    public Int32? TotalFilesToDownload { get; set; }
    public Int32? CompletedFilesCount { get; set; }
    public Int32? ActiveFilesCount { get; set; }
    public Int32? QueuedFilesCount { get; set; }
    public Boolean TorrentIsCached { get; set; }
}
