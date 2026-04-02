namespace RdtClient.Data.Models.Internal;

public class PublicTorrentQueueItemDto
{
    public String Name { get; set; } = null!;
    public Int64 TotalSizeBytes { get; set; }
    public Double DownloadedPercent { get; set; }
    public String Status { get; set; } = null!;
    public String RawStatus { get; set; } = null!;
}
