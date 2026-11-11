using System.IO;

namespace BiliDesktop;

public sealed class BatchDownloadEngine(BiliApi api, VideoStream? preferredVideo, AudioStream? preferredAudio)
{
    public async Task RunJobAsync(BatchJob job, string directory, int mode, BatchFormat format, string userAgent, TransferControl? control = null, IProgress<string>? queueProgress = null, VideoInfo? namingVideo = null, bool useNaming = false, AudioMetadataOptions? audioOptions = null)
    {
        try
        {
            var sessionVersion = api.SessionVersion;
            IReadOnlyList<VideoPage> pages;
            if (job.Page is null) { job.Status = "正在读取视频分 P…"; var fetched = await api.GetVideoAsync(job.Bvid); pages = fetched.Pages; if (useNaming) namingVideo ??= fetched; }
            else pages = [job.Page];
            if (control is not null) await control.WaitAsync();
            if (api.SessionVersion != sessionVersion) throw new InvalidOperationException("登录会话已变化，请重新解析视频。");
            if (pages.Count == 0) throw new InvalidOperationException("视频没有可用的分 P。");
            var completed = 0; var skipped = 0; var errors = new List<string>();
            for (var index = 0; index < pages.Count; index++)
            {
                control?.PrepareTransfer();
                if (control is not null) await control.WaitAsync();
                var page = pages[index];
                var number = job.Page is null ? index + 1 : job.Number;
                var partDirectory = job.Page is null && namingVideo is not null ? DownloadNaming.Folder(directory, namingVideo) : directory;
                if (!Directory.Exists(partDirectory)) Directory.CreateDirectory(partDirectory);
                var baseName = namingVideo is null ? null : mode == 2 ? DownloadNaming.AudioBaseName(namingVideo, page, job.CollectionItemTitle) : DownloadNaming.BaseName(namingVideo, page, number, preferredVideo, preferredAudio);
                var mp4Path = Path.Combine(partDirectory, baseName is null ? BatchPolicy.FileName(job.Bvid, number, page.Label, mode == 2) : baseName + (mode == 2 ? ".m4a" : ".mp4"));
                var mkvPath = Path.Combine(partDirectory, baseName is null ? BatchPolicy.FileName(job.Bvid, number, page.Label, mode == 2, true) : baseName + ".mkv");
                if (namingVideo is null && BatchPolicy.OutputAlreadyExists(format, mp4Path, mkvPath)) { skipped++; continue; }
                try
                {
                    var part = index + 1;
                    job.Status = "等待下载任务…";
                    var appearedWhileQueued = false;
                    await MediaDownload.InSlotAsync(async () =>
                    {
                        if (namingVideo is null && BatchPolicy.OutputAlreadyExists(format, mp4Path, mkvPath)) { appearedWhileQueued = true; return; }
                        job.Status = $"正在获取分 P {part}/{pages.Count} 的流…"; queueProgress?.Report(job.Status);
                        var streams = await api.GetStreamsForPageAsync(job.Bvid, page, control?.Token ?? CancellationToken.None);
                        if (api.SessionVersion != sessionVersion) throw new InvalidOperationException("登录会话已变化，请重新解析视频。");
                        if (streams.IsPreview) throw new InvalidOperationException("当前账号只能获取预览，未作为完整视频下载。");
                        var selectedVideo = mode == 2 ? null : BatchPolicy.ChooseVideo(streams.Video, preferredVideo);
                        var selectedAudio = mode == 1 ? null : BatchPolicy.ChooseAudio(streams.Audio, preferredAudio);
                        if (mode != 2 && selectedVideo is null || mode != 1 && selectedAudio is null) throw new InvalidOperationException("没有所需的音频或视频流。");
                        if (namingVideo is not null)
                        {
                            var actualName = mode == 2 ? DownloadNaming.AudioBaseName(namingVideo, page, job.CollectionItemTitle) : DownloadNaming.BaseName(namingVideo, page, number, selectedVideo, selectedAudio);
                            mp4Path = Path.Combine(partDirectory, actualName + (mode == 2 ? ".m4a" : ".mp4"));
                            mkvPath = Path.Combine(partDirectory, actualName + ".mkv");
                            if (BatchPolicy.OutputAlreadyExists(format, mp4Path, mkvPath)) { appearedWhileQueued = true; return; }
                        }
                        var path = BatchPolicy.ChooseMatroska(format, selectedAudio) ? mkvPath : mp4Path;
                        var progress = new Progress<string>(message => { if (!job.Finished) { job.Status = $"{part}/{pages.Count} · {message}"; queueProgress?.Report(job.Status); } });
                        var audioMetadata = mode == 2 && audioOptions is not null ? audioOptions.ForVideo(namingVideo ?? throw new InvalidOperationException("无法获取视频信息。"), page, job.CollectionNumber, job.CollectionTitle, job.CollectionItemTitle) : null;
                        await MediaDownload.DownloadWithinSlotAsync(selectedVideo?.Urls, selectedAudio?.Urls, null, path, userAgent, progress, control: control, audioMetadata: audioMetadata, legacySegments: selectedVideo?.LegacySegments ?? selectedAudio?.LegacySegments);
                        if (pages.Count == 1) job.OutputPath = path;
                    }, control?.Token ?? CancellationToken.None, canStart: () => api.SessionVersion == sessionVersion, control: control);
                    if (appearedWhileQueued) skipped++; else completed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"P{number}: {ex.Message}"); }
            }
            job.Finished = true;
            job.HasError = errors.Count > 0;
            job.Status = errors.Count > 0 ? $"失败 {errors.Count}，完成 {completed}，跳过 {skipped}：{string.Join("；", errors)}" : skipped == pages.Count ? "文件已存在，跳过" : skipped > 0 ? $"完成 {completed}，跳过 {skipped}" : "完成";
        }
        catch (OperationCanceledException) { job.Status = "已取消"; throw; }
        catch (Exception ex) { job.HasError = true; job.Status = "失败：" + ex.Message; }
        finally { job.Finished = true; }
    }
}
