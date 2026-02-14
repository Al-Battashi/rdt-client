import { Pipe, PipeTransform } from '@angular/core';
import { RealDebridStatus, Torrent } from './models/torrent.model';
import { FileSizePipe } from './filesize.pipe';

@Pipe({ name: 'status' })
export class TorrentStatusPipe implements PipeTransform {
  constructor(private pipe: FileSizePipe) {}

  transform(torrent: Torrent): string {
    if (torrent.error) {
      return torrent.error;
    }

    if (torrent.downloads.length > 0) {
      const allFinished = torrent.downloads.every((m) => m.completed != null);

      if (allFinished) {
        return 'Finished';
      }

      const downloading = torrent.downloads.filter((m) => m.downloadStarted && !m.downloadFinished && m.bytesDone > 0);
      const downloaded = torrent.downloads.filter((m) => m.downloadFinished != null);

      if (downloading.length > 0) {
        const bytesDone = downloading.reduce((sum, m) => sum + m.bytesDone, 0);
        const bytesTotal = downloading.reduce((sum, m) => sum + m.bytesTotal, 0);
        const progress = (bytesDone / bytesTotal || 0) * 100;

        const allSpeeds = downloading.reduce((sum, m) => sum + m.speed, 0);

        const speed: string | string[] = this.pipe.transform(allSpeeds, 'filesize');

        return `Downloading from provider ${downloading.length + downloaded.length}/${
          torrent.downloads.length
        } (${progress.toFixed(2)}% - ${speed}/s)`;
      }

      const unpacking = torrent.downloads.filter((m) => m.unpackingStarted && !m.unpackingFinished && m.bytesDone > 0);
      const unpacked = torrent.downloads.filter((m) => m.unpackingFinished != null);

      if (unpacking.length > 0) {
        const bytesDone = unpacking.reduce((sum, m) => sum + m.bytesDone, 0);
        const bytesTotal = unpacking.reduce((sum, m) => sum + m.bytesTotal, 0);
        const progress = (bytesDone / bytesTotal || 0) * 100;

        return `Extracting file ${unpacking.length + unpacked.length}/${torrent.downloads.length} (${progress.toFixed(
          2,
        )}%)`;
      }

      const queuedForUnpacking = torrent.downloads.filter((m) => m.unpackingQueued && !m.unpackingStarted);

      if (queuedForUnpacking.length > 0) {
        return `Queued for unpacking`;
      }

      const queuedForDownload = torrent.downloads.filter((m) => !m.downloadStarted && !m.downloadFinished);

      if (queuedForDownload.length > 0) {
        return `Queued for provider download`;
      }

      if (unpacked.length > 0) {
        return `Files unpacked`;
      }

      if (downloaded.length > 0) {
        return `Files downloaded to host`;
      }
    }

    if (torrent.completed) {
      return 'Finished';
    }

    switch (torrent.rdStatus) {
      case RealDebridStatus.Queued:
        if (torrent.rdHost === 'qBittorrent') {
          return 'Queued in torrent client';
        }
        return 'Queued to provider';
      case RealDebridStatus.Downloading:
        if (torrent.rdHost === 'qBittorrent') {
          const speed = this.pipe.transform(torrent.rdSpeed, 'filesize');
          const progress = torrent.rdProgress ?? 0;
          const state = (torrent.rdStatusRaw ?? '').toLowerCase();

          if (state.includes('paused')) {
            return `Downloading via torrent client (paused ${progress}%)`;
          }

          if (state.includes('queued')) {
            return `Queued in torrent client (${progress}%)`;
          }

          if (state.includes('meta') || state.includes('check') || state.includes('allocat') || state.includes('mov')) {
            return `Preparing in torrent client`;
          }

          if ((torrent.rdSpeed ?? 0) > 0) {
            return `Downloading via torrent client (${progress}% - ${speed}/s)`;
          }

          if (state.includes('stalled')) {
            return `Downloading via torrent client (stalled ${progress}%)`;
          }

          return `Downloading via torrent client (stalled ${progress}%)`;
        }

        if (torrent.rdSeeders < 1) {
          return `Downloading on provider (stalled)`;
        }
        const speed = this.pipe.transform(torrent.rdSpeed, 'filesize');
        return `Downloading on provider (${torrent.rdProgress}% - ${speed}/s)`;
      case RealDebridStatus.Processing:
        if (torrent.rdHost === 'qBittorrent') {
          return `Preparing in torrent client`;
        }
        return `Provider is processing torrent`;
      case RealDebridStatus.WaitingForFileSelection:
        return `Waiting for provider file selection`;
      case RealDebridStatus.Error:
        return `Torrent error: ${torrent.rdStatusRaw}`;
      case RealDebridStatus.Finished:
        if (torrent.rdHost === 'qBittorrent') {
          return `Torrent client download complete`;
        }
        return `Provider download complete, waiting for links`;
      case RealDebridStatus.Uploading:
        return `Uploading to provider`;
      default:
        return 'Unknown status';
    }
  }
}
