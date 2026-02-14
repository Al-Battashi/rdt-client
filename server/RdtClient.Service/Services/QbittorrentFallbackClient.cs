using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.QBittorrent;

namespace RdtClient.Service.Services;

public class QbittorrentFallbackClient(ILogger<QbittorrentFallbackClient> logger)
{
    public const String FallbackStatusRaw = "qbittorrent_fallback";
    private const String FallbackIdPrefix = "qbt:";

    public Boolean IsEnabledAndConfigured()
    {
        var result = TryGetConfiguration(out _, out var validationError);

        if (!result && validationError != null)
        {
            logger.LogDebug("qBittorrent fallback not ready: {validationError}", validationError);
        }

        return result;
    }

    public static String ToFallbackId(String hash)
    {
        return $"{FallbackIdPrefix}{hash.Trim().ToLowerInvariant()}";
    }

    public static Boolean IsFallbackId(String? id)
    {
        return !String.IsNullOrWhiteSpace(id) && id.StartsWith(FallbackIdPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static Boolean IsFallbackTorrent(Torrent torrent)
    {
        return IsFallbackId(torrent.RdId) || String.Equals(torrent.RdStatusRaw, FallbackStatusRaw, StringComparison.OrdinalIgnoreCase);
    }

    public async Task AddMagnet(String magnetLink, String? category, Int32? priority, CancellationToken cancellationToken = default)
    {
        using var client = await CreateAuthenticatedClient(cancellationToken);

        var formData = new List<KeyValuePair<String, String>>
        {
            new("urls", magnetLink)
        };

        if (!String.IsNullOrWhiteSpace(category))
        {
            formData.Add(new("category", category));
        }

        if (priority.HasValue)
        {
            formData.Add(new("priority", priority.Value.ToString(CultureInfo.InvariantCulture)));
        }

        await SendForm(client, "api/v2/torrents/add", formData, cancellationToken);
    }

    public async Task AddFile(Byte[] fileBytes, String? category, Int32? priority, CancellationToken cancellationToken = default)
    {
        using var client = await CreateAuthenticatedClient(cancellationToken);
        using var formData = new MultipartFormDataContent();
        using var torrentFile = new ByteArrayContent(fileBytes);

        formData.Add(torrentFile, "torrents", "fallback.torrent");

        if (!String.IsNullOrWhiteSpace(category))
        {
            formData.Add(new StringContent(category), "category");
        }

        if (priority.HasValue)
        {
            formData.Add(new StringContent(priority.Value.ToString(CultureInfo.InvariantCulture)), "priority");
        }

        var response = await client.PostAsync("api/v2/torrents/add", formData, cancellationToken);
        await EnsureSuccess(response, "api/v2/torrents/add", cancellationToken);
    }

    public async Task<IList<TorrentInfo>> GetTorrents(CancellationToken cancellationToken = default)
    {
        using var client = await CreateAuthenticatedClient(cancellationToken);
        var root = await SendGetJson(client, "api/v2/torrents/info", cancellationToken);

        return ParseTorrentInfoList(root);
    }

    public async Task<TorrentInfo?> GetTorrent(String hash, CancellationToken cancellationToken = default)
    {
        using var client = await CreateAuthenticatedClient(cancellationToken);
        var escapedHash = Uri.EscapeDataString(hash.ToLowerInvariant());
        var root = await SendGetJson(client, $"api/v2/torrents/info?hashes={escapedHash}", cancellationToken);
        var torrents = ParseTorrentInfoList(root);

        return torrents.FirstOrDefault(m => m.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IList<TorrentFileItem>?> GetFiles(String hash, CancellationToken cancellationToken = default)
    {
        using var client = await CreateAuthenticatedClient(cancellationToken);
        var escapedHash = Uri.EscapeDataString(hash.ToLowerInvariant());
        var root = await SendGetJson(client, $"api/v2/torrents/files?hash={escapedHash}", cancellationToken);

        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<TorrentFileItem>();

        foreach (var file in root.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            results.Add(new()
            {
                Name = ReadString(file, "name")
            });
        }

        return results;
    }

    public async Task<TorrentProperties?> GetProperties(String hash, CancellationToken cancellationToken = default)
    {
        using var client = await CreateAuthenticatedClient(cancellationToken);
        var escapedHash = Uri.EscapeDataString(hash.ToLowerInvariant());
        var root = await SendGetJson(client, $"api/v2/torrents/properties?hash={escapedHash}", cancellationToken);

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new TorrentProperties
        {
            AdditionDate = ReadInt64(root, "addition_date"),
            Comment = ReadString(root, "comment"),
            CompletionDate = ReadInt64(root, "completion_date"),
            CreatedBy = ReadString(root, "created_by"),
            CreationDate = ReadInt64(root, "creation_date"),
            DlLimit = ReadInt64(root, "dl_limit"),
            DlSpeed = ReadInt64(root, "dl_speed"),
            DlSpeedAvg = ReadInt64(root, "dl_speed_avg"),
            Eta = ReadInt64(root, "eta"),
            LastSeen = ReadInt64(root, "last_seen"),
            NbConnections = ReadInt64(root, "nb_connections"),
            NbConnectionsLimit = ReadInt64(root, "nb_connections_limit"),
            Peers = ReadInt64(root, "peers"),
            PeersTotal = ReadInt64(root, "peers_total"),
            PieceSize = ReadInt64(root, "piece_size"),
            PiecesHave = ReadInt64(root, "pieces_have"),
            PiecesNum = ReadInt64(root, "pieces_num"),
            Reannounce = ReadInt64(root, "reannounce"),
            SavePath = ReadString(root, "save_path"),
            SeedingTime = ReadInt64(root, "seeding_time"),
            Seeds = ReadInt64(root, "seeds"),
            SeedsTotal = ReadInt64(root, "seeds_total"),
            ShareRatio = ReadInt64(root, "share_ratio"),
            TimeElapsed = ReadInt64(root, "time_elapsed"),
            TotalDownloaded = ReadInt64(root, "total_downloaded"),
            TotalDownloadedSession = ReadInt64(root, "total_downloaded_session"),
            TotalSize = ReadInt64(root, "total_size"),
            TotalUploaded = ReadInt64(root, "total_uploaded"),
            TotalUploadedSession = ReadInt64(root, "total_uploaded_session"),
            TotalWasted = ReadInt64(root, "total_wasted"),
            UpLimit = ReadInt64(root, "up_limit"),
            UpSpeed = ReadInt64(root, "up_speed"),
            UpSpeedAvg = ReadInt64(root, "up_speed_avg")
        };

        return result;
    }

    public async Task Pause(String hash, CancellationToken cancellationToken = default)
    {
        using var client = await CreateAuthenticatedClient(cancellationToken);

        await SendForm(client,
                       "api/v2/torrents/pause",
                       [
                           new("hashes", hash)
                       ],
                       cancellationToken);
    }

    public async Task Resume(String hash, CancellationToken cancellationToken = default)
    {
        using var client = await CreateAuthenticatedClient(cancellationToken);

        await SendForm(client,
                       "api/v2/torrents/resume",
                       [
                           new("hashes", hash)
                       ],
                       cancellationToken);
    }

    public async Task Delete(String hash, Boolean deleteFiles, CancellationToken cancellationToken = default)
    {
        using var client = await CreateAuthenticatedClient(cancellationToken);

        await SendForm(client,
                       "api/v2/torrents/delete",
                       [
                           new("hashes", hash),
                           new("deleteFiles", deleteFiles.ToString().ToLowerInvariant())
                       ],
                       cancellationToken);
    }

    public async Task SetCategory(String hash, String? category, CancellationToken cancellationToken = default)
    {
        using var client = await CreateAuthenticatedClient(cancellationToken);

        await SendForm(client,
                       "api/v2/torrents/setCategory",
                       [
                           new("hashes", hash),
                           new("category", category ?? "")
                       ],
                       cancellationToken);
    }

    public async Task TopPriority(String hash, CancellationToken cancellationToken = default)
    {
        using var client = await CreateAuthenticatedClient(cancellationToken);

        await SendForm(client,
                       "api/v2/torrents/topPrio",
                       [
                           new("hashes", hash)
                       ],
                       cancellationToken);
    }

    private async Task<HttpClient> CreateAuthenticatedClient(CancellationToken cancellationToken)
    {
        if (!TryGetConfiguration(out var settings, out var validationError))
        {
            throw new(validationError ?? "qBittorrent fallback is not configured.");
        }

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        if (settings.IgnoreTlsErrors)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var timeout = settings.Timeout <= 0 ? 15 : settings.Timeout;

        var client = new HttpClient(handler)
        {
            BaseAddress = settings.BaseUri,
            Timeout = TimeSpan.FromSeconds(timeout)
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "rdt-client qbt-fallback");

        await Login(client, settings, cancellationToken);

        return client;
    }

    private async Task Login(HttpClient client, QbittorrentFallbackConfiguration settings, CancellationToken cancellationToken)
    {
        using var formData = new FormUrlEncodedContent(
        [
            new("username", settings.UserName),
            new("password", settings.Password)
        ]);

        var response = await client.PostAsync("api/v2/auth/login", formData, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode || !responseText.Contains("Ok.", StringComparison.OrdinalIgnoreCase))
        {
            throw new($"Unable to authenticate qBittorrent fallback user. Status: {(Int32)response.StatusCode}. Message: {responseText}");
        }
    }

    private async Task SendForm(HttpClient client, String path, IEnumerable<KeyValuePair<String, String>> values, CancellationToken cancellationToken)
    {
        using var formData = new FormUrlEncodedContent(values);

        var response = await client.PostAsync(path, formData, cancellationToken);
        await EnsureSuccess(response, path, cancellationToken);
    }

    private async Task<JsonElement> SendGetJson(HttpClient client, String path, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(path, cancellationToken);

        await EnsureSuccess(response, path, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        using var jsonDocument = JsonDocument.Parse(responseBody);

        return jsonDocument.RootElement.Clone();
    }

    private async Task EnsureSuccess(HttpResponseMessage response, String path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        throw new($"qBittorrent fallback request '{path}' failed with status {(Int32)response.StatusCode}: {responseBody}");
    }

    private List<TorrentInfo> ParseTorrentInfoList(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<TorrentInfo>();

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var torrentInfo = new TorrentInfo
            {
                AddedOn = ReadInt64(item, "added_on"),
                AmountLeft = ReadInt64(item, "amount_left"),
                AutoTmm = ReadBoolean(item, "auto_tmm"),
                Availability = ReadDecimal(item, "availability") ?? 0,
                Category = ReadString(item, "category"),
                Completed = ReadInt64(item, "completed"),
                CompletionOn = ReadInt64(item, "completion_on"),
                ContentPath = ReadString(item, "content_path"),
                DlLimit = ReadInt64(item, "dl_limit"),
                Dlspeed = ReadInt64(item, "dlspeed"),
                Downloaded = ReadInt64(item, "downloaded"),
                DownloadedSession = ReadInt64(item, "downloaded_session"),
                Eta = ReadInt64(item, "eta"),
                FlPiecePrio = ReadBoolean(item, "f_l_piece_prio"),
                ForceStart = ReadBoolean(item, "force_start"),
                Hash = ReadString(item, "hash") ?? "",
                LastActivity = ReadInt64(item, "last_activity"),
                MagnetUri = ReadString(item, "magnet_uri"),
                MaxRatio = ReadInt64(item, "max_ratio"),
                MaxSeedingTime = ReadInt64(item, "max_seeding_time"),
                Name = ReadString(item, "name"),
                NumComplete = ReadInt64(item, "num_complete"),
                NumIncomplete = ReadInt64(item, "num_incomplete"),
                NumLeechs = ReadInt64(item, "num_leechs"),
                NumSeeds = ReadInt64(item, "num_seeds"),
                Priority = ReadInt64(item, "priority"),
                Progress = ReadSingle(item, "progress") ?? 0,
                Ratio = ReadInt64(item, "ratio"),
                RatioLimit = ReadInt64(item, "ratio_limit"),
                SavePath = ReadString(item, "save_path"),
                SeedingTimeLimit = ReadInt64(item, "seeding_time_limit"),
                SeenComplete = ReadInt64(item, "seen_complete"),
                SeqDl = ReadBoolean(item, "seq_dl"),
                Size = ReadInt64(item, "size"),
                State = ReadString(item, "state"),
                SuperSeeding = ReadBoolean(item, "super_seeding"),
                Tags = ReadString(item, "tags"),
                TimeActive = ReadInt64(item, "time_active"),
                TotalSize = ReadInt64(item, "total_size"),
                Tracker = ReadString(item, "tracker"),
                UpLimit = ReadInt64(item, "up_limit"),
                Uploaded = ReadInt64(item, "uploaded"),
                UploadedSession = ReadInt64(item, "uploaded_session"),
                Upspeed = ReadInt64(item, "upspeed")
            };

            if (String.IsNullOrWhiteSpace(torrentInfo.Hash))
            {
                continue;
            }

            results.Add(torrentInfo);
        }

        return results;
    }

    private static String? ReadString(JsonElement element, String propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static Boolean ReadBoolean(JsonElement element, String propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when property.TryGetInt64(out var numberValue) => numberValue != 0,
            JsonValueKind.String when Boolean.TryParse(property.GetString(), out var boolValue) => boolValue,
            JsonValueKind.String when Int32.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue) => intValue != 0,
            _ => false
        };
    }

    private static Int64? ReadInt64(JsonElement element, String propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.Number when property.TryGetDouble(out var value) => (Int64)Math.Round(value),
            JsonValueKind.String when Int64.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) => value,
            JsonValueKind.String when Double.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) => (Int64)Math.Round(value),
            _ => null
        };
    }

    private static Single? ReadSingle(JsonElement element, String propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetSingle(out var value) => value,
            JsonValueKind.Number when property.TryGetDouble(out var value) => (Single)value,
            JsonValueKind.String when Single.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static Decimal? ReadDecimal(JsonElement element, String propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDecimal(out var value) => value,
            JsonValueKind.Number when property.TryGetDouble(out var value) => (Decimal)value,
            JsonValueKind.String when Decimal.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static Boolean TryGetProperty(JsonElement element, String propertyName, out JsonElement property)
    {
        if (element.TryGetProperty(propertyName, out property))
        {
            return true;
        }

        property = default;

        return false;
    }

    private Boolean TryGetConfiguration(out QbittorrentFallbackConfiguration settings, out String? validationError)
    {
        settings = default!;
        validationError = null;

        var fallback = Settings.Get.Integrations.QbittorrentFallback;

        if (!fallback.Enabled)
        {
            validationError = "qBittorrent fallback is disabled.";

            return false;
        }

        if (String.IsNullOrWhiteSpace(fallback.Url))
        {
            validationError = "qBittorrent fallback URL is not configured.";

            return false;
        }

        if (!Uri.TryCreate(fallback.Url, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            validationError = "qBittorrent fallback URL is invalid.";

            return false;
        }

        if (String.IsNullOrWhiteSpace(fallback.Username) || String.IsNullOrWhiteSpace(fallback.Password))
        {
            validationError = "qBittorrent fallback credentials are not configured.";

            return false;
        }

        settings = new(baseUri, fallback.Username.Trim(), fallback.Password, fallback.IgnoreTlsErrors, fallback.Timeout);

        return true;
    }

    private readonly record struct QbittorrentFallbackConfiguration(Uri BaseUri,
                                                                    String UserName,
                                                                    String Password,
                                                                    Boolean IgnoreTlsErrors,
                                                                    Int32 Timeout);
}
