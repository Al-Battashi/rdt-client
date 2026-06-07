using System.Globalization;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.DebridClient;
using RdtClient.Data.Models.Internal;
using RdtClient.Data.Models.QBittorrent;
using RdtClient.Service.BackgroundServices;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services.DebridClients;
using RdtClient.Service.Wrappers;
using Torrent = RdtClient.Data.Models.Data.Torrent;

namespace RdtClient.Service.Services;

public class Torrents(
    ILogger<Torrents> logger,
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    ITorrentData torrentData,
    IDownloads downloads,
    IProcessFactory processFactory,
    IFileSystem fileSystem,
    IEnricher enricher,
    AllDebridDebridClient allDebridDebridClient,
    PremiumizeDebridClient premiumizeDebridClient,
    RealDebridDebridClient realDebridDebridClient,
    DebridLinkClient debridLinkClient,
    TorBoxDebridClient torBoxDebridClient,
    ISettings settings,
    ITorrentRunnerState runnerState,
    QbittorrentFallbackClient qbittorrentFallbackClient)
{
    private static readonly SemaphoreSlim RealDebridUpdateLock = new(1, 1);

    private static readonly SemaphoreSlim TorrentResetLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    private IDebridClient DebridClient
    {
        get
        {
            return settings.Current.Provider.Provider switch
            {
                Provider.Premiumize => premiumizeDebridClient,
                Provider.RealDebrid => realDebridDebridClient,
                Provider.AllDebrid => allDebridDebridClient,
                Provider.DebridLink => debridLinkClient,
                Provider.TorBox => torBoxDebridClient,
                _ => throw new("Invalid Provider")
            };
        }
    }

    public virtual (Int64 Speed, Int64 BytesTotal, Int64 BytesDone) GetDownloadStats(Guid downloadId)
    {
        return runnerState.GetStats(downloadId);
    }

    public virtual async Task<IList<Torrent>> Get()
    {
        var torrents = await torrentData.Get();

        await SyncQbittorrentFallbackTorrents(torrents);

        return torrents;
    }

    public virtual async Task<Torrent?> GetByHash(String hash)
    {
        var torrent = await torrentData.GetByHash(hash);

        if (torrent != null && IsQbittorrentFallback(torrent))
        {
            var torrents = new List<Torrent>
            {
                torrent
            };

            await SyncQbittorrentFallbackTorrents(torrents);
            torrent = torrents.FirstOrDefault();
        }
        else if (torrent != null)
        {
            await UpdateTorrentClientData(torrent);
        }

        return torrent;
    }

    public async Task UpdateCategory(String hash, String? category)
    {
        var torrent = await torrentData.GetByHash(hash);

        if (torrent == null)
        {
            return;
        }

        Log($"Update category to {category}", torrent);

        await torrentData.UpdateCategory(torrent.TorrentId, category);
    }

    public static Boolean IsQbittorrentFallback(Torrent torrent)
    {
        return QbittorrentFallbackClient.IsFallbackTorrent(torrent);
    }

    public virtual async Task<Torrent> AddNzbLinkToDebridQueue(String nzbLink, Torrent torrent)
    {
        torrent.RdStatus = TorrentStatus.Queued;

        try
        {
            var uri = new Uri(nzbLink);
            var lastSegment = uri.Segments.LastOrDefault()?.TrimEnd('/');
            torrent.RdName = !String.IsNullOrWhiteSpace(lastSegment) ? lastSegment : "Unknown NZB";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ex.Message}, trying to parse {nzbLink}", ex.Message, nzbLink);

            throw new($"{ex.Message}, trying to parse {nzbLink}");
        }

        var nzbHash = ComputeMd5Hash(nzbLink);
        var nzbNewTorrent = await AddQueued(nzbHash, nzbLink, false, DownloadType.Nzb, torrent);
        Log($"Adding {nzbLink} with hash {nzbHash} (nzb link) to queue");

        await CopyAddedTorrent(nzbNewTorrent);

        return nzbNewTorrent;
    }

    public virtual async Task<Torrent> AddNzbFileToDebridQueue(Byte[] bytes, String? fileName, Torrent torrent)
    {
        torrent.RdName = fileName ?? "Unknown NZB";
        torrent.RdStatus = TorrentStatus.Queued;

        try
        {
            using var stream = new MemoryStream(bytes);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null
            };

            using var reader = XmlReader.Create(stream, settings);
            var doc = XDocument.Load(reader);
            var nzbNamespace = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var title = doc.Root?
                           .Elements(nzbNamespace + "head")
                           .Elements(nzbNamespace + "meta")
                           .FirstOrDefault(x => x.Attribute("type")?.Value == "name")
                           ?
                           .Value;

            if (String.IsNullOrWhiteSpace(title))
            {
                title = doc.Root?
                           .Elements(nzbNamespace + "head")
                           .Elements(nzbNamespace + "meta")
                           .FirstOrDefault(x => x.Attribute("type")?.Value == "title")
                           ?
                           .Value;
            }

            if (!String.IsNullOrWhiteSpace(title))
            {
                torrent.RdName = title.Trim();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ex.Message}, trying to parse NZB file contents", ex.Message);

            throw new($"{ex.Message}, trying to parse NZB file contents");
        }

        var nzbHash = ComputeMd5HashFromBytes(bytes);
        var nzbFileAsBase64 = Convert.ToBase64String(bytes);
        var nzbNewTorrent = await AddQueued(nzbHash, nzbFileAsBase64, true, DownloadType.Nzb, torrent);
        Log($"Adding {nzbHash} (nzb file) to queue", nzbNewTorrent);

        await CopyAddedTorrent(nzbNewTorrent);

        return nzbNewTorrent;
    }

    public virtual async Task<Torrent> AddMagnetToDebridQueue(String magnetLink, Torrent torrent)
    {
        var enriched = await enricher.EnrichMagnetLink(magnetLink);
        MagnetLink magnet;

        try
        {
            magnet = MagnetLink.Parse(magnetLink);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ex.Message}, trying to parse {magnetLink}", ex.Message, magnetLink);

            throw new($"{ex.Message}, trying to parse {magnetLink}");
        }

        if (!String.IsNullOrWhiteSpace(settings.Current.General.BannedTrackers))
        {
            var bannedTrackers = settings.Current.General.BannedTrackers.Split(',');

            foreach (var bannedTracker in bannedTrackers)
            {
                var bannedTrackerCompare = bannedTracker.Trim().ToLower();

                if (String.IsNullOrWhiteSpace(bannedTrackerCompare))
                {
                    continue;
                }

                if (magnet.AnnounceUrls != null)
                {
                    var bannedUrls = magnet.AnnounceUrls.Where(m => m.Trim().ToLower().Contains(bannedTrackerCompare)).ToList();

                    if (bannedUrls.Count > 0)
                    {
                        var bannedUrlsString = String.Join(", ", bannedUrls);

                        throw new($"Cannot add torrent, the torrent contains banned trackers: {bannedUrlsString}.");
                    }
                }
            }
        }

        torrent.RdStatus = TorrentStatus.Queued;
        torrent.RdName = magnet.Name;

        var hash = magnet.InfoHashes.V1OrV2.ToHex();
        var newTorrent = await AddQueued(hash, enriched, false, DownloadType.Torrent, torrent);

        Log($"Adding {hash} (magnet link) to queue", newTorrent);
        await CopyAddedTorrent(newTorrent);

        return newTorrent;
    }

    public virtual async Task<Torrent> AddFileToDebridQueue(Byte[] bytes, Torrent torrent)
    {
        var enriched = await enricher.EnrichTorrentBytes(bytes);

        String fileAsBase64;

        MonoTorrent.Torrent monoTorrent;

        if (enriched.SequenceEqual(bytes))
        {
            fileAsBase64 = Convert.ToBase64String(bytes);
            logger.LogDebug($"bytes {bytes}");
        }
        else
        {
            fileAsBase64 = Convert.ToBase64String(enriched);
            logger.LogDebug($"enriched bytes {enriched}");
        }

        try
        {
            monoTorrent = await MonoTorrent.Torrent.LoadAsync(bytes);
        }
        catch (Exception ex)
        {
            throw new($"{ex.Message}, trying to parse {fileAsBase64}");
        }

        if (!String.IsNullOrWhiteSpace(settings.Current.General.BannedTrackers))
        {
            var bannedTrackers = settings.Current.General.BannedTrackers.Split(',');

            foreach (var bannedTracker in bannedTrackers)
            {
                var bannedTrackerCompare = bannedTracker.Trim().ToLower();

                if (String.IsNullOrWhiteSpace(bannedTrackerCompare))
                {
                    continue;
                }

                if (!String.IsNullOrWhiteSpace(monoTorrent.Source) && monoTorrent.Source.Contains(bannedTracker))
                {
                    throw new($"Cannot add torrent, the torrent source '{monoTorrent.Source}' is a banned tracker.");
                }

                if (monoTorrent.AnnounceUrls != null)
                {
                    var bannedUrls = monoTorrent.AnnounceUrls.SelectMany(m => m).Where(m => m.Trim().ToLower().Contains(bannedTrackerCompare)).ToList();

                    if (bannedUrls.Count > 0)
                    {
                        var bannedUrlsString = String.Join(", ", bannedUrls);

                        throw new($"Cannot add torrent, the torrent contains banned trackers: {bannedUrlsString}.");
                    }
                }
            }
        }

        torrent.RdStatus = TorrentStatus.Queued;
        torrent.RdName = monoTorrent.Name;

        var hash = monoTorrent.InfoHashes.V1OrV2.ToHex();

        var newTorrent = await AddQueued(hash, fileAsBase64, true, DownloadType.Torrent, torrent);

        Log($"Adding {hash} (torrent file) to queue", newTorrent);

        await CopyAddedTorrent(newTorrent);

        return newTorrent;
    }

    private async Task CopyAddedTorrent(Torrent torrent)
    {
        if (String.IsNullOrWhiteSpace(settings.Current.General.CopyAddedTorrents) || String.IsNullOrWhiteSpace(torrent.FileOrMagnet) || String.IsNullOrWhiteSpace(torrent.RdName))
        {
            return;
        }

        try
        {
            if (!fileSystem.Directory.Exists(settings.Current.General.CopyAddedTorrents))
            {
                fileSystem.Directory.CreateDirectory(settings.Current.General.CopyAddedTorrents);
            }

            var extension = torrent.Type switch
            {
                DownloadType.Nzb => ".nzb",
                DownloadType.Torrent => torrent.IsFile ? ".torrent" : ".magnet",
                _ => throw new ArgumentException("Unexpected DownloadType")
            };

            var copyFileName = Path.Combine(settings.Current.General.CopyAddedTorrents, FileHelper.RemoveInvalidFileNameChars(torrent.RdName));

            if (!copyFileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                copyFileName += extension;
            }

            if (fileSystem.File.Exists(copyFileName))
            {
                fileSystem.File.Delete(copyFileName);
            }

            if (torrent.IsFile)
            {
                var bytes = Convert.FromBase64String(torrent.FileOrMagnet);
                await fileSystem.File.WriteAllBytesAsync(copyFileName, bytes);
            }
            else
            {
                await fileSystem.File.WriteAllTextAsync(copyFileName, torrent.FileOrMagnet);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Unable to create torrent blackhole directory: {settings.Current.General.CopyAddedTorrents}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Adds torrent in database to debrid provider and updates database accordingly.
    /// </summary>
    /// <param name="torrent">The torrent from the database to upload to the debrid provider</param>
    /// <returns>Updated torrent</returns>
    /// <exception cref="Exception">When RdId is not null or FileOrMagnet is null.</exception>
    public async Task DequeueFromDebridQueue(Torrent torrent)
    {
        if (torrent.RdId != null)
        {
            throw new("Torrent already added to debrid provider, cannot dequeue");
        }

        if (torrent.FileOrMagnet == null)
        {
            throw new("Torrent has no torrent file or magnet link");
        }

        logger.LogDebug("Adding {hash} to debrid provider {torrentInfo}", torrent.Hash, torrent.ToLog());

        await RealDebridUpdateLock.WaitAsync();

        try
        {
            String id;

            if (torrent.Type == DownloadType.Nzb)
            {
                id = torrent.IsFile
                    ? await DebridClient.AddNzbFile(Convert.FromBase64String(torrent.FileOrMagnet), torrent.RdName)
                    : await DebridClient.AddNzbLink(torrent.FileOrMagnet);
            }
            else
            {
                id = torrent.IsFile
                    ? await DebridClient.AddTorrentFile(Convert.FromBase64String(torrent.FileOrMagnet))
                    : await DebridClient.AddTorrentMagnet(torrent.FileOrMagnet);
            }

            await torrentData.UpdateRdId(torrent, id);

            await UpdateTorrentClientData(torrent);
        }
        finally
        {
            RealDebridUpdateLock.Release();
        }
    }

    public async Task<Boolean> TryFallbackToQbittorrent(Torrent torrent, Exception exception)
    {
        if (!ShouldFallbackToQbittorrent(exception))
        {
            return false;
        }

        if (!qbittorrentFallbackClient.IsEnabledAndConfigured())
        {
            logger.LogError("qBittorrent fallback was not attempted because the fallback client is not configured {torrentInfo}", torrent.ToLog());

            return false;
        }

        if (String.IsNullOrWhiteSpace(torrent.FileOrMagnet))
        {
            logger.LogError("Cannot use qBittorrent fallback because the torrent payload is missing {torrentInfo}", torrent.ToLog());

            return false;
        }

        try
        {
            return await SendToQbittorrentFallback(torrent, QbittorrentFallbackClient.FallbackStatusRaw);
        }
        catch (Exception fallbackException)
        {
            logger.LogError(fallbackException, "qBittorrent fallback failed {torrentInfo}", torrent.ToLog());

            return false;
        }
    }

    public async Task<Boolean> TryFallbackToQbittorrentOnProviderCacheMiss(Torrent torrent)
    {
        if (settings.Current.Provider.Provider != Provider.RealDebrid)
        {
            return false;
        }

        if (!settings.Current.Integrations.QbittorrentFallback.SendNonCachedToQbittorrent)
        {
            return false;
        }

        if (!qbittorrentFallbackClient.IsEnabledAndConfigured())
        {
            logger.LogError("Cannot route non-cached torrent to qBittorrent because fallback client is not configured {torrentInfo}", torrent.ToLog());

            return false;
        }

        if (String.IsNullOrWhiteSpace(torrent.FileOrMagnet))
        {
            logger.LogError("Cannot route non-cached torrent to qBittorrent because payload is missing {torrentInfo}", torrent.ToLog());

            return false;
        }

        try
        {
            return await SendToQbittorrentFallback(torrent, "qbittorrent_fallback_not_cached");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to send non-cached provider torrent to qBittorrent fallback {torrentInfo}", torrent.ToLog());

            return false;
        }
    }

    public async Task<Boolean?> IsProviderInstantlyAvailable(Torrent torrent)
    {
        if (settings.Current.Provider.Provider != Provider.RealDebrid)
        {
            return null;
        }

        if (String.IsNullOrWhiteSpace(settings.Current.Provider.ApiKey))
        {
            return null;
        }

        var cacheKey = $"provider-instant-availability:{settings.Current.Provider.Provider}:{torrent.Hash.ToLowerInvariant()}";

        if (memoryCache.TryGetValue<Boolean>(cacheKey, out var cachedValue))
        {
            return cachedValue;
        }

        try
        {
            var host = String.IsNullOrWhiteSpace(settings.Current.Provider.ApiHostname) ? "api.real-debrid.com" : settings.Current.Provider.ApiHostname!.Trim();
            var requestUri = $"https://{host}/rest/1.0/torrents/instantAvailability/{torrent.Hash.ToLowerInvariant()}";

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.Current.Provider.ApiKey);

            var client = httpClientFactory.CreateClient(DiConfig.RD_CLIENT);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(settings.Current.Provider.Timeout, 5));

            using var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Instant availability lookup failed with status {statusCode} {torrentInfo}", (Int32)response.StatusCode, torrent.ToLog());

                return null;
            }

            var body = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);

            var isAvailable = ParseInstantAvailability(doc.RootElement, torrent.Hash);

            if (isAvailable.HasValue)
            {
                memoryCache.Set(cacheKey, isAvailable.Value, TimeSpan.FromSeconds(45));
            }

            return isAvailable;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to determine instant provider availability {torrentInfo}", torrent.ToLog());

            return null;
        }
    }

    public async Task<IList<DebridClientAvailableFile>> GetAvailableFiles(String hash)
    {
        var result = await DebridClient.GetAvailableFiles(hash);

        return result;
    }

    public async Task SelectFiles(Guid torrentId)
    {
        var torrent = await GetById(torrentId);

        if (torrent == null)
        {
            return;
        }

        var selected = await DebridClient.SelectFiles(torrent);

        if (selected == 0)
        {
            await MarkAllFilesExcluded(torrent);
        }
    }

    public async Task CreateDownloads(Guid torrentId)
    {
        var torrent = await GetById(torrentId);

        if (torrent == null)
        {
            return;
        }

        var downloadInfos = await DebridClient.GetDownloadInfos(torrent);

        if (downloadInfos == null)
        {
            return;
        }

        if (downloadInfos.Count == 0)
        {
            await MarkAllFilesExcluded(torrent);

            return;
        }

        foreach (var downloadInfo in downloadInfos)
        {
            var addResult = await downloads.TryAddForTorrent(torrent.TorrentId, downloadInfo);

            switch (addResult)
            {
                case DownloadAddResult.Added:
                case DownloadAddResult.AlreadyExists:
                    continue;
                case DownloadAddResult.TorrentMissing:
                    logger.LogDebug("Stopping download creation because the torrent was deleted concurrently. TorrentId: {torrentId}", torrent.TorrentId);

                    return;
                case DownloadAddResult.InvalidInput:
                    logger.LogDebug("Skipping download creation because the provider returned an invalid download link. TorrentId: {torrentId}", torrent.TorrentId);

                    continue;
                default:
                    throw new ArgumentOutOfRangeException(nameof(addResult), addResult, null);
            }
        }
    }

    /// <summary>
    ///     Logs a message to the console, sets the error on the torrent and ensures it is not retried.
    /// </summary>
    /// <param name="torrent">The torrent to mark as "All files excluded"</param>
    private async Task MarkAllFilesExcluded(Torrent torrent)
    {
        logger.LogInformation("All files excluded by filters (IncludeRegex: {includeRegex}, ExcludeRegex: {excludeRegex}, DownloadMinSize: {downloadMinSize}) {torrentInfo}",
                              torrent.IncludeRegex,
                              torrent.ExcludeRegex,
                              torrent.DownloadMinSize,
                              torrent.ToLog());

        await torrentData.UpdateRetry(torrent.TorrentId, null, torrent.TorrentRetryAttempts);
        await torrentData.UpdateComplete(torrent.TorrentId, "All files excluded", DateTimeOffset.Now, false);
    }

    public virtual async Task Delete(Guid torrentId, Boolean deleteData, Boolean deleteRdTorrent, Boolean deleteLocalFiles)
    {
        var torrent = await GetById(torrentId);

        if (torrent == null)
        {
            return;
        }

        Log($"Deleting", torrent);

        if (IsQbittorrentFallback(torrent))
        {
            var shouldDeleteFromQbittorrent = deleteRdTorrent || deleteLocalFiles;

            if (shouldDeleteFromQbittorrent)
            {
                if (!qbittorrentFallbackClient.IsEnabledAndConfigured())
                {
                    throw new("qBittorrent fallback is not configured, unable to delete torrent from qBittorrent.");
                }

                await qbittorrentFallbackClient.Delete(torrent.Hash, deleteLocalFiles);
            }

            if (deleteData || shouldDeleteFromQbittorrent)
            {
                await downloads.DeleteForTorrent(torrent.TorrentId);
                await torrentData.Delete(torrent.TorrentId);
            }

            return;
        }

        await UpdateComplete(torrentId, "Torrent deleted", DateTimeOffset.UtcNow, false);

        foreach (var download in torrent.Downloads)
        {
            var retry = 10;

            while (runnerState.ActiveDownloadClients.TryGetValue(download.DownloadId, out var downloadClient))
            {
                Log($"Cancelling download", download, torrent);

                await downloadClient.Cancel();

                await Task.Delay(500);

                retry++;

                if (retry > 5)
                {
                    break;
                }
            }

            retry = 10;

            while (runnerState.ActiveUnpackClients.TryGetValue(download.DownloadId, out var unpackClient))
            {
                Log($"Cancelling unpack", download, torrent);

                unpackClient.Cancel();

                await Task.Delay(500);

                retry++;

                if (retry > 10)
                {
                    break;
                }
            }
        }

        if (deleteData)
        {
            Log($"Deleting RdtClient data", torrent);

            await torrentData.Delete(torrentId);
        }

        if (deleteRdTorrent && torrent.RdId != null)
        {
            Log($"Deleting RealDebrid Torrent", torrent);

            try
            {
                await DebridClient.Delete(torrent);
            }
            catch
            {
                // ignored
            }
        }

        if (deleteLocalFiles && !String.IsNullOrWhiteSpace(torrent.RdName))
        {
            var downloadPath = DownloadPath(torrent, settings.Current);
            downloadPath = Path.Combine(downloadPath, torrent.RdName);

            Log($"Deleting local files in {downloadPath}", torrent);

            if (Directory.Exists(downloadPath))
            {
                var retry = 0;

                while (true)
                {
                    try
                    {
                        Directory.Delete(downloadPath, true);

                        break;
                    }
                    catch
                    {
                        retry++;

                        if (retry >= 3)
                        {
                            throw;
                        }

                        await Task.Delay(1000);
                    }
                }
            }
        }
    }

    public async Task<String> UnrestrictLink(Guid downloadId)
    {
        var download = await downloads.GetById(downloadId) ?? throw new($"Download with ID {downloadId} not found");

        Log("Unrestricting link", download, download.Torrent);

        var unrestrictedLink = await DebridClient.Unrestrict(download.Torrent!, download.Path);

        await downloads.UpdateUnrestrictedLink(downloadId, unrestrictedLink);

        return unrestrictedLink;
    }

    public async Task<String> RetrieveFileName(Guid downloadId)
    {
        var download = await downloads.GetById(downloadId) ?? throw new($"Download with ID {downloadId} not found");

        Log($"Retrieving filename for", download, download.Torrent!);

        var fileName = await DebridClient.GetFileName(download);

        await downloads.UpdateFileName(downloadId, fileName);

        return fileName;
    }

    public async Task<Profile> GetProfile()
    {
        var user = await DebridClient.GetUser();

        var profile = new Profile
        {
            Provider = Enum.GetName(settings.Current.Provider.Provider),
            UserName = user.Username,
            Expiration = user.Expiration,
            CurrentVersion = UpdateChecker.CurrentVersion,
            LatestVersion = UpdateChecker.LatestVersion,
            IsInsecure = UpdateChecker.IsInsecure,
            DisableUpdateNotification = settings.Current.General.DisableUpdateNotifications
        };

        return profile;
    }

    public async Task UpdateRdData()
    {
        await RealDebridUpdateLock.WaitAsync();

        var torrents = await Get();
        var providerManagedTorrents = torrents.Where(t => !IsQbittorrentFallback(t)).ToList();

        try
        {
            var rdTorrents = await DebridClient.GetDownloads();
            var torrentsByRdId = CreateTorrentLookupByRdId(providerManagedTorrents);
            var providerTorrentsById = CreateProviderTorrentLookupById(rdTorrents);

            foreach (var rdTorrent in rdTorrents)
            {
                torrentsByRdId.TryGetValue(rdTorrent.Id, out var torrent);

                // TorBox migration from storing torrent hash in RdId to torrent ids.
                if (torrent == null
                    && Settings.Get.Provider.Provider == Provider.TorBox
                    && rdTorrent.Type == DownloadType.Torrent
                    && !String.IsNullOrWhiteSpace(rdTorrent.Hash)
                    && !String.IsNullOrWhiteSpace(rdTorrent.Id))
                {
                    torrent = providerManagedTorrents.FirstOrDefault(localTorrent => localTorrent is { Type: DownloadType.Torrent, ClientKind: null or Provider.TorBox }
                                                                                     && !String.IsNullOrWhiteSpace(localTorrent.Hash)
                                                                                     && !String.IsNullOrWhiteSpace(localTorrent.RdId)
                                                                                     && localTorrent.RdId.Equals(localTorrent.Hash, StringComparison.OrdinalIgnoreCase)
                                                                                     && localTorrent.Hash.Equals(rdTorrent.Hash, StringComparison.OrdinalIgnoreCase));

                    if (torrent != null)
                    {
                        if (!String.IsNullOrWhiteSpace(torrent.RdId))
                        {
                            torrentsByRdId.Remove(torrent.RdId);
                        }

                        await torrentData.UpdateRdId(torrent, rdTorrent.Id);
                        torrent.RdId = rdTorrent.Id;
                        torrent.ClientKind = Provider.TorBox;
                        torrentsByRdId[rdTorrent.Id] = torrent;

                        logger.LogInformation("Migrated TorBox torrent RdId from hash to torrent id for {TorrentName} ({Hash}) -> {RdId}",
                                              torrent.RdName ?? rdTorrent.Filename,
                                              rdTorrent.Hash,
                                              rdTorrent.Id);
                    }
                }

                // Auto import torrents only torrents that have their files selected
                if (torrent == null && settings.Current.Provider.AutoImport)
                {
                    var newTorrent = new Torrent
                    {
                        Category = settings.Current.Provider.Default.Category,
                        DownloadClient = settings.Current.DownloadClient.Client,
                        DownloadAction =
                            settings.Current.Provider.Default.OnlyDownloadAvailableFiles ? TorrentDownloadAction.DownloadAvailableFiles : TorrentDownloadAction.DownloadAll,
                        HostDownloadAction = settings.Current.Provider.Default.HostDownloadAction,
                        FinishedActionDelay = settings.Current.Provider.Default.FinishedActionDelay,
                        FinishedAction = settings.Current.Provider.Default.FinishedAction,
                        DownloadMinSize = settings.Current.Provider.Default.MinFileSize,
                        IncludeRegex = settings.Current.Provider.Default.IncludeRegex,
                        ExcludeRegex = settings.Current.Provider.Default.ExcludeRegex,
                        TorrentRetryAttempts = settings.Current.Provider.Default.TorrentRetryAttempts,
                        DownloadRetryAttempts = settings.Current.Provider.Default.DownloadRetryAttempts,
                        DeleteOnError = settings.Current.Provider.Default.DeleteOnError,
                        Lifetime = settings.Current.Provider.Default.TorrentLifetime,
                        Priority = settings.Current.Provider.Default.Priority > 0 ? settings.Current.Provider.Default.Priority : null,
                        RdId = rdTorrent.Id
                    };

                    if (newTorrent.RdStatus == TorrentStatus.WaitingForFileSelection)
                    {
                        continue;
                    }

                    torrent = await torrentData.Add(rdTorrent.Id, rdTorrent.Hash, null, false, DownloadType.Torrent, settings.Current.DownloadClient.Client, newTorrent);
                    torrentsByRdId[rdTorrent.Id] = torrent;
                    torrents.Add(torrent);
                    providerManagedTorrents.Add(torrent);

                    await UpdateTorrentClientData(torrent, rdTorrent);
                }
                else if (torrent != null)
                {
                    await UpdateTorrentClientData(torrent, rdTorrent);
                }
            }

            foreach (var torrent in providerManagedTorrents)
            {
                var rdTorrent = torrent.RdId != null && providerTorrentsById.TryGetValue(torrent.RdId, out var providerTorrent) ? providerTorrent : null;

                if (rdTorrent == null && settings.Current.Provider.AutoDelete && torrent.RdStatus != TorrentStatus.Queued)
                {
                    await Delete(torrent.TorrentId, true, false, true);
                }
            }
        }
        finally
        {
            RealDebridUpdateLock.Release();
        }
    }

    public async Task RetryTorrent(Guid torrentId, Int32 retryCount)
    {
        await TorrentResetLock.WaitAsync();

        try
        {
            var torrent = await torrentData.GetById(torrentId);

            if (torrent?.Retry == null)
            {
                return;
            }

            Log($"Retrying Torrent", torrent);

            await UpdateComplete(torrent.TorrentId, "Retrying Torrent", DateTimeOffset.UtcNow, false);
            await UpdateRetry(torrent.TorrentId, null, 0);

            foreach (var download in torrent.Downloads)
            {
                await downloads.UpdateError(download.DownloadId, null);
                await downloads.UpdateCompleted(download.DownloadId, DateTimeOffset.UtcNow);
            }

            foreach (var download in torrent.Downloads)
            {
                while (runnerState.ActiveDownloadClients.TryRemove(download.DownloadId, out var downloadClient))
                {
                    await downloadClient.Cancel();

                    await Task.Delay(100);
                }

                while (runnerState.ActiveUnpackClients.TryRemove(download.DownloadId, out var unpackClient))
                {
                    unpackClient.Cancel();

                    await Task.Delay(100);
                }
            }

            await Delete(torrentId, true, true, true);

            if (String.IsNullOrWhiteSpace(torrent.FileOrMagnet))
            {
                throw new($"Cannot re-add this torrent, original magnet or file not found");
            }

            Torrent newTorrent;

            if (torrent.Type == DownloadType.Nzb)
            {
                if (torrent.IsFile)
                {
                    var bytes = Convert.FromBase64String(torrent.FileOrMagnet!);

                    newTorrent = await AddNzbFileToDebridQueue(bytes, torrent.RdName, torrent);
                }
                else
                {
                    newTorrent = await AddNzbLinkToDebridQueue(torrent.FileOrMagnet!, torrent);
                }
            }
            else
            {
                if (torrent.IsFile)
                {
                    var bytes = Convert.FromBase64String(torrent.FileOrMagnet!);

                    newTorrent = await AddFileToDebridQueue(bytes, torrent);
                }
                else
                {
                    newTorrent = await AddMagnetToDebridQueue(torrent.FileOrMagnet!, torrent);
                }
            }

            await torrentData.UpdateRetry(newTorrent.TorrentId, null, retryCount);
        }
        finally
        {
            TorrentResetLock.Release();
        }
    }

    public async Task RetryDownload(Guid downloadId)
    {
        var download = await downloads.GetById(downloadId);

        if (download == null)
        {
            return;
        }

        Log($"Retrying Download", download, download.Torrent);

        while (runnerState.ActiveDownloadClients.TryRemove(download.DownloadId, out var downloadClient))
        {
            await downloadClient.Cancel();

            await Task.Delay(100);
        }

        while (runnerState.ActiveUnpackClients.TryRemove(download.DownloadId, out var unpackClient))
        {
            unpackClient.Cancel();

            await Task.Delay(100);
        }

        var downloadPath = DownloadPath(download.Torrent!, settings.Current);

        var filePath = DownloadHelper.GetDownloadPath(downloadPath, download.Torrent!, download);

        if (filePath != null)
        {
            Log($"Deleting {filePath}", download, download.Torrent);

            await FileHelper.Delete(filePath);
        }

        Log($"Resetting", download, download.Torrent);

        await downloads.Reset(downloadId);

        await torrentData.UpdateComplete(download.TorrentId, null, null, false);
    }

    public async Task UpdateComplete(Guid torrentId, String? error, DateTimeOffset datetime, Boolean retry)
    {
        await torrentData.UpdateComplete(torrentId, error, datetime, retry);
    }

    public async Task UpdateFilesSelected(Guid torrentId, DateTimeOffset datetime)
    {
        await torrentData.UpdateFilesSelected(torrentId, datetime);
    }

    public async Task<Boolean> UpdateFileSelection(String hash, IReadOnlyCollection<Int32> fileIds, Boolean selected)
    {
        if (fileIds.Count == 0)
        {
            return false;
        }

        var torrent = await torrentData.GetByHash(hash);

        if (torrent == null)
        {
            return false;
        }

        var files = torrent.Files.ToList();

        if (files.Count == 0)
        {
            return false;
        }

        foreach (var fileId in fileIds)
        {
            if (fileId < 0 || fileId >= files.Count)
            {
                continue;
            }

            files[fileId].Selected = selected;
        }

        torrent.RdFiles = JsonSerializer.Serialize(files, JsonSerializerOptions);
        await torrentData.UpdateRdData(torrent);

        return true;
    }

    public async Task UpdatePriority(String hash, Int32 priority)
    {
        var torrent = await torrentData.GetByHash(hash);

        if (torrent == null)
        {
            return;
        }

        await torrentData.UpdatePriority(torrent.TorrentId, priority);
    }

    public async Task UpdateRetry(Guid torrentId, DateTimeOffset? datetime, Int32 retry)
    {
        await torrentData.UpdateRetry(torrentId, datetime, retry);
    }

    public async Task UpdateError(Guid torrentId, String error)
    {
        await torrentData.UpdateError(torrentId, error);
    }

    public async Task<Torrent?> GetById(Guid torrentId)
    {
        var torrent = await torrentData.GetById(torrentId);

        if (torrent == null)
        {
            return null;
        }

        if (!IsQbittorrentFallback(torrent))
        {
            await UpdateTorrentClientData(torrent);
        }

        return torrent;
    }

    private static String DownloadPath(Torrent torrent, DbSettings settings)
    {
        var settingDownloadPath = settings.DownloadClient.DownloadPath;

        if (!String.IsNullOrWhiteSpace(torrent.Category))
        {
            settingDownloadPath = Path.Combine(settingDownloadPath, torrent.Category);
        }

        return settingDownloadPath;
    }

    private async Task<Torrent> AddQueued(String infoHash,
                                          String fileOrMagnetContents,
                                          Boolean isFile,
                                          DownloadType downloadType,
                                          Torrent torrent)
    {
        var existingTorrent = await torrentData.GetByHash(infoHash);

        if (existingTorrent != null)
        {
            return existingTorrent;
        }

        var newTorrent = await torrentData.Add(null,
                                               infoHash,
                                               fileOrMagnetContents,
                                               isFile,
                                               downloadType,
                                               torrent.DownloadClient,
                                               torrent);

        return newTorrent;
    }

    public async Task Update(Torrent torrent)
    {
        await torrentData.Update(torrent);
    }

    public async Task RunTorrentComplete(Guid torrentId, DbSettings? runSettings = null)
    {
        runSettings ??= settings.Current;

        if (String.IsNullOrWhiteSpace(runSettings.General.RunOnTorrentCompleteFileName))
        {
            return;
        }

        var torrent = await torrentData.GetById(torrentId) ?? throw new($"Cannot find Torrent with ID {torrentId}");

        var downloadsForTorrent = await downloads.GetForTorrent(torrentId);

        var fileName = runSettings.General.RunOnTorrentCompleteFileName;
        var arguments = runSettings.General.RunOnTorrentCompleteArguments ?? "";

        Log($"Parsing external program {fileName} with arguments {arguments}", torrent);

        var downloadPath = DownloadPath(torrent, runSettings);
        var torrentPath = Path.Combine(downloadPath, torrent.RdName ?? "Unknown");

        var filePath = torrentPath;

        var files = fileSystem.Directory.GetFiles(filePath);

        if (files.Length == 1)
        {
            filePath = Path.Combine(torrentPath, files[0]);
        }

        arguments = arguments.Replace("%N", $"\"{torrent.RdName}\"");
        arguments = arguments.Replace("%L", $"\"{torrent.Category}\"");
        arguments = arguments.Replace("%F", $"\"{filePath}\"");
        arguments = arguments.Replace("%R", $"\"{downloadPath}\"");
        arguments = arguments.Replace("%D", $"\"{torrentPath}\"");
        arguments = arguments.Replace("%C", downloadsForTorrent.Count.ToString(CultureInfo.InvariantCulture).Replace(",", "").Replace(".", ""));
        arguments = arguments.Replace("%Z", torrent.RdSize?.ToString(CultureInfo.InvariantCulture).Replace(",", "").Replace(".", ""));
        arguments = arguments.Replace("%I", torrent.Hash);

        Log($"Executing external program {fileName} with arguments {arguments}", torrent);

        var errorSb = new StringBuilder();
        var outputSb = new StringBuilder();

        using var process = processFactory.NewProcess();

        process.StartInfo.FileName = fileName;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        process.OutputDataReceived += (_, data) =>
        {
            if (data == null)
            {
                return;
            }

            outputSb.AppendLine(data.Trim());
        };

        process.ErrorDataReceived += (_, data) =>
        {
            if (data == null)
            {
                return;
            }

            errorSb.AppendLine(data.Trim());
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exited = process.WaitForExit(60000 * 10);

        var errors = errorSb.ToString();
        var output = outputSb.ToString();

        if (errors.Length > 0)
        {
            Log($"External application exited with errors: {errors}", torrent);
        }

        if (output.Length > 0)
        {
            Log($"External application exited with output: {output}", torrent);
        }

        if (!exited)
        {
            Log("External application after a 60 second timeout", torrent);
        }
    }

    private async Task UpdateTorrentClientData(Torrent torrent, DebridClientTorrent? torrentClientTorrent = null)
    {
        if (IsQbittorrentFallback(torrent))
        {
            return;
        }

        try
        {
            var originalTorrent = CaptureRdState(torrent);

            await DebridClient.UpdateData(torrent, torrentClientTorrent);

            var newTorrent = CaptureRdState(torrent);

            if (originalTorrent != newTorrent)
            {
                await torrentData.UpdateRdData(torrent);
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private static Dictionary<String, Torrent> CreateTorrentLookupByRdId(IEnumerable<Torrent> torrents)
    {
        var lookup = new Dictionary<String, Torrent>(StringComparer.Ordinal);

        foreach (var torrent in torrents)
        {
            if (!String.IsNullOrWhiteSpace(torrent.RdId))
            {
                lookup[torrent.RdId] = torrent;
            }
        }

        return lookup;
    }

    private static Dictionary<String, DebridClientTorrent> CreateProviderTorrentLookupById(IEnumerable<DebridClientTorrent> torrents)
    {
        var lookup = new Dictionary<String, DebridClientTorrent>(StringComparer.Ordinal);

        foreach (var torrent in torrents)
        {
            if (!String.IsNullOrWhiteSpace(torrent.Id))
            {
                lookup[torrent.Id] = torrent;
            }
        }

        return lookup;
    }

    private static TorrentRdState CaptureRdState(Torrent torrent)
    {
        return new(torrent.RdName,
                   torrent.RdSize,
                   torrent.RdHost,
                   torrent.RdSplit,
                   torrent.RdProgress,
                   torrent.RdStatus,
                   torrent.RdStatusRaw,
                   torrent.RdAdded,
                   torrent.RdEnded,
                   torrent.RdSpeed,
                   torrent.RdSeeders,
                   torrent.RdFiles);
    }

    private readonly record struct TorrentRdState(
        String? RdName,
        Int64? RdSize,
        String? RdHost,
        Int64? RdSplit,
        Int64? RdProgress,
        TorrentStatus? RdStatus,
        String? RdStatusRaw,
        DateTimeOffset? RdAdded,
        DateTimeOffset? RdEnded,
        Int64? RdSpeed,
        Int64? RdSeeders,
        String? RdFiles);

    private async Task SyncQbittorrentFallbackTorrents(IList<Torrent> torrents)
    {
        var fallbackTorrents = torrents.Where(IsQbittorrentFallback).ToList();

        if (fallbackTorrents.Count == 0)
        {
            return;
        }

        if (!qbittorrentFallbackClient.IsEnabledAndConfigured())
        {
            return;
        }

        IList<TorrentInfo> qbtTorrents;

        try
        {
            qbtTorrents = await qbittorrentFallbackClient.GetTorrents();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to sync qBittorrent fallback state");

            return;
        }

        var qbtTorrentsByHash = qbtTorrents.Where(m => !String.IsNullOrWhiteSpace(m.Hash))
                                           .ToDictionary(m => m.Hash, m => m, StringComparer.OrdinalIgnoreCase);

        var toDelete = new List<Torrent>();

        foreach (var fallbackTorrent in fallbackTorrents)
        {
            if (qbtTorrentsByHash.TryGetValue(fallbackTorrent.Hash, out var qbtTorrent))
            {
                var changed = ApplyQbittorrentFallbackState(fallbackTorrent, qbtTorrent);
                var shouldBeCompleted = fallbackTorrent.RdStatus == TorrentStatus.Finished && String.IsNullOrWhiteSpace(fallbackTorrent.Error);
                var needsCompleteUpdate = shouldBeCompleted != fallbackTorrent.Completed.HasValue;

                if (changed)
                {
                    await torrentData.UpdateRdData(fallbackTorrent);
                }

                if (needsCompleteUpdate)
                {
                    logger.LogInformation("Updating fallback completion state. completed={completed} {torrentInfo}", shouldBeCompleted, fallbackTorrent.ToLog());
                    await torrentData.UpdateComplete(fallbackTorrent.TorrentId, null, shouldBeCompleted ? DateTimeOffset.UtcNow : null, false);
                }

                continue;
            }

            // qBittorrent can take a moment to expose a just-added torrent through the API.
            if (fallbackTorrent.RdAdded.HasValue && DateTimeOffset.UtcNow - fallbackTorrent.RdAdded.Value < TimeSpan.FromMinutes(2))
            {
                continue;
            }

            logger.LogInformation("Removing fallback torrent from rdt-client because it no longer exists in qBittorrent {torrentInfo}",
                                  fallbackTorrent.ToLog());

            toDelete.Add(fallbackTorrent);
        }

        foreach (var staleTorrent in toDelete)
        {
            await downloads.DeleteForTorrent(staleTorrent.TorrentId);
            await torrentData.Delete(staleTorrent.TorrentId);
            torrents.Remove(staleTorrent);
        }
    }

    private static Boolean ApplyQbittorrentFallbackState(Torrent torrent, TorrentInfo qbtTorrent)
    {
        var changed = false;

        var expectedRdId = QbittorrentFallbackClient.ToFallbackId(torrent.Hash);

        if (!String.Equals(torrent.RdId, expectedRdId, StringComparison.OrdinalIgnoreCase))
        {
            torrent.RdId = expectedRdId;
            changed = true;
        }

        if (!String.Equals(torrent.RdHost, "qBittorrent", StringComparison.Ordinal))
        {
            torrent.RdHost = "qBittorrent";
            changed = true;
        }

        var name = String.IsNullOrWhiteSpace(qbtTorrent.Name) ? torrent.RdName : qbtTorrent.Name;

        if (!String.Equals(torrent.RdName, name, StringComparison.Ordinal))
        {
            torrent.RdName = name;
            changed = true;
        }

        var size = qbtTorrent.TotalSize ?? qbtTorrent.Size ?? torrent.RdSize;

        if (torrent.RdSize != size)
        {
            torrent.RdSize = size;
            changed = true;
        }

        var safeProgress = Single.IsNaN(qbtTorrent.Progress) || Single.IsInfinity(qbtTorrent.Progress)
            ? 0
            : qbtTorrent.Progress;
        var progress = (Int64)Math.Clamp(Math.Round(safeProgress * 100), 0, 100);

        if (torrent.RdProgress != progress)
        {
            torrent.RdProgress = progress;
            changed = true;
        }

        var speed = qbtTorrent.Dlspeed ?? 0;

        if (torrent.RdSpeed != speed)
        {
            torrent.RdSpeed = speed;
            changed = true;
        }

        var seeders = qbtTorrent.NumSeeds ?? qbtTorrent.NumComplete ?? 0;

        if (torrent.RdSeeders != seeders)
        {
            torrent.RdSeeders = seeders;
            changed = true;
        }

        var statusRaw = qbtTorrent.State ?? QbittorrentFallbackClient.FallbackStatusRaw;

        if (!String.Equals(torrent.RdStatusRaw, statusRaw, StringComparison.Ordinal))
        {
            torrent.RdStatusRaw = statusRaw;
            changed = true;
        }

        var status = MapQbittorrentState(statusRaw, progress);

        if (torrent.RdStatus != status)
        {
            torrent.RdStatus = status;
            changed = true;
        }

        var added = FromUnixTime(qbtTorrent.AddedOn) ?? torrent.RdAdded ?? DateTimeOffset.UtcNow;

        if (torrent.RdAdded != added)
        {
            torrent.RdAdded = added;
            changed = true;
        }

        DateTimeOffset? ended = progress >= 100 ? (FromUnixTime(qbtTorrent.CompletionOn) ?? DateTimeOffset.UtcNow) : null;

        if (torrent.RdEnded != ended)
        {
            torrent.RdEnded = ended;
            changed = true;
        }

        return changed;
    }

    private static TorrentStatus MapQbittorrentState(String statusRaw, Int64 progress)
    {
        var state = statusRaw.Trim().ToLowerInvariant();

        if (state.Contains("error") || state.Contains("missing"))
        {
            return TorrentStatus.Error;
        }

        if (progress >= 100 || state.Contains("up"))
        {
            return TorrentStatus.Finished;
        }

        if (state.Contains("queued"))
        {
            return TorrentStatus.Queued;
        }

        if (state.Contains("check") || state.Contains("allocat") || state.Contains("meta") || state.Contains("mov"))
        {
            return TorrentStatus.Processing;
        }

        return TorrentStatus.Downloading;
    }

    private static DateTimeOffset? FromUnixTime(Int64? unixSeconds)
    {
        if (!unixSeconds.HasValue || unixSeconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value);
        }
        catch
        {
            return null;
        }
    }

    private async Task<Boolean> SendToQbittorrentFallback(Torrent torrent, String statusRaw)
    {
        var fallbackSavePath = GetQbittorrentFallbackSavePath(torrent);

        if (torrent.IsFile)
        {
            var fileBytes = Convert.FromBase64String(torrent.FileOrMagnet!);
            await qbittorrentFallbackClient.AddFile(fileBytes, torrent.Category, torrent.Priority, fallbackSavePath);
        }
        else
        {
            await qbittorrentFallbackClient.AddMagnet(torrent.FileOrMagnet!, torrent.Category, torrent.Priority, fallbackSavePath);
        }

        torrent.RdId = QbittorrentFallbackClient.ToFallbackId(torrent.Hash);
        torrent.RdStatus = TorrentStatus.Downloading;
        torrent.RdStatusRaw = statusRaw;
        torrent.RdHost = "qBittorrent";
        torrent.RdAdded = DateTimeOffset.UtcNow;
        torrent.RdEnded = null;
        torrent.RdProgress = 0;
        torrent.RdSpeed = 0;
        torrent.RdSeeders = 0;

        await torrentData.UpdateRdId(torrent, torrent.RdId);
        await torrentData.UpdateRdData(torrent);
        await torrentData.UpdateComplete(torrent.TorrentId, null, null, false);
        await torrentData.UpdateRetry(torrent.TorrentId, null, 0);

        logger.LogInformation("Sent torrent to external qBittorrent fallback successfully. SavePath: {savePath} {torrentInfo}",
                              fallbackSavePath ?? "<qB default>",
                              torrent.ToLog());

        return true;
    }

    private static Boolean? ParseInstantAvailability(JsonElement root, String hash)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonElement hashNode;

        if (!root.TryGetProperty(hash.ToLowerInvariant(), out hashNode) &&
            !root.TryGetProperty(hash.ToUpperInvariant(), out hashNode) &&
            !root.TryGetProperty(hash, out hashNode))
        {
            return null;
        }

        return HasAvailabilityData(hashNode);
    }

    private static Boolean HasAvailabilityData(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (HasAvailabilityData(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasAvailabilityData(item))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.String:
                return !String.IsNullOrWhiteSpace(element.GetString());
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return true;
            default:
                return false;
        }
    }

    private Boolean ShouldFallbackToQbittorrent(Exception exception)
    {
        if (settings.Current.Provider.Provider != Provider.RealDebrid)
        {
            return false;
        }

        for (var currentException = exception; currentException != null; currentException = currentException.InnerException)
        {
            if (currentException is RDNET.RealDebridException realDebridException && realDebridException.ErrorCode == 35)
            {
                return true;
            }

            if (currentException.Message.Contains("Infringing file", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private String? GetQbittorrentFallbackSavePath(Torrent torrent)
    {
        var basePath = settings.Current.DownloadClient.DownloadPath;

        if (String.IsNullOrWhiteSpace(basePath))
        {
            return null;
        }

        if (!String.IsNullOrWhiteSpace(torrent.Category))
        {
            return Path.Combine(basePath, torrent.Category);
        }

        return basePath;
    }

    private void Log(String message, Download? download, Torrent? torrent)
    {
        if (download != null)
        {
            message = $"{message} {download.ToLog()}";
        }

        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogDebug(message);
    }

    private void Log(String message, Torrent? torrent = null)
    {
        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogDebug(message);
    }

    private static String ComputeSha1Hash(String input)
    {
        using var sha1 = SHA1.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = sha1.ComputeHash(bytes);

        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    private static String ComputeMd5Hash(String input)
    {
        using var md5 = MD5.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = md5.ComputeHash(bytes);

        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    private static String ComputeMd5HashFromBytes(Byte[] bytes)
    {
        using var md5 = MD5.Create();
        var hashBytes = md5.ComputeHash(bytes);

        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}
