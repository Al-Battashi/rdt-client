using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services.DebridClients;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace RdtClient.Service.Services;

public class UnpackClient(Download download, String destinationPath)
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly Torrent _torrent = download.Torrent ?? throw new($"Torrent is null");
    public Boolean Finished { get; private set; }

    public String? Error { get; private set; }

    public Int32 Progess { get; private set; }

    public void Start()
    {
        Progess = 0;

        try
        {
            var filePath = DownloadHelper.GetDownloadPath(destinationPath, _torrent, download) ?? throw new("Invalid download path");

            Task.Run(async delegate
            {
                if (!_cancellationTokenSource.IsCancellationRequested)
                {
                    await Unpack(filePath, _cancellationTokenSource.Token);
                }
            });
        }
        catch (Exception ex)
        {
            Error = $"An unexpected error occurred preparing download {download.Link} for torrent {_torrent.RdName}: {ex.Message}";
            Finished = true;
        }
    }

    public void Cancel()
    {
        _cancellationTokenSource.Cancel();
    }

    private async Task Unpack(String filePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var extractPath = destinationPath;
            String? extractPathTemp = null;

            var archiveEntries = await GetArchiveFiles(filePath);

            if (!archiveEntries.Any(m => m.StartsWith(_torrent.RdName + @"\")) && !archiveEntries.Any(m => m.StartsWith(_torrent.RdName + "/")))
            {
                extractPath = Path.Combine(destinationPath, _torrent.RdName!);
            }

            if (archiveEntries.Any(m => m.Contains(".r00")))
            {
                extractPathTemp = Path.Combine(extractPath, Guid.NewGuid().ToString());

                if (!Directory.Exists(extractPathTemp))
                {
                    Directory.CreateDirectory(extractPathTemp);
                }
            }

            if (extractPathTemp != null)
            {
                Extract(filePath, extractPathTemp, cancellationToken);

                await FileHelper.Delete(filePath);

                var rarFiles = Directory.GetFiles(extractPathTemp, "*.r00", SearchOption.TopDirectoryOnly);

                foreach (var rarFile in rarFiles)
                {
                    var mainRarFile = Path.ChangeExtension(rarFile, ".rar");

                    if (File.Exists(mainRarFile))
                    {
                        Extract(mainRarFile, extractPath, cancellationToken);
                    }

                    await FileHelper.DeleteDirectory(extractPathTemp);
                }
            }
            else
            {
                Extract(filePath, extractPath, cancellationToken);

                await FileHelper.Delete(filePath);
            }

            if (_torrent.ClientKind == Provider.TorBox)
            {
                TorBoxDebridClient.MoveHashDirContents(extractPath, _torrent);
            }
        }
        catch (Exception ex)
        {
            Error = $"An unexpected error occurred unpacking {download.Link} for torrent {_torrent.RdName}: {ex.Message}";
        }
        finally
        {
            Finished = true;
        }
    }

    private static Task<IList<String>> GetArchiveFiles(String filePath)
    {
        using Stream stream = File.OpenRead(filePath);
        using var archive = ArchiveFactory.OpenArchive(stream, new ReaderOptions());

        var entries = archive.Entries
                             .Where(entry => !entry.IsDirectory)
                             .Select(m => m.Key!)
                             .ToList();

        return Task.FromResult<IList<String>>(entries);
    }

    private void Extract(String filePath, String extractPath, CancellationToken cancellationToken)
    {
        var parts = ArchiveFactory.GetFileParts(filePath);

        var fi = parts.Select(m => new FileInfo(m));

        using var archive = ArchiveFactory.OpenArchive(fi, new ReaderOptions());

        archive.WriteToDirectory(extractPath,
                                 new ExtractionOptions
                                 {
                                     ExtractFullPath = true,
                                     Overwrite = true
                                 },
                                 new ExtractionProgress(progress =>
                                 {
                                     cancellationToken.ThrowIfCancellationRequested();

                                     if (progress.PercentComplete.HasValue)
                                     {
                                         Progess = (Int32)Math.Round(progress.PercentComplete.Value);
                                     }
                                 }));

        GC.Collect();
    }

    private sealed class ExtractionProgress(Action<ProgressReport> handler) : IProgress<ProgressReport>
    {
        public void Report(ProgressReport value)
        {
            handler(value);
        }
    }
}
