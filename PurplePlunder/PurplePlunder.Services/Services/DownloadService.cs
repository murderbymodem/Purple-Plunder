using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PurplePlunder.Core.Enums;
using PurplePlunder.Core.Events;
using PurplePlunder.Core.Models;
using PurplePlunder.Services.Interfaces;
using PurplePlunder.Shared.Events;
using PurplePlunder.Shared.Extensions;
using PurplePlunder.Shared.IO;
using PurplePlunder.Shared.Notification;
using PurplePlunder.Shared.Reflection;

namespace PurplePlunder.Services.Services
{
    internal class DownloadService : BindableBase, IDownloadService, IDisposable
    {
        #region Constants

        private const int TIMER_INTERVALL = 2;
        private const int DOWNLOAD_RETRIES = 3;
        private const int DOWNLOAD_RETRY_TIME = 5;

        private const int LIVE_POLL_SECONDS = 4;
        private const int LIVE_STALE_SECONDS = 20;
        private const int LIVE_CHECK_SECONDS = 10;
        private const int LIVE_MAX_PLAYLIST_FAILURES = 3;
        private const int LIVE_GIVE_UP_MINUTES = 10;

        #endregion Constants

        #region Fields

        private bool disposedValue = false;

        private readonly IApiService _apiService;
        private readonly IPreferencesService _preferencesService;
        private readonly IProcessingService _processingService;
        private readonly IEventAggregator _eventAggregator;

        private readonly Timer _downloadTimer;

        private ObservableCollection<VodDownload> _downloads;

        private ConcurrentDictionary<string, DownloadTask> _downloadTasks;

        private readonly object _changeDownloadLockObject;

        private volatile bool _paused;

        #endregion Fields

        #region Constructors

        public DownloadService(
            IApiService apiService,
            IPreferencesService preferencesService,
            IProcessingService processingService,
            IEventAggregator eventAggregator)
        {
            _apiService = apiService;
            _preferencesService = preferencesService;
            _processingService = processingService;
            _eventAggregator = eventAggregator;

            _downloads = new ObservableCollection<VodDownload>();
            _downloads.CollectionChanged += Downloads_CollectionChanged;

            _downloadTasks = new ConcurrentDictionary<string, DownloadTask>();

            _changeDownloadLockObject = new object();

            _downloadTimer = new Timer(DownloadTimerCallback, null, 0, TIMER_INTERVALL);

            _eventAggregator.GetEvent<RemoveDownloadEvent>().Subscribe(Remove, ThreadOption.UIThread);
        }

        #endregion Constructors

        #region Properties

        public ObservableCollection<VodDownload> Downloads
        {
            get { return _downloads; }
            private set
            {
                if (_downloads != null)
                {
                    _downloads.CollectionChanged -= Downloads_CollectionChanged;
                }

                SetProperty(ref _downloads, value, nameof(Downloads));

                if (_downloads != null)
                {
                    _downloads.CollectionChanged += Downloads_CollectionChanged;
                }

                FireDownloadsCountChanged();
            }
        }

        #endregion Properties

        #region Methods

        public void Enqueue(DownloadParameters downloadParams)
        {
            if (_paused)
            {
                return;
            }

            lock (_changeDownloadLockObject)
            {
                _downloads.Add(new VodDownload(downloadParams));
            }
        }

        private void DownloadTimerCallback(object state)
        {
            if (_paused)
            {
                return;
            }

            StartQueuedDownloadIfExists();
        }

        private void StartQueuedDownloadIfExists()
        {
            if (_paused)
            {
                return;
            }

            if (Monitor.TryEnter(_changeDownloadLockObject))
            {
                try
                {
                    // Live captures start right away and run in parallel, everything else runs one at a time.
                    VodDownload download = _downloads.FirstOrDefault(d =>
                        d.DownloadState == DownloadState.Queued && d.DownloadParams.IsLiveCapture);

                    if (download == null && !_downloads.Any(d =>
                            d.DownloadState == DownloadState.Downloading && !d.DownloadParams.IsLiveCapture))
                    {
                        download = _downloads.FirstOrDefault(d =>
                            d.DownloadState == DownloadState.Queued && !d.DownloadParams.IsLiveCapture);
                    }

                    if (download != null)
                    {

                        DownloadParameters downloadParams = download.DownloadParams;

                        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                        CancellationToken cancellationToken = cancellationTokenSource.Token;

                        string downloadId = download.Id;
                        string vodId = downloadParams.Video.Id;
                        string tempDir = Path.Combine(_preferencesService.CurrentPreferences.DownloadTempFolder,
                            downloadId);
                        string ffmpegFile = _processingService.FFMPEGExe;
                        string concatFile = Path.Combine(tempDir,
                            Path.GetFileNameWithoutExtension(downloadParams.FullPath) + ".ts");
                        string outputFile = downloadParams.FullPath;

                        bool disableConversion = downloadParams.DisableConversion;
                        bool cropStart = downloadParams.CropStart;
                        bool cropEnd = downloadParams.CropEnd;

                        TimeSpan cropStartTime = downloadParams.CropStartTime;
                        TimeSpan cropEndTime = downloadParams.CropEndTime;

                        VodQuality quality = downloadParams.SelectedQuality;

                        Action<DownloadState> setDownloadState = download.SetDownloadState;
                        Action<string> log = download.AppendLog;
                        Action<string> setStatus = download.SetStatus;
                        Action<double> setProgress = download.SetProgress;
                        Action<bool> setIsIndeterminate = download.SetIsIndeterminate;

                        Task<IEnumerable<string>> downloadVideoTask = new Task<IEnumerable<string>>(() =>
                        {
                            setStatus("Initializing");

                            log("Download task has been started!");

                            WriteDownloadInfo(log, downloadParams, ffmpegFile, tempDir);

                            cancellationToken.ThrowIfCancellationRequested();

                            log(Environment.NewLine + Environment.NewLine + "Retrieving VOD access information...");
                            VodAuthInfo vodAuthInfo = _apiService.GetVodAuthInfo(vodId);
                            log(" done!");

                            cancellationToken.ThrowIfCancellationRequested();

                            WriteVodAuthInfo(log, vodAuthInfo);

                            cancellationToken.ThrowIfCancellationRequested();

                            CheckTempDirectory(log, tempDir);

                            cancellationToken.ThrowIfCancellationRequested();

                            log(Environment.NewLine + Environment.NewLine +
                                "Retrieving playlist information for all VOD qualities...");
                            Dictionary<VodQuality, string> playlistInfo =
                                _apiService.GetPlaylistInfo(vodId, vodAuthInfo);
                            log(" done!");

                            cancellationToken.ThrowIfCancellationRequested();

                            WritePlaylistInfo(log, playlistInfo);

                            cancellationToken.ThrowIfCancellationRequested();

                            if (downloadParams.IsLiveCapture)
                            {
                                List<string> liveWarnings = CaptureLive(log, setStatus, setIsIndeterminate, tempDir,
                                    downloadParams, playlistInfo, quality, out string recordingFile, cancellationToken);

                                FinishRecording(log, setStatus, setProgress, setIsIndeterminate, recordingFile,
                                    outputFile, disableConversion);

                                return liveWarnings;
                            }

                            VodPlaylist vodPlaylist;
                            CropInfo cropInfo;
                            IEnumerable<string> downloadWarnings;

                            {
                                vodPlaylist = GetVodPlaylist(log, tempDir, playlistInfo, quality);

                                cancellationToken.ThrowIfCancellationRequested();

                                cropInfo = CropVodPlaylist(vodPlaylist, cropStart, cropEnd, cropStartTime,
                                    cropEndTime);

                                cancellationToken.ThrowIfCancellationRequested();

                                downloadWarnings = DownloadParts(log, setStatus, setProgress, vodPlaylist,
                                    cancellationToken);
                            }

                            cancellationToken.ThrowIfCancellationRequested();

                            CheckOutputDirectory(log, Path.GetDirectoryName(outputFile));

                            cancellationToken.ThrowIfCancellationRequested();

                            var processingWarnings = _processingService.ConcatParts(log, setStatus, setProgress, vodPlaylist,
                                disableConversion ? outputFile : concatFile);

                            if (!disableConversion)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                _processingService.ConvertVideo(log, setStatus, setProgress, setIsIndeterminate,
                                    concatFile, outputFile, cropInfo);
                            }

                            List<string> warnings = new List<string>();
                            warnings.AddRange(downloadWarnings);
                            warnings.AddRange(processingWarnings);

                            return warnings;
                        }, cancellationToken,
                            downloadParams.IsLiveCapture ? TaskCreationOptions.LongRunning : TaskCreationOptions.None);

                        Task continueTask = downloadVideoTask.ContinueWith((Task<IEnumerable<string>> task) =>
                        {
                            log(Environment.NewLine + Environment.NewLine +
                                "Starting temporary download folder cleanup!");
                            CleanUp(tempDir, log);

                            setProgress(100);
                            setIsIndeterminate(false);

                            bool success = false;

                            if (task.IsFaulted)
                            {
                                setDownloadState(DownloadState.Error);
                                log(Environment.NewLine + Environment.NewLine + "Download task ended with an error!");

                                if (task.Exception != null)
                                {
                                    log(Environment.NewLine + Environment.NewLine + task.Exception.ToString());
                                }
                            }
                            else if (task.IsCanceled)
                            {
                                setDownloadState(DownloadState.Canceled);
                                log(Environment.NewLine + Environment.NewLine + "Download task was canceled!");
                            }
                            else
                            {
                                success = true;
                                var warnings = task.Result.ToArray();
                                if (warnings.Any())
                                {
                                    var warningsString = string.Join("\n", warnings.Distinct());
                                    setDownloadState(DownloadState.CompletedWithWarning);
                                    log(Environment.NewLine + Environment.NewLine +
                                        $"Download completed with warnings:\n{warningsString}");
                                }
                                else
                                {
                                    setDownloadState(DownloadState.Done);
                                    log(Environment.NewLine + Environment.NewLine +
                                        "Download task ended successfully!");
                                }
                            }

                            if (!_downloadTasks.TryRemove(downloadId, out DownloadTask downloadTask))
                            {
                                throw new ApplicationException("Could not remove download task with ID '" + downloadId +
                                                               "' from download task collection!");
                            }

                            if (success && _preferencesService.CurrentPreferences.DownloadRemoveCompleted)
                            {
                                _eventAggregator.GetEvent<RemoveDownloadEvent>().Publish(downloadId);
                            }
                        });

                        if (_downloadTasks.TryAdd(downloadId,
                                new DownloadTask(downloadVideoTask, continueTask, cancellationTokenSource)))
                        {
                            downloadVideoTask.Start();
                            setDownloadState(DownloadState.Downloading);
                        }
                    }
                }
                finally
                {
                    Monitor.Exit(_changeDownloadLockObject);
                }
            }
        }

        private void WriteDownloadInfo(Action<string> log, DownloadParameters downloadParams, string ffmpegFile,
            string tempDir)
        {
            log(Environment.NewLine + Environment.NewLine + "PURPLE PLUNDER INFO");
            log(Environment.NewLine +
                "--------------------------------------------------------------------------------------------");
            log(Environment.NewLine + "Version: " + AssemblyUtil.Get.GetAssemblyVersion().Trim());

            log(Environment.NewLine + Environment.NewLine + "VOD INFO");
            log(Environment.NewLine +
                "--------------------------------------------------------------------------------------------");
            log(Environment.NewLine + "VOD ID: " + downloadParams.Video.Id);
            log(Environment.NewLine + "Selected Quality: " + downloadParams.SelectedQuality.DisplayString);
            log(Environment.NewLine + "Download Url: " + downloadParams.Video.Url);
            log(Environment.NewLine + "Crop Start: " + (downloadParams.CropStart
                ? "Yes (" + downloadParams.CropStartTime.ToDaylessString() + ")"
                : "No"));
            log(Environment.NewLine + "Crop End: " + (downloadParams.CropEnd
                ? "Yes (" + downloadParams.CropEndTime.ToDaylessString() + ")"
                : "No"));

            log(Environment.NewLine + Environment.NewLine + "OUTPUT INFO");
            log(Environment.NewLine +
                "--------------------------------------------------------------------------------------------");
            log(Environment.NewLine + "Disable Conversion: " + (downloadParams.DisableConversion ? "Yes" : "No"));
            log(Environment.NewLine + "Output File: " + downloadParams.FullPath);
            log(Environment.NewLine + "FFMPEG Path: " + ffmpegFile);
            log(Environment.NewLine + "Temporary Download Folder: " + tempDir);
        }

        private void WriteVodAuthInfo(Action<string> log, VodAuthInfo vodAuthInfo)
        {
            log(Environment.NewLine + Environment.NewLine + "ACCESS INFO");
            log(Environment.NewLine +
                "--------------------------------------------------------------------------------------------");
            log(Environment.NewLine + "Token: " + vodAuthInfo.Token);
            log(Environment.NewLine + "Signature: " + vodAuthInfo.Signature);
            log(Environment.NewLine + "Sub-Only: " + (vodAuthInfo.SubOnly ? "Yes" : "No"));
            log(Environment.NewLine + "Privileged: " + (vodAuthInfo.Privileged ? "Yes" : "No"));
        }

        private void WritePlaylistInfo(Action<string> log, Dictionary<VodQuality, string> playlistInfo)
        {
            log(Environment.NewLine + Environment.NewLine + "PLAYLIST INFO");
            log(Environment.NewLine +
                "--------------------------------------------------------------------------------------------");

            foreach (var entry in playlistInfo)
            {
                log(Environment.NewLine + "Playlist for quality '" + entry.Key.Name + "' is '" + entry.Value + "'");
            }
        }

        private void CheckTempDirectory(Action<string> log, string tempDir)
        {
            if (!Directory.Exists(tempDir))
            {
                log(Environment.NewLine + Environment.NewLine + "Creating temporary download directory '" + tempDir +
                    "'...");
                FileSystem.CreateDirectory(tempDir);
                log(" done!");
            }

            if (Directory.EnumerateFileSystemEntries(tempDir).Any())
            {
                throw new ApplicationException("Temporary download directory '" + tempDir + "' is not empty!");
            }
        }

        private void CheckOutputDirectory(Action<string> log, string outputDir)
        {
            if (!Directory.Exists(outputDir))
            {
                log(Environment.NewLine + Environment.NewLine + "Creating output directory '" + outputDir + "'...");
                FileSystem.CreateDirectory(outputDir);
                log(" done!");
            }
        }

        private VodPlaylist GetVodPlaylist(Action<string> log, string tempDir,
            Dictionary<VodQuality, string> playlistInfo, VodQuality selectedQuality)
        {
            VodQuality quality = playlistInfo.Keys.First(q => q.Equals(selectedQuality));

            string playlistUrl = playlistInfo[quality];

            log(Environment.NewLine + Environment.NewLine + "Playlist url for selected quality '" +
                quality.DisplayString + "' is '" + playlistUrl + "'");

            using (WebClient webClient = new WebClient())
            {
                log(Environment.NewLine + Environment.NewLine + "Retrieving playlist...");
                string playlistStr = webClient.DownloadString(playlistUrl);
                log(" done!");

                if (string.IsNullOrWhiteSpace(playlistStr))
                {
                    throw new ApplicationException("The playlist is empty!");
                }

                string urlPrefix = playlistUrl.Substring(0, playlistUrl.LastIndexOf("/") + 1);

                log(Environment.NewLine + "Parsing playlist...");
                VodPlaylist vodPlaylist = VodPlaylist.Parse(tempDir, playlistStr, urlPrefix);
                log(" done!");

                log(Environment.NewLine + "Number of video chunks: " + vodPlaylist.Count());

                return vodPlaylist;
            }
        }

        private List<string> CaptureLive(Action<string> log, Action<string> setStatus,
            Action<bool> setIsIndeterminate, string tempDir, DownloadParameters downloadParams,
            Dictionary<VodQuality, string> playlistInfo, VodQuality selectedQuality,
            out string recordingFile, CancellationToken cancellationToken)
        {
            LiveRecordingWriter writer = null;
            List<string> warnings = new List<string>();
            HashSet<string> handledParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string playlistUrl = playlistInfo[playlistInfo.Keys.First(q => q.Equals(selectedQuality))];
            string vodId = downloadParams.Video.Id;
            string channel = downloadParams.LiveChannelLogin;
            string streamId = downloadParams.LiveStreamId;

            log(Environment.NewLine + Environment.NewLine + "LIVE CAPTURE");
            log(Environment.NewLine +
                "--------------------------------------------------------------------------------------------");
            log(Environment.NewLine + "Channel: " + channel);
            log(Environment.NewLine + "Stream ID: " + streamId);
            log(Environment.NewLine + "Playlist url: " + playlistUrl);
            log(Environment.NewLine + "Chunks are downloaded while the stream is live and the capture " +
                "finishes as soon as the stream has ended.");

            setIsIndeterminate(true);
            setStatus("Recording live");

            VodPlaylist latest = null;
            int lastCount = 0;
            DateTime lastGrowth = DateTime.UtcNow;
            DateTime lastLiveCheck = DateTime.MinValue;
            int playlistFailures = 0;
            bool streamEnded = false;

            KeepSystemAwake(true);

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool hasEndList = false;

                    try
                    {
                        string playlistStr;

                        using (WebClient webClient = new WebClient())
                        {
                            playlistStr = webClient.DownloadString(playlistUrl);
                        }

                        playlistFailures = 0;

                        if (!string.IsNullOrWhiteSpace(playlistStr))
                        {
                            hasEndList = playlistStr.Contains("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase);

                            string urlPrefix = playlistUrl.Substring(0, playlistUrl.LastIndexOf("/") + 1);
                            latest = VodPlaylist.Parse(tempDir, playlistStr, urlPrefix);
                        }
                    }
                    catch (WebException ex)
                    {
                        playlistFailures++;
                        log(Environment.NewLine + "Retrieving the live playlist failed (" + playlistFailures +
                            "): " + ex.Message);

                        if (playlistFailures % 3 == 0)
                        {
                            // The signed playlist url might have expired, get a fresh one
                            try
                            {
                                VodAuthInfo authInfo = _apiService.GetVodAuthInfo(vodId);
                                Dictionary<VodQuality, string> freshInfo =
                                    _apiService.GetPlaylistInfo(vodId, authInfo);
                                VodQuality freshQuality = freshInfo.Keys.FirstOrDefault(q => q.Equals(selectedQuality));

                                if (freshQuality != null)
                                {
                                    playlistUrl = freshInfo[freshQuality];
                                    log(Environment.NewLine + "Refreshed playlist url: " + playlistUrl);
                                }
                            }
                            catch (Exception refreshEx)
                            {
                                log(Environment.NewLine + "Refreshing the playlist url failed: " + refreshEx.Message);
                            }
                        }
                    }

                    if (latest != null)
                    {
                        if (latest.InitSegment != null && !File.Exists(latest.InitSegment.LocalFile))
                        {
                            DownloadInitSegment(latest.InitSegment, log);
                        }

                        List<VodPlaylistPart> newParts =
                            latest.Where(p => !handledParts.Contains(p.LocalFile)).ToList();

                        if (newParts.Count > 0)
                        {
                            warnings.AddRange(DownloadLiveParts(log, newParts, handledParts, cancellationToken));
                        }

                        if (writer == null)
                        {
                            writer = LiveRecordingWriter.Create(downloadParams.Folder, downloadParams.Filename,
                                latest.InitSegment != null);
                            log(Environment.NewLine + "Recording to '" + writer.Path + "'");
                        }

                        writer.AppendReady(latest, handledParts, log, warnings);

                        if (latest.Count > lastCount)
                        {
                            lastCount = latest.Count;
                            lastGrowth = DateTime.UtcNow;
                        }

                        setStatus("Recording live (" + handledParts.Count + " chunks, " +
                                  TimeSpan.FromSeconds(Math.Round(latest.Sum(p => p.Length))).ToDaylessString() + ")");
                    }

                    if (streamEnded)
                    {
                        // One last pass after the stream ended has been done, nothing left to fetch
                        break;
                    }

                    // Only ask the API once the playlist stops growing, to keep the checks cheap.
                    TimeSpan sinceGrowth = DateTime.UtcNow - lastGrowth;
                    bool stale = sinceGrowth > TimeSpan.FromSeconds(LIVE_STALE_SECONDS) || hasEndList ||
                                 playlistFailures > 0;

                    if (stale && DateTime.UtcNow - lastLiveCheck > TimeSpan.FromSeconds(LIVE_CHECK_SECONDS))
                    {
                        lastLiveCheck = DateTime.UtcNow;

                        // If the API cannot be reached, a playlist that stopped growing long ago means the stream is over
                        if (sinceGrowth > TimeSpan.FromMinutes(LIVE_GIVE_UP_MINUTES) ||
                            !IsStreamStillLive(channel, streamId, log))
                        {
                            log(Environment.NewLine + Environment.NewLine + "The stream has ended, fetching the last chunks...");
                            streamEnded = true;

                            if (playlistFailures >= LIVE_MAX_PLAYLIST_FAILURES)
                            {
                                // The VOD playlist is gone (e.g. the VOD was deleted), keep what we have
                                break;
                            }

                            continue;
                        }
                    }

                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(LIVE_POLL_SECONDS));
                }
            }
            finally
            {
                KeepSystemAwake(false);
                writer?.Dispose();
            }

            if (latest == null || latest.Count == 0 || writer == null || writer.AppendedChunks == 0)
            {
                throw new ApplicationException("No video chunks could be captured!");
            }

            setIsIndeterminate(false);

            log(Environment.NewLine + Environment.NewLine + "Live capture complete, " + handledParts.Count +
                " chunks captured.");

            recordingFile = writer.Path;
            return warnings;
        }

        private bool IsStreamStillLive(string channel, string streamId, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                return false;
            }

            try
            {
                Dictionary<string, LiveStreamInfo> streams = _apiService.GetLiveStreams(new[] { channel });

                return streams.TryGetValue(channel, out LiveStreamInfo stream) &&
                       (string.IsNullOrWhiteSpace(streamId) || stream.StreamId == streamId);
            }
            catch (Exception ex)
            {
                // When in doubt keep recording, the next check decides
                log(Environment.NewLine + "Checking the stream status failed: " + ex.Message);
                return true;
            }
        }

        private IEnumerable<string> DownloadLiveParts(Action<string> log, List<VodPlaylistPart> parts,
            HashSet<string> handledParts, CancellationToken cancellationToken)
        {
            List<string> warnings = new List<string>();
            object syncRoot = new object();

            Parallel.ForEach(parts,
                new ParallelOptions()
                {
                    MaxDegreeOfParallelism = Math.Max(1, _preferencesService.CurrentPreferences.MaxConnectionCount - 1),
                    CancellationToken = cancellationToken
                },
                part =>
                {
                    bool done = false;

                    for (int attempt = 0; attempt <= DOWNLOAD_RETRIES && !done; attempt++)
                    {
                        foreach (string candidate in SegmentNames.GetCandidates(part.RemoteFile))
                        {
                            try
                            {
                                using (WebClient downloadClient = new WebClient())
                                {
                                    byte[] bytes = downloadClient.DownloadData(candidate);
                                    FileSystem.DeleteFile(part.LocalFile);
                                    File.WriteAllBytes(part.LocalFile, bytes);
                                }

                                done = true;
                                break;
                            }
                            catch (WebException ex) when (ex.Status == WebExceptionStatus.ProtocolError)
                            {
                                // Try the next candidate
                            }
                            catch (WebException ex)
                            {
                                log(Environment.NewLine + "Downloading '" + candidate + "' failed: " + ex.Message);
                                break;
                            }
                        }

                        if (!done && attempt < DOWNLOAD_RETRIES)
                        {
                            Thread.Sleep(1000);
                        }
                    }

                    lock (syncRoot)
                    {
                        if (!done)
                        {
                            warnings.Add("Video contains missing frames");
                            log(Environment.NewLine + "File '" + part.RemoteFile + "' is not available, skipping");
                        }

                        handledParts.Add(part.LocalFile);
                    }
                });

            return warnings;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private static readonly object _inhibitLock = new object();
        private static int _inhibitCount;
        private static Process _inhibitProcess;

        /// <summary>
        /// Prevents the PC from going to sleep while a live capture is running
        /// </summary>
        private static void KeepSystemAwake(bool keepAwake)
        {
            if (OperatingSystem.IsLinux())
            {
                InhibitLinuxSleep(keepAwake);
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            const uint ES_CONTINUOUS = 0x80000000;
            const uint ES_SYSTEM_REQUIRED = 0x00000001;

            try
            {
                SetThreadExecutionState(keepAwake ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
            }
            catch
            {
                // Not critical
            }
        }

        /// <summary>
        /// Turns the finished recording into the final video with a lossless stream copy
        /// </summary>
        private void FinishRecording(Action<string> log, Action<string> setStatus, Action<double> setProgress,
            Action<bool> setIsIndeterminate, string recordingFile, string outputFile, bool disableConversion)
        {
            CheckOutputDirectory(log, Path.GetDirectoryName(outputFile));

            if (disableConversion)
            {
                log(Environment.NewLine + Environment.NewLine + "Moving recording to '" + outputFile + "'");
                File.Move(recordingFile, outputFile, true);
                return;
            }

            _processingService.ConvertVideo(log, setStatus, setProgress, setIsIndeterminate,
                recordingFile, outputFile, new CropInfo(false, false, 0, 0));

            FileSystem.DeleteFile(recordingFile);
        }

        /// <summary>
        /// Holds a systemd-logind sleep and idle inhibitor while at least one capture runs. KDE Plasma (PowerDevil)
        /// and other desktops honor it and list Purple Plunder as the reason sleep is blocked.
        /// </summary>
        private static void InhibitLinuxSleep(bool inhibit)
        {
            lock (_inhibitLock)
            {
                _inhibitCount = Math.Max(0, _inhibitCount + (inhibit ? 1 : -1));

                try
                {
                    if (_inhibitCount > 0 && (_inhibitProcess == null || _inhibitProcess.HasExited))
                    {
                        ProcessStartInfo psi = new ProcessStartInfo("systemd-inhibit")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        foreach (string arg in new[] { "--what=sleep:idle", "--who=Purple Plunder",
                                     "--why=Recording a live stream", "--mode=block", "sleep", "infinity" })
                        {
                            psi.ArgumentList.Add(arg);
                        }

                        _inhibitProcess = Process.Start(psi);
                    }
                    else if (_inhibitCount == 0 && _inhibitProcess != null)
                    {
                        if (!_inhibitProcess.HasExited)
                        {
                            _inhibitProcess.Kill(true);
                        }

                        _inhibitProcess.Dispose();
                        _inhibitProcess = null;
                    }
                }
                catch
                {
                    // No systemd-logind, recording works but the PC may go to sleep
                }
            }
        }

        private CropInfo CropVodPlaylist(VodPlaylist vodPlaylist, bool cropStart, bool cropEnd,
            TimeSpan cropStartTime, TimeSpan cropEndTime)
        {
            double start = cropStartTime.TotalMilliseconds;
            double end = cropEndTime.TotalMilliseconds;
            double length = cropEndTime.TotalMilliseconds;

            if (cropStart)
            {
                length -= start;
            }

            start = Math.Round(start / 1000, 3);
            end = Math.Round(end / 1000, 3);
            length = Math.Round(length / 1000, 3);

            List<VodPlaylistPart> deleteStart = new List<VodPlaylistPart>();
            List<VodPlaylistPart> deleteEnd = new List<VodPlaylistPart>();

            if (cropStart)
            {
                double lengthSum = 0;

                foreach (VodPlaylistPart part in vodPlaylist)
                {
                    double partLength = part.Length;

                    if (lengthSum + partLength < start)
                    {
                        lengthSum += partLength;
                        deleteStart.Add(part);
                    }
                    else
                    {
                        start = Math.Round(start - lengthSum, 3);
                        break;
                    }
                }
            }

            if (cropEnd)
            {
                double lengthSum = 0;

                foreach (VodPlaylistPart part in vodPlaylist)
                {
                    if (lengthSum >= end)
                    {
                        deleteEnd.Add(part);
                    }

                    lengthSum += part.Length;
                }
            }

            deleteStart.ForEach(part => { vodPlaylist.Remove(part); });

            deleteEnd.ForEach(part => { vodPlaylist.Remove(part); });

            return new CropInfo(cropStart, cropEnd, cropStart ? start : 0, length);
        }

        private IEnumerable<string> DownloadParts(Action<string> log, Action<string> setStatus,
            Action<double> setProgress,
            VodPlaylist vodPlaylist,
            CancellationToken cancellationToken)
        {
            var warnings = new List<string>();
            int partsCount = vodPlaylist.Count;
            int maxConnectionCount = _preferencesService.CurrentPreferences.MaxConnectionCount;

            log(Environment.NewLine + Environment.NewLine + "Starting parallel video chunk download");
            log(Environment.NewLine + "Number of video chunks to download: " + partsCount);
            log(Environment.NewLine + "Maximum connection count: " + maxConnectionCount);

            setStatus("Downloading");

            if (vodPlaylist.InitSegment != null)
            {
                log(Environment.NewLine + Environment.NewLine + "Downloading VOD init segment...");
                DownloadInitSegment(vodPlaylist.InitSegment, log);
                log(" done!");
            }

            log(Environment.NewLine + Environment.NewLine + "Parallel video chunk download is running...");

            long completedPartDownloads = 0;

            Parallel.ForEach(vodPlaylist,
                new ParallelOptions() { MaxDegreeOfParallelism = maxConnectionCount - 1 },
                (part, loopState) =>
                {
                    int retryCounter = 0;

                    bool success = false;

                    do
                    {
                        try
                        {
                            using (var downloadClient = new WebClient())
                            {
                                byte[] bytes = DownloadSegment(downloadClient, part.RemoteFile);

                                FileSystem.DeleteFile(part.LocalFile);

                                File.WriteAllBytes(part.LocalFile, bytes);

                                long completed = Interlocked.Increment(ref completedPartDownloads);

                                setProgress((double)completed / partsCount * 100);

                                success = true;
                            }
                        }
                        catch (WebException ex)
                        {
                            if (ex.Status == WebExceptionStatus.ProtocolError)
                            {
                                warnings.Add("Video contains missing frames");
                                log($"{Environment.NewLine}File '{part.RemoteFile}' is not available, skipping");

                                long completed = Interlocked.Increment(ref completedPartDownloads);

                                setProgress((double)completed / partsCount * 100);

                                success = true;
                                continue;
                            }

                            if (retryCounter < DOWNLOAD_RETRIES)
                            {
                                retryCounter++;
                                log(Environment.NewLine + Environment.NewLine + "Downloading file '" + part.RemoteFile +
                                    "' failed! Trying again in " + DOWNLOAD_RETRY_TIME + "s");
                                log(Environment.NewLine + ex);
                                Thread.Sleep(DOWNLOAD_RETRY_TIME * 1000);
                            }
                        }
                    } while (!success);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        loopState.Stop();
                    }
                });

            setProgress(100);

            log(Environment.NewLine + Environment.NewLine + "Download of all video chunks complete!");
            return warnings;
        }

        /// <summary>
        /// Downloads a chunk, preferring the version with the original audio and falling back to the muted copy.
        /// Throws the last protocol error if no version exists.
        /// </summary>
        private static byte[] DownloadSegment(WebClient client, string remoteFile)
        {
            WebException lastProtocolError = null;

            foreach (string candidate in SegmentNames.GetCandidates(remoteFile))
            {
                try
                {
                    return client.DownloadData(candidate);
                }
                catch (WebException ex) when (ex.Status == WebExceptionStatus.ProtocolError)
                {
                    lastProtocolError = ex;
                }
            }

            throw lastProtocolError ?? new WebException("Chunk not found", WebExceptionStatus.ProtocolError);
        }

        private void DownloadInitSegment(VodPlaylistPart initSegment, Action<string> log)
        {
            int retryCounter = 0;

            while (true)
            {
                try
                {
                    using (var downloadClient = new WebClient())
                    {
                        byte[] bytes = downloadClient.DownloadData(initSegment.RemoteFile);

                        FileSystem.DeleteFile(initSegment.LocalFile);

                        File.WriteAllBytes(initSegment.LocalFile, bytes);
                    }

                    return;
                }
                catch (WebException ex)
                {
                    if (retryCounter >= DOWNLOAD_RETRIES)
                    {
                        throw;
                    }

                    retryCounter++;
                    log(Environment.NewLine + Environment.NewLine + "Downloading init segment '" + initSegment.RemoteFile +
                        "' failed! Trying again in " + DOWNLOAD_RETRY_TIME + "s");
                    log(Environment.NewLine + ex);
                    Thread.Sleep(DOWNLOAD_RETRY_TIME * 1000);
                }
            }
        }

        private void CleanUp(string directory, Action<string> log)
        {
            try
            {
                log(Environment.NewLine + "Deleting directory '" + directory + "'...");
                FileSystem.DeleteDirectory(directory);
                log(" done!");
            }
            catch
            {
            }
        }

        public void Cancel(string id)
        {
            lock (_changeDownloadLockObject)
            {
                if (_downloadTasks.TryGetValue(id, out DownloadTask downloadTask))
                {
                    downloadTask.CancellationTokenSource.Cancel();
                }
            }
        }

        public void Retry(string id)
        {
            if (_paused)
            {
                return;
            }

            lock (_changeDownloadLockObject)
            {
                if (!_downloadTasks.TryGetValue(id, out DownloadTask downloadTask))
                {
                    VodDownload download = _downloads.Where(d => d.Id == id).FirstOrDefault();

                    if (download != null && (download.DownloadState == DownloadState.Canceled ||
                                             download.DownloadState == DownloadState.Error))
                    {
                        download.ResetLog();
                        download.SetProgress(0);
                        download.SetDownloadState(DownloadState.Queued);
                        download.SetStatus("Initializing");
                    }
                }
            }
        }

        public void Remove(string id)
        {
            lock (_changeDownloadLockObject)
            {
                if (!_downloadTasks.TryGetValue(id, out DownloadTask downloadTask))
                {
                    VodDownload download = _downloads.Where(d => d.Id == id).FirstOrDefault();

                    if (download != null)
                    {
                        _downloads.Remove(download);
                    }
                }
            }
        }

        public void Pause()
        {
            _paused = true;
            _downloadTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public void Resume()
        {
            _paused = false;
            _downloadTimer.Change(0, TIMER_INTERVALL);
        }

        public bool CanShutdown()
        {
            Monitor.Enter(_changeDownloadLockObject);

            try
            {
                return !_downloads.Where(d =>
                    d.DownloadState == DownloadState.Downloading || d.DownloadState == DownloadState.Queued).Any();
            }
            finally
            {
                Monitor.Exit(_changeDownloadLockObject);
            }
        }

        public void Shutdown()
        {
            Pause();

            foreach (DownloadTask downloadTask in _downloadTasks.Values)
            {
                downloadTask.CancellationTokenSource.Cancel();
            }

            List<Task> tasks = _downloadTasks.Values.Select(v => v.Task).ToList();
            tasks.AddRange(_downloadTasks.Values.Select(v => v.ContinueTask).ToList());

            try
            {
                Task.WaitAll(tasks.ToArray());
            }
            catch (Exception)
            {
                // Don't care about aborted tasks
            }

            List<string> toRemove = _downloads.Select(d => d.Id).ToList();

            foreach (string id in toRemove)
            {
                Remove(id);
            }
        }

        public bool IsFileNameUsed(string fullPath)
        {
            IEnumerable<VodDownload> downloads = _downloads.Where(d =>
                d.DownloadState == DownloadState.Downloading || d.DownloadState == DownloadState.Queued);

            foreach (VodDownload download in downloads)
            {
                if (download.DownloadParams.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void FireDownloadsCountChanged()
        {
            _eventAggregator.GetEvent<DownloadsCountChangedEvent>().Publish(_downloads != null ? _downloads.Count : 0);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _downloadTimer.Dispose();
                }

                _downloads = null;
                _downloadTasks = null;

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion Methods

        #region EventHandlers

        private void Downloads_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            FireDownloadsCountChanged();
        }

        #endregion EventHandlers
    }
}