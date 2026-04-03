using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
    private readonly Mock<Authentication> _authenticationMock;
    private readonly TorrentsController _controller;
    private readonly Mock<IRateLimitCoordinator> _coordinatorMock;
    private readonly Mock<ILogger<TorrentsController>> _loggerMock;
    private readonly Mock<Torrents> _torrentsMock;

    public TorrentsControllerPublicQueueTest()
    {
        _torrentsMock = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        _loggerMock = new();
        _coordinatorMock = new();
        _authenticationMock = new(null!, null!, null!);
        _authenticationMock.Setup(a => a.ValidateCredentials("user", "pass"))
                           .ReturnsAsync(true);
        _controller = new(_loggerMock.Object, _torrentsMock.Object, null!, _coordinatorMock.Object, _authenticationMock.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";
        _controller.ControllerContext = new()
        {
            HttpContext = httpContext
        };
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
                             RdSpeed = 630000,
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
        Assert.Equal(630000, item.CurrentDownloadSpeedBytesPerSecond);
        Assert.Equal("Queued for downloading", item.RawStatus);
        Assert.False(item.TorrentIsCached);
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
        Assert.Equal(500, item.CurrentDownloadSpeedBytesPerSecond);
        Assert.Equal("Downloading file 1/1 (25.00% - 500 B/s)", item.RawStatus);
        Assert.True(item.TorrentIsCached);
    }

    [Fact]
    public async Task GetPublicQueue_ReturnsWaitingToDownloadWhenProviderFinishedAndNoDownloads()
    {
        var torrentId = Guid.NewGuid();

        _torrentsMock.Setup(t => t.Get())
                     .ReturnsAsync([
                         new Torrent
                         {
                             TorrentId = torrentId,
                             Hash = "hash-3",
                             RdName = "Ready Torrent",
                             RdSize = 3000,
                             RdProgress = 100,
                             RdStatus = TorrentStatus.Finished
                         }
                     ]);

        var result = await _controller.GetPublicQueue();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var queue = Assert.IsType<List<PublicTorrentQueueItemDto>>(okResult.Value);
        var item = Assert.Single(queue);

        Assert.Equal("Ready Torrent", item.Name);
        Assert.Equal(3000, item.TotalSizeBytes);
        Assert.Equal(0, item.DownloadedPercent);
        Assert.Equal(0, item.CurrentDownloadSpeedBytesPerSecond);
        Assert.Equal("Torrent finished, waiting for download links", item.RawStatus);
        Assert.True(item.TorrentIsCached);
    }

    [Fact]
    public async Task GetPublicQueue_ReturnsWaitingForDebridWhenProviderNotDownloading()
    {
        var torrentId = Guid.NewGuid();

        _torrentsMock.Setup(t => t.Get())
                     .ReturnsAsync([
                         new Torrent
                         {
                             TorrentId = torrentId,
                             Hash = "hash-5",
                             RdName = "Stalled Torrent",
                             RdSize = 5000,
                             RdProgress = 42,
                             RdStatus = TorrentStatus.Processing
                         }
                     ]);

        var result = await _controller.GetPublicQueue();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var queue = Assert.IsType<List<PublicTorrentQueueItemDto>>(okResult.Value);
        var item = Assert.Single(queue);

        Assert.Equal("Stalled Torrent", item.Name);
        Assert.Equal(42, item.DownloadedPercent);
        Assert.Equal(0, item.CurrentDownloadSpeedBytesPerSecond);
        Assert.Equal("Torrent processing", item.RawStatus);
        Assert.False(item.TorrentIsCached);
    }

    [Fact]
    public async Task GetPublicQueue_ReturnsWaitingInQueueForQueuedDownloads()
    {
        var torrentId = Guid.NewGuid();
        var downloadId = Guid.NewGuid();

        _torrentsMock.Setup(t => t.Get())
                     .ReturnsAsync([
                         new Torrent
                         {
                             TorrentId = torrentId,
                             Hash = "hash-4",
                             RdName = "Queued Torrent",
                             RdSize = 4000,
                             RdProgress = 100,
                             RdStatus = TorrentStatus.Finished,
                             Downloads =
                             [
                                 new Download
                                 {
                                     DownloadId = downloadId,
                                     TorrentId = torrentId,
                                     Path = "/downloads/queued.mkv",
                                     DownloadQueued = DateTimeOffset.UtcNow
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

        Assert.Equal("Queued Torrent", item.Name);
        Assert.Equal(0, item.DownloadedPercent);
        Assert.Equal(0, item.CurrentDownloadSpeedBytesPerSecond);
        Assert.Equal("Queued for downloading", item.RawStatus);
        Assert.True(item.TorrentIsCached);
    }

    [Fact]
    public async Task GetPublicQueue_PrioritizesActiveDownloadsOverQueueStatus()
    {
        var torrentId = Guid.NewGuid();
        var activeDownloadId = Guid.NewGuid();
        var queuedDownloadId = Guid.NewGuid();

        _torrentsMock.Setup(t => t.Get())
                     .ReturnsAsync([
                         new Torrent
                         {
                             TorrentId = torrentId,
                             Hash = "hash-6",
                             RdName = "Mixed Torrent",
                             RdSize = 6000,
                             RdProgress = 100,
                             RdStatus = TorrentStatus.Finished,
                             Downloads =
                             [
                                 new Download
                                 {
                                     DownloadId = activeDownloadId,
                                     TorrentId = torrentId,
                                     Path = "/downloads/active.mkv",
                                     DownloadQueued = DateTimeOffset.UtcNow.AddMinutes(-5),
                                     DownloadStarted = DateTimeOffset.UtcNow.AddMinutes(-3)
                                 },
                                 new Download
                                 {
                                     DownloadId = queuedDownloadId,
                                     TorrentId = torrentId,
                                     Path = "/downloads/queued2.mkv",
                                     DownloadQueued = DateTimeOffset.UtcNow.AddMinutes(-1)
                                 }
                             ]
                         }
                     ]);

        _torrentsMock.Setup(t => t.GetDownloadStats(activeDownloadId))
                     .Returns((1000, 2000, 1000));
        _torrentsMock.Setup(t => t.GetDownloadStats(queuedDownloadId))
                     .Returns((0, 0, 0));

        var result = await _controller.GetPublicQueue();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var queue = Assert.IsType<List<PublicTorrentQueueItemDto>>(okResult.Value);
        var item = Assert.Single(queue);

        Assert.Equal("Mixed Torrent", item.Name);
        Assert.Equal(25, item.DownloadedPercent);
        Assert.Equal(1000, item.CurrentDownloadSpeedBytesPerSecond);
        Assert.Equal("Downloading file 1/2 (50.00% - 1000 B/s)", item.RawStatus);
        Assert.True(item.TorrentIsCached);
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
        Assert.Equal(0, item.CurrentDownloadSpeedBytesPerSecond);
        Assert.False(item.TorrentIsCached);
    }
}
