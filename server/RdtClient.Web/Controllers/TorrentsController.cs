using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MonoTorrent;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.DebridClient;
using RdtClient.Data.Models.Internal;
using RdtClient.Service;
using RdtClient.Service.BackgroundServices;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;
using Torrent = RdtClient.Data.Models.Data.Torrent;

namespace RdtClient.Web.Controllers;

[Authorize(Policy = "AuthSetting")]
[Route("Api/Torrents")]
public class TorrentsController(ILogger<TorrentsController> logger, Torrents torrents, TorrentRunner torrentRunner, IRateLimitCoordinator coordinator, Authentication authentication) : Controller
{
    private static readonly Regex DownloadingFilesRegex = new(@"^Downloading \d+/\d+ files \(\d+(?:\.\d+)?% - .+/s\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExtractingFilesRegex = new(@"^Extracting \d+/\d+ files \(\d+(?:\.\d+)?%\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TorrentDownloadingRegex = new(@"^Torrent downloading \(\d+(?:\.\d+)?% - .+/s\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TorrentErrorRegex = new(@"^Torrent error: .+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [HttpGet]
    [AllowAnonymous]
    [Route("~/Api/ShikiDashboard/Queue/Public")]
    public async Task<ActionResult<IList<PublicTorrentQueueItemDto>>> GetPublicQueue()
    {
        if (!await IsShikiDashboardAuthorized())
        {
            AddShikiDashboardAuthChallenge();
            return Unauthorized();
        }

        var results = await torrents.Get();

        var queue = results.Where(IsPublicQueueCandidate)
                           .Select(MapPublicQueueItem)
                           .OrderBy(item => item.Name)
                           .ToList();

        return Ok(queue);
    }

    [HttpPost]
    [AllowAnonymous]
    [Route("~/Api/ShikiDashboard/EditQueue/Remove")]
    public async Task<ActionResult<Boolean>> RemoveFromShikiDashboardQueue([FromBody] ShikiDashboardEditQueueRequest? request)
    {
        if (!await IsShikiDashboardAuthorized())
        {
            AddShikiDashboardAuthChallenge();
            return Unauthorized();
        }

        if (request == null || request.TorrentId == Guid.Empty)
        {
            return Ok(false);
        }

        logger.LogDebug("Removing torrent {torrentId} from Shiki Dashboard", request.TorrentId);

        try
        {
            await torrents.Delete(request.TorrentId, true, true, true);
            return Ok(true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove torrent {torrentId} from Shiki Dashboard", request.TorrentId);
            return Ok(false);
        }
    }

    [HttpPost]
    [AllowAnonymous]
    [Route("~/Api/ShikiDashboard/EditQueue/Retry")]
    public async Task<ActionResult<Boolean>> RetryFromShikiDashboardQueue([FromBody] ShikiDashboardEditQueueRequest? request)
    {
        if (!await IsShikiDashboardAuthorized())
        {
            AddShikiDashboardAuthChallenge();
            return Unauthorized();
        }

        if (request == null || request.TorrentId == Guid.Empty)
        {
            return Ok(false);
        }

        logger.LogDebug("Retrying torrent {torrentId} from Shiki Dashboard", request.TorrentId);

        try
        {
            await torrents.UpdateRetry(request.TorrentId, DateTimeOffset.UtcNow, 0);
            await torrents.RetryTorrent(request.TorrentId, 0);
            return Ok(true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to retry torrent {torrentId} from Shiki Dashboard", request.TorrentId);
            return Ok(false);
        }
    }

    [HttpPost]
    [AllowAnonymous]
    [Route("~/Api/ShikiDashboard/IngestMagnetLink")]
    public async Task<ActionResult<Boolean>> AddMagnetFromShikiDashboard([FromBody] ShikiDashboardIngestRequest? request)
    {
        if (!await IsShikiDashboardAuthorized())
        {
            AddShikiDashboardAuthChallenge();
            return Unauthorized();
        }

        if (request == null || String.IsNullOrWhiteSpace(request.MagnetLink))
        {
            return Ok(false);
        }

        var defaults = Settings.Get.Gui.Default;

        var torrent = new Torrent
        {
            DownloadClient = Settings.Get.DownloadClient.Client,
            Category = defaults.Category,
            HostDownloadAction = defaults.HostDownloadAction,
            FinishedActionDelay = defaults.FinishedActionDelay,
            DownloadAction = defaults.OnlyDownloadAvailableFiles
                ? TorrentDownloadAction.DownloadAvailableFiles
                : TorrentDownloadAction.DownloadAll,
            FinishedAction = defaults.FinishedAction,
            DownloadMinSize = defaults.MinFileSize,
            IncludeRegex = defaults.IncludeRegex,
            ExcludeRegex = defaults.ExcludeRegex,
            TorrentRetryAttempts = defaults.TorrentRetryAttempts,
            DownloadRetryAttempts = defaults.DownloadRetryAttempts,
            DeleteOnError = defaults.DeleteOnError,
            Lifetime = defaults.TorrentLifetime,
            Priority = defaults.Priority > 0 ? defaults.Priority : null
        };

        logger.LogDebug("Ingesting magnet from Shiki Dashboard");

        try
        {
            await torrents.AddMagnetToDebridQueue(request.MagnetLink.Trim(), torrent);
            return Ok(true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to ingest magnet from Shiki Dashboard");
            return Ok(false);
        }
    }

    [HttpGet]
    [Route("")]
    public async Task<ActionResult<IList<TorrentDto>>> GetAll()
    {
        var results = await torrents.Get();

        var torrentDtos = results.Select(torrent => new TorrentDto
                                 {
                                     TorrentId = torrent.TorrentId,
                                     Hash = torrent.Hash,
                                     Category = torrent.Category,
                                     DownloadAction = torrent.DownloadAction,
                                     FinishedAction = torrent.FinishedAction,
                                     FinishedActionDelay = torrent.FinishedActionDelay,
                                     HostDownloadAction = torrent.HostDownloadAction,
                                     DownloadMinSize = torrent.DownloadMinSize,
                                     IncludeRegex = torrent.IncludeRegex,
                                     ExcludeRegex = torrent.ExcludeRegex,
                                     DownloadManualFiles = torrent.DownloadManualFiles,
                                     DownloadClient = torrent.DownloadClient,
                                     Added = torrent.Added,
                                     FilesSelected = torrent.FilesSelected,
                                     Completed = torrent.Completed,
                                     Type = torrent.Type,
                                     IsFile = torrent.IsFile,
                                     Priority = torrent.Priority,
                                     RetryCount = torrent.RetryCount,
                                     DownloadRetryAttempts = torrent.DownloadRetryAttempts,
                                     TorrentRetryAttempts = torrent.TorrentRetryAttempts,
                                     DeleteOnError = torrent.DeleteOnError,
                                     Lifetime = torrent.Lifetime,
                                     Error = torrent.Error,
                                     RdId = torrent.RdId,
                                     RdName = torrent.RdName,
                                     RdSize = torrent.RdSize,
                                     RdHost = torrent.RdHost,
                                     RdSplit = torrent.RdSplit,
                                     RdProgress = torrent.RdProgress,
                                     RdStatus = torrent.RdStatus,
                                     RdStatusRaw = torrent.RdStatusRaw,
                                     RdAdded = torrent.RdAdded,
                                     RdEnded = torrent.RdEnded,
                                     RdSpeed = torrent.RdSpeed,
                                     RdSeeders = torrent.RdSeeders,
                                     Files = torrent.Files,
                                     Downloads = torrent.Downloads.Select(download =>
                                                        {
                                                            var (speed, bytesTotal, bytesDone) = torrents.GetDownloadStats(download.DownloadId);

                                                            return new DownloadDto
                                                            {
                                                                DownloadId = download.DownloadId,
                                                                TorrentId = download.TorrentId,
                                                                Path = download.Path,
                                                                Link = download.Link,
                                                                Added = download.Added,
                                                                DownloadQueued = download.DownloadQueued,
                                                                DownloadStarted = download.DownloadStarted,
                                                                DownloadFinished = download.DownloadFinished,
                                                                UnpackingQueued = download.UnpackingQueued,
                                                                UnpackingStarted = download.UnpackingStarted,
                                                                UnpackingFinished = download.UnpackingFinished,
                                                                Completed = download.Completed,
                                                                RetryCount = download.RetryCount,
                                                                Error = download.Error,
                                                                BytesTotal = bytesTotal,
                                                                BytesDone = bytesDone,
                                                                Speed = speed
                                                            };
                                                        })
                                                        .ToList()
                                 })
                                 .ToList();

        return Ok(torrentDtos);
    }

    [HttpGet]
    [Route("Get/{torrentId:guid}")]
    public async Task<ActionResult<TorrentDto>> GetById(Guid torrentId)
    {
        var torrent = await torrents.GetById(torrentId);

        if (torrent == null)
        {
            return NotFound();
        }

        foreach (var file in torrent.Downloads)
        {
            file.Torrent = null;
        }

        var torrentDto = new TorrentDto
        {
            TorrentId = torrent!.TorrentId,
            Hash = torrent.Hash,
            Category = torrent.Category,
            DownloadAction = torrent.DownloadAction,
            FinishedAction = torrent.FinishedAction,
            FinishedActionDelay = torrent.FinishedActionDelay,
            HostDownloadAction = torrent.HostDownloadAction,
            DownloadMinSize = torrent.DownloadMinSize,
            IncludeRegex = torrent.IncludeRegex,
            ExcludeRegex = torrent.ExcludeRegex,
            DownloadManualFiles = torrent.DownloadManualFiles,
            DownloadClient = torrent.DownloadClient,
            Added = torrent.Added,
            FilesSelected = torrent.FilesSelected,
            Completed = torrent.Completed,
            Type = torrent.Type,
            IsFile = torrent.IsFile,
            Priority = torrent.Priority,
            RetryCount = torrent.RetryCount,
            DownloadRetryAttempts = torrent.DownloadRetryAttempts,
            TorrentRetryAttempts = torrent.TorrentRetryAttempts,
            DeleteOnError = torrent.DeleteOnError,
            Lifetime = torrent.Lifetime,
            Error = torrent.Error,
            RdId = torrent.RdId,
            RdName = torrent.RdName,
            RdSize = torrent.RdSize,
            RdHost = torrent.RdHost,
            RdSplit = torrent.RdSplit,
            RdProgress = torrent.RdProgress,
            RdStatus = torrent.RdStatus,
            RdStatusRaw = torrent.RdStatusRaw,
            RdAdded = torrent.RdAdded,
            RdEnded = torrent.RdEnded,
            RdSpeed = torrent.RdSpeed,
            RdSeeders = torrent.RdSeeders,
            Files = torrent.Files,
            Downloads = torrent.Downloads.Select(download =>
                               {
                                   var (speed, bytesTotal, bytesDone) = torrents.GetDownloadStats(download.DownloadId);

                                   return new DownloadDto
                                   {
                                       DownloadId = download.DownloadId,
                                       TorrentId = download.TorrentId,
                                       Path = download.Path,
                                       Link = download.Link,
                                       Added = download.Added,
                                       DownloadQueued = download.DownloadQueued,
                                       DownloadStarted = download.DownloadStarted,
                                       DownloadFinished = download.DownloadFinished,
                                       UnpackingQueued = download.UnpackingQueued,
                                       UnpackingStarted = download.UnpackingStarted,
                                       UnpackingFinished = download.UnpackingFinished,
                                       Completed = download.Completed,
                                       RetryCount = download.RetryCount,
                                       Error = download.Error,
                                       BytesTotal = bytesTotal,
                                       BytesDone = bytesDone,
                                       Speed = speed
                                   };
                               })
                               .ToList()
        };

        return Ok(torrentDto);
    }

    [HttpGet]
    [Route("DiskSpaceStatus")]
    public ActionResult<DiskSpaceStatus?> GetDiskSpaceStatus()
    {
        var status = DiskSpaceMonitor.GetCurrentStatus();

        return Ok(status);
    }

    [HttpGet]
    [Route("RateLimitStatus")]
    public ActionResult<RateLimitStatus> GetRateLimitStatus()
    {
        var nextDequeueTime = coordinator.GetMaxNextAllowedAt();

        if (nextDequeueTime == null || nextDequeueTime < DateTimeOffset.Now)
        {
            return Ok(new RateLimitStatus
            {
                NextDequeueTime = null,
                SecondsRemaining = 0
            });
        }

        return Ok(new RateLimitStatus
        {
            NextDequeueTime = nextDequeueTime,
            SecondsRemaining = (nextDequeueTime.Value - DateTimeOffset.Now).TotalSeconds
        });
    }

    /// <summary>
    ///     Used for debugging only. Force a tick.
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [Route("Tick")]
    public async Task<ActionResult> Tick()
    {
        await torrentRunner.Tick();

        return Ok();
    }

    [HttpPost]
    [Route("UploadFile")]
    public async Task<ActionResult> UploadFile([FromForm] IFormFile? file,
                                               [ModelBinder(BinderType = typeof(JsonModelBinder))]
                                               TorrentControllerUploadFileRequest? formData)
    {
        if (file == null || file.Length <= 0)
        {
            return BadRequest("Invalid torrent file");
        }

        if (formData?.Torrent == null)
        {
            return BadRequest("Invalid Torrent");
        }

        logger.LogDebug($"Add file");

        var fileStream = file.OpenReadStream();

        await using var memoryStream = new MemoryStream();

        await fileStream.CopyToAsync(memoryStream);

        var bytes = memoryStream.ToArray();

        await torrents.AddFileToDebridQueue(bytes, formData.Torrent);

        return Ok();
    }

    [HttpPost]
    [Route("UploadMagnet")]
    public async Task<ActionResult> UploadMagnet([FromBody] TorrentControllerUploadMagnetRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        if (String.IsNullOrEmpty(request.MagnetLink))
        {
            return BadRequest("Invalid magnet link");
        }

        if (request.Torrent == null)
        {
            return BadRequest("Invalid Torrent");
        }

        logger.LogDebug($"Add magnet");

        await torrents.AddMagnetToDebridQueue(request.MagnetLink, request.Torrent);

        return Ok();
    }

    [HttpPost]
    [Route("UploadNzbFile")]
    public async Task<ActionResult> UploadNzbFile([FromForm] IFormFile? file,
                                                  [ModelBinder(BinderType = typeof(JsonModelBinder))]
                                                  TorrentControllerUploadFileRequest? formData)
    {
        if (file == null || file.Length <= 0)
        {
            return BadRequest("Invalid nzb file");
        }

        if (formData?.Torrent == null)
        {
            return BadRequest("Invalid Torrent");
        }

        logger.LogDebug($"Add nzb file");

        if (String.IsNullOrWhiteSpace(formData.Torrent.RdName))
        {
            formData.Torrent.RdName = file.FileName;
        }

        var fileStream = file.OpenReadStream();

        await using var memoryStream = new MemoryStream();

        await fileStream.CopyToAsync(memoryStream);

        var bytes = memoryStream.ToArray();

        await torrents.AddNzbFileToDebridQueue(bytes, file.FileName, formData.Torrent);

        return Ok();
    }

    [HttpPost]
    [Route("UploadNzbLink")]
    public async Task<ActionResult> UploadNzbLink([FromBody] TorrentControllerUploadNzbLinkRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        if (String.IsNullOrEmpty(request.NzbLink))
        {
            return BadRequest("Invalid nzb link");
        }

        if (request.Torrent == null)
        {
            return BadRequest("Invalid Torrent");
        }

        logger.LogDebug($"Add nzb link {request.NzbLink}");

        await torrents.AddNzbLinkToDebridQueue(request.NzbLink, request.Torrent);

        return Ok();
    }

    [HttpPost]
    [Route("CheckFiles")]
    public async Task<ActionResult> CheckFiles([FromForm] IFormFile? file)
    {
        if (file == null || file.Length <= 0)
        {
            return BadRequest("Invalid torrent file");
        }

        var fileStream = file.OpenReadStream();

        await using var memoryStream = new MemoryStream();

        await fileStream.CopyToAsync(memoryStream);

        var bytes = memoryStream.ToArray();

        var torrent = await MonoTorrent.Torrent.LoadAsync(bytes);

        var result = await torrents.GetAvailableFiles(torrent.InfoHashes.V1OrV2.ToHex());

        return Ok(result);
    }

    [HttpPost]
    [Route("CheckFilesMagnet")]
    public async Task<ActionResult> CheckFilesMagnet([FromBody] TorrentControllerCheckFilesRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        if (String.IsNullOrEmpty(request.MagnetLink))
        {
            return BadRequest("MagnetLink cannot be null or empty");
        }

        var magnet = MagnetLink.Parse(request.MagnetLink);

        var result = await torrents.GetAvailableFiles(magnet.InfoHashes.V1OrV2.ToHex());

        return Ok(result);
    }

    [HttpPost]
    [Route("Delete/{torrentId:guid}")]
    public async Task<ActionResult> Delete(Guid torrentId, [FromBody] TorrentControllerDeleteRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        logger.LogDebug("Delete {torrentId}", torrentId);

        await torrents.Delete(torrentId, request.DeleteData, request.DeleteRdTorrent, request.DeleteLocalFiles);

        return Ok();
    }

    [HttpPost]
    [Route("Retry/{torrentId:guid}")]
    public async Task<ActionResult> Retry(Guid torrentId)
    {
        logger.LogDebug("Retry {torrentId}", torrentId);

        await torrents.UpdateRetry(torrentId, DateTimeOffset.UtcNow, 0);
        await torrents.RetryTorrent(torrentId, 0);

        return Ok();
    }

    [HttpPost]
    [Route("RetryDownload/{downloadId:guid}")]
    public async Task<ActionResult> RetryDownload(Guid downloadId)
    {
        logger.LogDebug("Retry download {downloadId}", downloadId);

        await torrents.RetryDownload(downloadId);

        return Ok();
    }

    [HttpPut]
    [Route("Update")]
    public async Task<ActionResult> Update([FromBody] Torrent? torrent)
    {
        if (torrent == null)
        {
            return BadRequest();
        }

        await torrents.Update(torrent);

        return Ok();
    }

    [HttpPost]
    [Route("VerifyRegex")]
    public async Task<ActionResult> VerifyRegex([FromForm] IFormFile? file, [FromBody] TorrentControllerVerifyRegexRequest? request)
    {
        if (request == null)
        {
            return Ok();
        }

        var includeError = "";
        var excludeError = "";

        IList<DebridClientAvailableFile> availableFiles;

        if (!String.IsNullOrWhiteSpace(request.MagnetLink))
        {
            var magnet = MagnetLink.Parse(request.MagnetLink);

            availableFiles = await torrents.GetAvailableFiles(magnet.InfoHashes.V1OrV2.ToHex());
        }
        else if (file != null)
        {
            var fileStream = file.OpenReadStream();

            await using var memoryStream = new MemoryStream();

            await fileStream.CopyToAsync(memoryStream);

            var bytes = memoryStream.ToArray();

            var torrent = await MonoTorrent.Torrent.LoadAsync(bytes);

            availableFiles = await torrents.GetAvailableFiles(torrent.InfoHashes.V1OrV2.ToHex());
        }
        else
        {
            return BadRequest();
        }

        var selectedFiles = new List<DebridClientAvailableFile>();

        if (!String.IsNullOrWhiteSpace(request.IncludeRegex))
        {
            foreach (var availableFile in availableFiles)
            {
                try
                {
                    if (Regex.IsMatch(availableFile.Filename, request.IncludeRegex))
                    {
                        selectedFiles.Add(availableFile);
                    }
                }
                catch (Exception ex)
                {
                    includeError = ex.Message;
                }
            }
        }
        else if (!String.IsNullOrWhiteSpace(request.ExcludeRegex))
        {
            foreach (var availableFile in availableFiles)
            {
                try
                {
                    if (!Regex.IsMatch(availableFile.Filename, request.ExcludeRegex))
                    {
                        selectedFiles.Add(availableFile);
                    }
                }
                catch (Exception ex)
                {
                    excludeError = ex.Message;
                }
            }
        }
        else
        {
            selectedFiles = [.. availableFiles];
        }

        return Ok(new
        {
            includeError,
            excludeError,
            selectedFiles
        });
    }

    private static Boolean IsPublicQueueCandidate(Torrent torrent)
    {
        return torrent.Completed == null && String.IsNullOrWhiteSpace(torrent.Error);
    }

    private PublicTorrentQueueItemDto MapPublicQueueItem(Torrent torrent)
    {
        var downloadStats = torrent.Downloads.Select(download =>
                                     {
                                         var (speed, bytesTotal, bytesDone) = torrents.GetDownloadStats(download.DownloadId);

                                         return new DownloadDto
                                         {
                                             DownloadId = download.DownloadId,
                                             TorrentId = download.TorrentId,
                                             Path = download.Path,
                                             Link = download.Link,
                                             Added = download.Added,
                                             DownloadQueued = download.DownloadQueued,
                                             DownloadStarted = download.DownloadStarted,
                                             DownloadFinished = download.DownloadFinished,
                                             UnpackingQueued = download.UnpackingQueued,
                                             UnpackingStarted = download.UnpackingStarted,
                                             UnpackingFinished = download.UnpackingFinished,
                                             Completed = download.Completed,
                                             RetryCount = download.RetryCount,
                                             Error = download.Error,
                                             BytesTotal = bytesTotal,
                                             BytesDone = bytesDone,
                                             Speed = speed
                                         };
                                     })
                                     .ToList();

        var activeLocalDownloads = downloadStats.Where(download => download.Completed == null && download.DownloadFinished == null && download.Error == null)
                                                .ToList();
        var completedFilesCount = downloadStats.Count(download => download.DownloadFinished != null && download.Error == null);
        var activeFilesCount = downloadStats.Count(download => download.DownloadStarted != null && download.DownloadFinished == null && download.Error == null);
        var queuedFilesCount = downloadStats.Count(download => download.DownloadQueued != null && download.DownloadStarted == null && download.Error == null);

        var allBytesTotal = downloadStats.Sum(download => download.BytesTotal);
        var allBytesDone = downloadStats.Sum(download => download.BytesDone);
        var hasLocalDownloadPhase = activeLocalDownloads.Any(download => download.DownloadQueued != null || download.DownloadStarted != null);
        var hasTrackedHostDownloadState = downloadStats.Any(download =>
            download.DownloadQueued != null ||
            download.DownloadStarted != null ||
            download.DownloadFinished != null ||
            download.UnpackingQueued != null ||
            download.UnpackingStarted != null ||
            download.UnpackingFinished != null ||
            download.Completed != null);
        var waitingForHostDownload = torrent.RdStatus == TorrentStatus.Finished && torrent.Downloads.Count == 0;
        var hostDownloadsActive = downloadStats.Any(download => download.DownloadStarted != null && download.DownloadFinished == null && download.Error == null);
        var queuedForHostDownload = !hostDownloadsActive && downloadStats.Any(download => download.DownloadQueued != null && download.DownloadStarted == null && download.Error == null);
        var providerDownloading = torrent.RdStatus == TorrentStatus.Downloading;
        var currentDownloadSpeedBytesPerSecond = providerDownloading && !hasLocalDownloadPhase
            ? torrent.RdSpeed ?? 0
            : activeLocalDownloads.Sum(download => download.Speed);

        var formattedStatus = GetTorrentStatusText(torrent, downloadStats);
        var normalizedStatus = NormalizeQueueStatus(formattedStatus);

        var totalSizeBytes = allBytesTotal > 0 ? allBytesTotal : torrent.RdSize ?? 0;
        var downloadedPercent = CalculateDownloadedPercent(torrent, downloadStats, waitingForHostDownload, queuedForHostDownload);
        var torrentIsCached = IsTorrentCached(torrent, formattedStatus, downloadStats);
        Int32? totalFilesToDownload = hasTrackedHostDownloadState ? downloadStats.Count : null;

        return new PublicTorrentQueueItemDto
        {
            TorrentId = torrent.TorrentId,
            Name = torrent.RdName ?? torrent.Hash,
            TotalSizeBytes = totalSizeBytes,
            DownloadedPercent = downloadedPercent,
            CurrentDownloadSpeedBytesPerSecond = currentDownloadSpeedBytesPerSecond,
            RawStatus = formattedStatus,
            Status = normalizedStatus,
            TotalFilesToDownload = totalFilesToDownload,
            CompletedFilesCount = totalFilesToDownload.HasValue ? completedFilesCount : null,
            ActiveFilesCount = totalFilesToDownload.HasValue ? activeFilesCount : null,
            QueuedFilesCount = totalFilesToDownload.HasValue ? queuedFilesCount : null,
            TorrentIsCached = torrentIsCached
        };
    }

    private static Double CalculateDownloadedPercent(
        Torrent torrent,
        IReadOnlyList<DownloadDto> downloadStats,
        Boolean waitingForHostDownload,
        Boolean queuedForHostDownload)
    {
        var downloading = downloadStats.Where(download => download.DownloadStarted != null && download.DownloadFinished == null && download.BytesDone > 0).ToList();

        if (downloadStats.Count > 0 && downloading.Count > 0)
        {
            var downloaded = downloadStats.Count(download => download.DownloadFinished != null);
            var bytesDone = downloading.Sum(download => download.BytesDone);
            var bytesTotal = downloading.Sum(download => download.BytesTotal);
            var activeProgress = bytesTotal > 0 ? bytesDone / (Double)bytesTotal : 0.0;
            var totalProgress = (downloaded + (activeProgress * downloading.Count)) / downloadStats.Count * 100.0;

            return Math.Clamp(totalProgress, 0.0, 100.0);
        }

        var allBytesTotal = downloadStats.Sum(download => download.BytesTotal);
        var allBytesDone = downloadStats.Sum(download => download.BytesDone);

        return waitingForHostDownload
            ? 0.0
            : allBytesTotal > 0
                ? Math.Clamp(allBytesDone / (Double)allBytesTotal * 100.0, 0.0, 100.0)
                : queuedForHostDownload
                    ? 0.0
                    : Math.Clamp((Double)(torrent.RdProgress ?? 0), 0.0, 100.0);
    }

    private static Boolean IsTorrentCached(Torrent torrent, String rawStatus, IReadOnlyList<DownloadDto> downloadStats)
    {
        if (rawStatus.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase) ||
            rawStatus.Equals("Finished", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (torrent.Completed != null || torrent.RdStatus == TorrentStatus.Finished)
        {
            return true;
        }

        return downloadStats.Any(download =>
            download.DownloadQueued != null ||
            download.DownloadStarted != null ||
            download.DownloadFinished != null ||
            download.UnpackingQueued != null ||
            download.UnpackingStarted != null ||
            download.UnpackingFinished != null ||
            download.Completed != null);
    }

    private static String GetTorrentStatusText(Torrent torrent, IReadOnlyList<DownloadDto> downloadStats)
    {
        if (!String.IsNullOrWhiteSpace(torrent.Error))
        {
            return torrent.Error;
        }

        if (downloadStats.Count > 0)
        {
            var allFinished = downloadStats.All(download => download.Completed != null);

            if (allFinished)
            {
                return "Finished";
            }

            var downloading = downloadStats.Where(download => download.DownloadStarted != null && download.DownloadFinished == null && download.BytesDone > 0).ToList();
            var downloaded = downloadStats.Where(download => download.DownloadFinished != null).ToList();

            if (downloading.Count > 0)
            {
                var bytesDone = downloading.Sum(download => download.BytesDone);
                var bytesTotal = downloading.Sum(download => download.BytesTotal);
                var progress = bytesTotal > 0 ? (bytesDone / (Double)bytesTotal) * 100.0 : 0.0;
                var speed = downloading.Sum(download => download.Speed);
                var speedText = FormatBytes(speed);

                return $"Downloading {downloading.Count + downloaded.Count}/{downloadStats.Count} files ({progress:F2}% - {speedText}/s)";
            }

            var unpacking = downloadStats.Where(download => download.UnpackingStarted != null && download.UnpackingFinished == null && download.BytesDone > 0).ToList();
            var unpacked = downloadStats.Where(download => download.UnpackingFinished != null).ToList();

            if (unpacking.Count > 0)
            {
                var bytesDone = unpacking.Sum(download => download.BytesDone);
                var bytesTotal = unpacking.Sum(download => download.BytesTotal);
                var progress = bytesTotal > 0 ? (bytesDone / (Double)bytesTotal) * 100.0 : 0.0;

                return $"Extracting {unpacking.Count + unpacked.Count}/{downloadStats.Count} files ({progress:F2}%)";
            }

            var queuedForUnpacking = downloadStats.Where(download => download.UnpackingQueued != null && download.UnpackingStarted == null).ToList();

            if (queuedForUnpacking.Count > 0)
            {
                return "Queued for unpacking";
            }

            var queuedForDownload = downloadStats.Where(download => download.DownloadStarted == null && download.DownloadFinished == null).ToList();

            if (queuedForDownload.Count > 0)
            {
                return "Queued for downloading";
            }

            if (unpacked.Count > 0)
            {
                return "Files unpacked";
            }

            if (downloaded.Count > 0)
            {
                return "Files downloaded to host";
            }
        }

        if (torrent.Completed != null)
        {
            return "Finished";
        }

        return FormatProviderStatus(torrent);
    }

    private static String FormatProviderStatus(Torrent torrent)
    {
        switch (torrent.RdStatus)
        {
            case TorrentStatus.Queued:
                return "Not Yet Added to Provider";
            case TorrentStatus.Downloading:
            {
                if ((torrent.RdSeeders ?? 0) < 1 && torrent.Type != DownloadType.Nzb)
                {
                    return "Torrent stalled";
                }

                var speedText = FormatBytes(torrent.RdSpeed ?? 0);
                var progress = torrent.RdProgress ?? 0;

                return $"Torrent downloading ({progress}% - {speedText}/s)";
            }
            case TorrentStatus.Processing:
                return "Torrent processing";
            case TorrentStatus.WaitingForFileSelection:
                return "Torrent waiting for file selection";
            case TorrentStatus.Error:
                return $"Torrent error: {torrent.RdStatusRaw ?? "Unknown"}";
            case TorrentStatus.Finished:
                return "Torrent finished, waiting for download links";
            case TorrentStatus.Uploading:
                return "Torrent uploading";
            default:
                return "Unknown status";
        }
    }

    private static String NormalizeQueueStatus(String rawStatus)
    {
        if (DownloadingFilesRegex.IsMatch(rawStatus))
        {
            return "Downloading";
        }

        if (ExtractingFilesRegex.IsMatch(rawStatus))
        {
            return "Extracting";
        }

        if (TorrentDownloadingRegex.IsMatch(rawStatus))
        {
            return "Torrent Downloading";
        }

        if (TorrentErrorRegex.IsMatch(rawStatus))
        {
            return "Error";
        }

        return rawStatus;
    }

    private static String FormatBytes(Double bytes)
    {
        if (Double.IsNaN(bytes) || bytes <= 0)
        {
            return "0 B";
        }

        var units = new[] { "B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
        var exponent = (Int32)Math.Min(units.Length - 1, Math.Floor(Math.Log(bytes, 1024)));
        exponent = Math.Max(exponent, 0);
        var value = bytes / Math.Pow(1024, exponent);
        var formatted = value >= 100 ? value.ToString("0") : value >= 10 ? value.ToString("0.0") : value.ToString("0.##");

        return $"{formatted} {units[exponent]}";
    }

    private async Task<Boolean> IsShikiDashboardAuthorized()
    {
        var header = Request.Headers["Authorization"].FirstOrDefault();

        if (String.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        const String prefix = "Basic ";

        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var encoded = header[prefix.Length..].Trim();

        Byte[] credentialBytes;

        try
        {
            credentialBytes = Convert.FromBase64String(encoded);
        }
        catch
        {
            return false;
        }

        var credentialString = Encoding.UTF8.GetString(credentialBytes);
        var separatorIndex = credentialString.IndexOf(':');

        if (separatorIndex <= 0)
        {
            return false;
        }

        var userName = credentialString.Substring(0, separatorIndex);
        var password = credentialString[(separatorIndex + 1)..];

        if (String.IsNullOrWhiteSpace(userName) || String.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        return await authentication.ValidateCredentials(userName, password);
    }

    private void AddShikiDashboardAuthChallenge()
    {
        if (!Response.Headers.ContainsKey("WWW-Authenticate"))
        {
            Response.Headers.Add("WWW-Authenticate", "Basic realm=\"ShikiDashboard\"");
        }
    }
}

public class TorrentControllerUploadFileRequest
{
    public Torrent? Torrent { get; set; }
}

public class TorrentControllerUploadMagnetRequest
{
    public String? MagnetLink { get; set; }
    public Torrent? Torrent { get; set; }
}

public class TorrentControllerUploadNzbLinkRequest
{
    public String? NzbLink { get; set; }
    public Torrent? Torrent { get; set; }
}

public class TorrentControllerDeleteRequest
{
    public Boolean DeleteData { get; set; }
    public Boolean DeleteRdTorrent { get; set; }
    public Boolean DeleteLocalFiles { get; set; }
}

public class TorrentControllerCheckFilesRequest
{
    public String? MagnetLink { get; set; }
}

public class TorrentControllerVerifyRegexRequest
{
    public String? IncludeRegex { get; set; }
    public String? ExcludeRegex { get; set; }
    public String? MagnetLink { get; set; }
}

public class ShikiDashboardIngestRequest
{
    public String? MagnetLink { get; set; }
}

public class ShikiDashboardEditQueueRequest
{
    public Guid TorrentId { get; set; }
}
