using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;
using RdtClient.Web.Controllers;

namespace RdtClient.Web.Test.Controllers;

public class TorrentsControllerPublicQueueTest
{
    private readonly TorrentsController _controller;
    private readonly Mock<IRateLimitCoordinator> _coordinatorMock;
    private readonly Mock<ILogger<TorrentsController>> _loggerMock;
    private readonly Mock<Torrents> _torrentsMock;

    public TorrentsControllerPublicQueueTest()
    {
        _torrentsMock = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        _loggerMock = new();
        _coordinatorMock = new();
        _controller = new(_loggerMock.Object, _torrentsMock.Object, null!, _coordinatorMock.Object);
    }

    [Fact]
    public void GetPublicQueue_HasAllowAnonymousAttribute()
    {
        var type = typeof(TorrentsController);
        var method = type.GetMethod(nameof(TorrentsController.GetPublicQueue));

        var attribute = method?.GetCustomAttributes(typeof(AllowAnonymousAttribute), true).FirstOrDefault();

        Assert.NotNull(attribute);
    }

    [Fact]
    public async Task GetPublicQueue_ReturnsProviderProgressWhenHostDownloadHasNotStarted()
    {
        var torrentId = Guid.NewGuid();
        var downloadId = Guid.NewGuid();

        _torrentsMock.Setup(t => t.Get())
                     .ReturnsAsync([
                         new Torrent
                         {
                             TorrentId = torrentId,
                             Hash = "hash-1",
                             RdName = "Ubuntu ISO",
                             RdSize = 1000,
                             RdProgress = 35,
                             RdStatus = TorrentStatus.Downloading,
                             Downloads =
                             [
                                 new Download
                                 {
                                     DownloadId = downloadId,
                                     TorrentId = torrentId,
                                     Path = "/downloads/file.iso"
                                 }
                             ]
                         }
                     ]);

        _torrentsMock.Setup(t => t.GetDownloadStats(downloadId))
                     .Returns((0, 0, 0));

        var result = await _controller.GetPublicQueue();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var queue = Assert.IsType<List<PublicTorrentQueueItemDto>>(okResult.Value);
        var item = Assert.Single(queue);

        Assert.Equal("Ubuntu ISO", item.Name);
        Assert.Equal(1000, item.TotalSizeBytes);
        Assert.Equal(35, item.DownloadedPercent);
        Assert.Equal("Being downloaded by RealDebrid", item.Status);
    }

    [Fact]
    public async Task GetPublicQueue_ReturnsLocalDownloadProgressWhenHostDownloadHasStarted()
    {
        var torrentId = Guid.NewGuid();
        var activeDownloadId = Guid.NewGuid();

        _torrentsMock.Setup(t => t.Get())
                     .ReturnsAsync([
                         new Torrent
                         {
                             TorrentId = torrentId,
                             Hash = "hash-2",
                             RdName = "Movie Pack",
                             RdSize = 5000,
                             RdProgress = 100,
                             RdStatus = TorrentStatus.Finished,
                             Downloads =
                             [
                                 new Download
                                 {
                                     DownloadId = activeDownloadId,
                                     TorrentId = torrentId,
                                     Path = "/downloads/movie.mkv",
                                     DownloadQueued = DateTimeOffset.UtcNow.AddMinutes(-2),
                                     DownloadStarted = DateTimeOffset.UtcNow.AddMinutes(-1)
                                 }
                             ]
                         }
                     ]);

        _torrentsMock.Setup(t => t.GetDownloadStats(activeDownloadId))
                     .Returns((500, 2000, 500));

        var result = await _controller.GetPublicQueue();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var queue = Assert.IsType<List<PublicTorrentQueueItemDto>>(okResult.Value);
        var item = Assert.Single(queue);

        Assert.Equal("Movie Pack", item.Name);
        Assert.Equal(2000, item.TotalSizeBytes);
        Assert.Equal(25, item.DownloadedPercent);
        Assert.Equal("Being downloaded from RealDebrid", item.Status);
    }

    [Fact]
    public async Task GetPublicQueue_FiltersOutCompletedAndErroredTorrents()
    {
        _torrentsMock.Setup(t => t.Get())
                     .ReturnsAsync([
                         new Torrent
                         {
                             TorrentId = Guid.NewGuid(),
                             Hash = "active",
                             RdName = "Active Torrent",
                             RdSize = 100,
                             RdStatus = TorrentStatus.Queued
                         },
                         new Torrent
                         {
                             TorrentId = Guid.NewGuid(),
                             Hash = "completed",
                             RdName = "Completed Torrent",
                             Completed = DateTimeOffset.UtcNow,
                             RdStatus = TorrentStatus.Finished
                         },
                         new Torrent
                         {
                             TorrentId = Guid.NewGuid(),
                             Hash = "error",
                             RdName = "Errored Torrent",
                             Error = "boom",
                             RdStatus = TorrentStatus.Error
                         }
                     ]);

        var result = await _controller.GetPublicQueue();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var queue = Assert.IsType<List<PublicTorrentQueueItemDto>>(okResult.Value);
        var item = Assert.Single(queue);

        Assert.Equal("Active Torrent", item.Name);
        Assert.Equal("Not yet added to provider", item.Status);
    }
}
