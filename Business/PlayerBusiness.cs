﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DataAccess;

namespace Business {
    public class PlayerBusiness {

        #region Declarations / Constructor

        private List<Guid> playedVideos = new List<Guid>();
        private IMediaPlayerBusiness player;
        private Media nextVideo;
        private PlayerMode playMode = PlayerMode.Normal;
        private DispatcherTimer timerChangeConditions;
        private DispatcherTimer timerSession;
        private int sessionTotalSeconds;
        private int lastSearchResultCount;
        public bool IsStarted { get; private set; }
        public bool IsPaused { get; private set; }
        private DownloadBusiness downloadManager = new DownloadBusiness();
        /// <summary>
        /// Contains all filter settings.
        /// </summary>
        public SearchSettings FilterSettings { get; set; }

        /// <summary>
        /// Gets or sets the type of media to play (audio or video).
        /// </summary>
        //public MediaType MediaType { get; set; }
        /// <summary>
        /// Gets or sets whether the intensity slider is at its lowest.
        /// </summary>
        public bool IsMinimumIntensity { get; set; }
        /// <summary>
        /// Gets or sets whether to loop current video.
        /// </summary>
        public bool Loop { get; set; }

        /// <summary>
        /// Occurs before selecting a new video to get search conditions.
        /// </summary>
        public event EventHandler<GetConditionsEventArgs> GetConditions;
        /// <summary>
        /// Occurs when conditions need to be increased.
        /// </summary>
        public event EventHandler IncreaseConditions;
        /// <summary>
        /// Occurs after the playlist is updated.
        /// </summary>
        public event EventHandler PlaylistChanged;
        /// <summary>
        /// Occurs when the play time clock is updated.
        /// </summary>
        public event EventHandler DisplayPlayTime;
        /// <summary>
        /// Occurs when playing a new video.
        /// </summary>
        public event EventHandler<NowPlayingEventArgs> NowPlaying;

        /// <summary>
        /// Initializes a new instance of the PlayerBusiness class.
        /// </summary>
        public PlayerBusiness()
            : this(null) {
        }

        public PlayerBusiness(IMediaPlayerBusiness player) {
            FilterSettings = new SearchSettings();
            FilterSettings.MediaType = MediaType.Video;

            timerSession = new DispatcherTimer();
            timerSession.Interval = TimeSpan.FromSeconds(1);
            timerSession.Tick += sessionTimer_Tick;
            timerChangeConditions = new DispatcherTimer();
            timerChangeConditions.Interval = TimeSpan.FromSeconds(1);
            timerChangeConditions.Tick += timerChangeConditions_Tick;

            if (player != null)
                SetPlayer(player);
        }

        #endregion

        #region Properties

        public DownloadBusiness DownloadManager {
            get { return downloadManager; }
        }

        public Media CurrentVideo {
            get {
                if (player != null)
                    return player.CurrentVideo;
                else
                    return null;
            }
        }

        public bool IsPlaying {
            get { return player.IsPlaying; }
        }

        public int SessionTotalSeconds {
            get { return sessionTotalSeconds; }
        }

        public Media NextVideo {
            get { return nextVideo; }
        }

        public PlayerMode PlayMode {
            get { return playMode; }
            private set {
                playMode = value;
                if (PlaylistChanged != null)
                    PlaylistChanged(this, new EventArgs());
            }
        }


        public int LastSearchResultCount {
            get { return lastSearchResultCount; }
        }

        public bool IgnorePos {
            get { return player.IgnorePos; }
            set { player.IgnorePos = value; }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// When the player finishes playing a video, select the next video.
        /// </summary>
        private async void player_PlayNext(object sender, EventArgs e) {
            if (!Loop) {
                DownloadItem VideoDownload = GetNextVideoDownloading();
                if (VideoDownload == null) {
                    if (playMode == PlayerMode.Manual && nextVideo == null) {
                        if (player.CurrentVideo != null && player.CurrentVideo.EndPos.HasValue && !player.IgnorePos) // Enforce end position without moving to next video.
                            await player.SetPositionAsync(player.CurrentVideo.StartPos.HasValue ? player.CurrentVideo.StartPos.Value : 0);
                    } else {
                        // Play next video if it is not downloading.
                        if (PlayMode == PlayerMode.Normal) {
                            if (IncreaseConditions != null)
                                IncreaseConditions(this, new EventArgs());
                        }
                        await PlayNextVideoAsync();
                    }
                } else {
                    // If next video still downloading, restart current video.
                    // This method will be called again once download is completed.
                    if (player.CurrentVideo != null && player.CurrentVideo.StartPos.HasValue && !player.IgnorePos)
                        await player.SetPositionAsync(player.CurrentVideo.StartPos.Value);
                    if (PlaylistChanged != null)
                        PlaylistChanged(this, new EventArgs());
                }
            } else if (player.CurrentVideo != null && player.CurrentVideo.EndPos.HasValue && !player.IgnorePos) // Enforce end position without moving to next video.
                await player.SetPositionAsync(player.CurrentVideo.StartPos.HasValue ? player.CurrentVideo.StartPos.Value : 0);
        }

        /// <summary>
        /// When the player is playing a file, start the timer and notify the UI.
        /// </summary>
        private void player_NowPlaying(object sender, EventArgs e) {
            timerSession.Start();
            if (NowPlaying != null)
                NowPlaying(this, new NowPlayingEventArgs());
        }

        /// <summary>
        /// When the player stops, stop the timer and notify the UI.
        /// </summary>
        void player_Pause(object sender, EventArgs e) {
            timerSession.Stop();
            if (DisplayPlayTime != null)
                DisplayPlayTime(this, new EventArgs());
        }

        /// <summary>
        /// When the player resumes, start the timer and notify the UI.
        /// </summary>
        void player_Resume(object sender, EventArgs e) {
            timerSession.Start();
            if (DisplayPlayTime != null)
                DisplayPlayTime(this, new EventArgs());
        }

        /// <summary>
        /// When the play timer is updated, notify the UI.
        /// </summary>
        void sessionTimer_Tick(object sender, EventArgs e) {
            sessionTotalSeconds++;
            if (DisplayPlayTime != null)
                DisplayPlayTime(this, new EventArgs());
        }

        /// <summary>
        /// After conditions were changed, ensure the next video still matches conditions.
        /// </summary>
        private async void timerChangeConditions_Tick(object sender, EventArgs e) {
            timerChangeConditions.Stop();
            if (PlayMode == PlayerMode.Water && !IsMinimumIntensity)
                await SetWaterVideosAsync(false);
            else if (IsMinimumIntensity && !IsSpecialMode())
                await SetWaterVideosAsync(true);
            else
                await EnsureNextVideoMatchesConditionsAsync(false);
        }

        public bool IsSpecialMode() {
            return playMode == PlayerMode.WarmPause || playMode == PlayerMode.RequestCategory;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Starts the video player.
        /// </summary>
        public async Task RunPlayerAsync(string fileName) {
            player.AllowClose = false;
            player.Show();
            IsStarted = true;
            bool IsDownloaded = false;
            if (fileName != null)
                await SetNextVideoFileAsync(PlayerMode.Manual, fileName);
            else
                IsDownloaded = await SelectNextVideoAsync(0);
            if (!IsDownloaded) {
                await Task.Delay(1000);
                await PlayNextVideoAsync();
            }
        }

        /// <summary>
        /// Closes the video player.
        /// </summary>
        public void ClosePlayer() {
            if (player != null)
                player.Close();
            MpcConfigBusiness.ClearLog();
        }

        public void PauseSession() {
            IsPaused = true;
            player.AllowClose = true;
            player.Hide();
        }

        public void ResumeSession() {
            IsPaused = false;
            player.AllowClose = false;
            player.Show();
        }

        /// <summary>
        /// Manually sets the next video in queue.
        /// </summary>
        /// <param name="videoId">The ID of the video play next.</param>
        public async Task SetNextVideoIdAsync(PlayerMode mode, Guid videoId) {
            Media Result = PlayerAccess.GetVideoById(videoId);
            if (Result != null)
                await SetNextVideoAsync(mode, Result);
        }

        /// <summary>
        /// Manually sets the next video in queue.
        /// </summary>
        /// <param name="fileName">The name of the file to play.</param>
        public async Task SetNextVideoFileAsync(PlayerMode mode, string fileName) {
            await SetNextVideoAsync(mode, GetMediaObject(fileName));
        }

        public Media GetMediaObject(string fileName) {
            Media Result = PlayerAccess.GetVideoByFileName(fileName);
            if (Result == null)
                Result = new Media() { FileName = fileName, Title = fileName };
            return Result;
        }

        private async Task SetNextVideoAsync(PlayerMode mode, Media video) {
            if (nextVideo != null)
                CancelNextDownload(nextVideo);
            nextVideo = video;
            this.PlayMode = mode;
            await PrepareNextVideoAsync(1, 0);
            if (PlaylistChanged != null)
                PlaylistChanged(this, new EventArgs());
        }

        /// <summary>
        /// Cancels the download and autoplay of specified video.
        /// </summary>
        private void CancelNextDownload(Media video) {
            DownloadItem VideoDownload = downloadManager.DownloadsList.FirstOrDefault(d => d.Request.MediaId == video.MediaId && !d.IsCompleted);
            if (VideoDownload != null) {
                // Removes autoplay from the next video.
                VideoDownload.QueuePos = -1;
                // Cancel the download if progress is less than 40%
                if (VideoDownload.ProgressValue < 40 && playMode != PlayerMode.Manual)
                    VideoDownload.Status = DownloadStatus.Canceled;
            }
        }

        /// <summary>
        /// Sets the next videos to egoless pause.
        /// </summary>
        /// <param name="enabled">True to enable this mode, false to restore normal session.</param>
        public async Task SetFunPauseAsync(bool enabled) {
            PlayMode = (enabled ? PlayerMode.WarmPause : PlayerMode.Normal);
            await SelectNextVideoAsync(0);
            // PlayNextVideo();

            if (PlaylistChanged != null)
                PlaylistChanged(this, new EventArgs());
        }

        /// <summary>
        /// Apply specified search and rating category filters to the next videos.
        /// </summary>
        /// <param name="request">The search filters being requested, or null to disable the request category mode.</param>
        public async Task SetRequestCategoryAsync(SearchSettings request) {
            if (request != null) {
                FilterSettings.Search = request.Search;
                if (!string.IsNullOrEmpty(request.RatingCategory) && request.RatingValue.HasValue) {
                    FilterSettings.RatingCategory = request.RatingCategory;
                    FilterSettings.RatingOperator = request.RatingOperator;
                    FilterSettings.RatingValue = request.RatingValue;
                }

                playMode = PlayerMode.RequestCategory;
            } else {
                FilterSettings.Search = null;
                FilterSettings.RatingCategory = null;
                FilterSettings.RatingValue = null;
                playMode = PlayerMode.Normal;
            }

            await SelectNextVideoAsync(0);

            if (PlaylistChanged != null)
                PlaylistChanged(this, new EventArgs());
        }

        /// <summary>
        /// Sets the next videos to water videos for cool down.
        /// </summary>
        /// <param name="enabled">True to enable this mode, false to restore normal session.</param>
        public async Task SetWaterVideosAsync(bool enabled) {
            PlayMode = (enabled ? PlayerMode.Water : PlayerMode.Normal);
            await SelectNextVideoAsync(1);

            if (PlaylistChanged != null)
                PlaylistChanged(this, new EventArgs());
        }

        /// <summary>
        /// Plays the next video.
        /// </summary>
        private async Task PlayNextVideoAsync() {
            if (nextVideo == null)
                return;

            // Enable/Disable SVP if necessary.
            MpcConfigBusiness.AutoConfigure(nextVideo);

            // If next video is still downloading, advance QueuePos. If QueuePos = 0 when download finishes, it will auto-play.
            DownloadItem VideoDownload = GetNextVideoDownloading();
            if (VideoDownload != null) {
                if (VideoDownload.QueuePos > 0)
                    VideoDownload.QueuePos--;
                return;
            }

            await player.PlayVideoAsync(nextVideo);
            playedVideos.Add(nextVideo.MediaId);
            nextVideo = null;

            if (PlayMode == PlayerMode.SpecialRequest)
                PlayMode = PlayerMode.Normal;

            if (playMode != PlayerMode.Manual)
                await SelectNextVideoAsync(1);

            if (PlayMode == PlayerMode.Fire)
                PlayMode = PlayerMode.SpecialRequest;

            if (PlaylistChanged != null)
                PlaylistChanged(this, new EventArgs());
        }

        /// <summary>
        /// Selects which video will be played next.
        /// </summary>
        /// <param name="queuePos">The video position to select. 0 for current, 1 for next.</param>
        /// <returns>Whether the file is downloading.</returns>
        private async Task<bool> SelectNextVideoAsync(int queuePos) {
            return await SelectNextVideoAsync(queuePos, 0);
        }

        /// <summary>
        /// Selects which video will be played next.
        /// </summary>
        /// <param name="queuePos">The video position to select. 0 for current, 1 for next.</param>
        /// <param name="attempts">The number of attemps already made, to avoid infinite loop.</param>
        /// <returns>Whether the file is downloading.</returns>
        private async Task<bool> SelectNextVideoAsync(int queuePos, int attempts) {
            bool IsDownloading = false;
            if (attempts > 3) {
                nextVideo = null;
                return false;
            }

            timerChangeConditions.Stop();

            // Get video conditions
            GetConditionsEventArgs e = new GetConditionsEventArgs(FilterSettings);
            e.QueuePos = queuePos;
            if (GetConditions != null)
                GetConditions(this, e);

            // Select random video matching conditions.
            Media Result = PlayerAccess.SelectVideo(FilterSettings.Update(playedVideos, Settings.SavedFile.AutoDownload));
            lastSearchResultCount = FilterSettings.TotalFound;

            // If no video is found, try again while increasing tolerance
            if (Result == null) {
                e = new GetConditionsEventArgs(FilterSettings);
                e.IncreaseTolerance = true;
                if (GetConditions != null)
                    GetConditions(this, e);
                Result = PlayerAccess.SelectVideo(FilterSettings.Update(null, Settings.SavedFile.AutoDownload));
                FilterSettings.TotalFound = lastSearchResultCount;
            }

            if (Result != null) {
                if (nextVideo != null)
                    CancelNextDownload(nextVideo);
                nextVideo = Result;
                IsDownloading = await PrepareNextVideoAsync(queuePos, attempts);
            }

            timerChangeConditions.Stop();
            if (PlaylistChanged != null)
                PlaylistChanged(this, new EventArgs());

            return IsDownloading;
        }

        /// <summary>
        /// Prepares for playing the next video.
        /// </summary>
        /// <param name="queuePos">The video position to select. 0 for current, 1 for next.</param>
        /// <param name="attempts">The number of attemps already made, to avoid infinite loop.</param>
        /// <returns>Whether the file is downloading.</returns>
        private async Task<bool> PrepareNextVideoAsync(int queuePos, int attempts) {
            bool FileExists = false;
            if (nextVideo != null)
                FileExists = File.Exists(Settings.NaturalGroundingFolder + nextVideo.FileName);

            if (!FileExists) {
                // If file doesn't exist and can't be downloaded, select another one.
                if (!Settings.SavedFile.AutoDownload || nextVideo == null || nextVideo.DownloadUrl.Length == 0)
                    await SelectNextVideoAsync(queuePos, attempts + 1);
                // If file doesn't exist and can be downloaded, download it.
                else if (nextVideo != null && nextVideo.DownloadUrl.Length > 0) {
                    if (PlaylistChanged != null)
                        PlaylistChanged(this, new EventArgs());
                    await downloadManager.DownloadVideoAsync(nextVideo, queuePos, Download_Complete);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Occurs when download is completed.
        /// </summary>
        private async void Download_Complete(object sender, DownloadCompletedEventArgs args) {
            if (args.DownloadInfo.IsCompleted && (args.DownloadInfo.QueuePos == 0 || player.CurrentVideo == null) && !player.AllowClose) {
                nextVideo = args.DownloadInfo.Request;
                player_PlayNext(null, null);
            } else if (args.DownloadInfo.IsCanceled && args.DownloadInfo.QueuePos > -1 && playMode != PlayerMode.Manual) {
                nextVideo = null;
                await SelectNextVideoAsync(args.DownloadInfo.QueuePos);
            }
        }

        public void ChangeConditions() {
            if (player != null && player.CurrentVideo != null) {
                timerChangeConditions.Stop();
                timerChangeConditions.Start();
            }
        }

        public async Task SkipVideoAsync() {
            if (!player.IsAvailable)
                return;

            if (playedVideos.Count > 0)
                playedVideos.RemoveAt(playedVideos.Count - 1);
            await EnsureNextVideoMatchesConditionsAsync(true);
            await PlayNextVideoAsync();
        }

        public async Task ReplayLastAsync() {
            if (!player.IsAvailable)
                return;

            if (playedVideos.Count > 1) {
                Media LastVideo = PlayerAccess.GetVideoById(playedVideos[playedVideos.Count - 2]);
                if (LastVideo.MediaId != player.CurrentVideo.MediaId) {
                    playedVideos.RemoveAt(playedVideos.Count - 1);
                    if (nextVideo != null)
                        CancelNextDownload(nextVideo);
                    nextVideo = player.CurrentVideo;

                    // Enable/Disable SVP if necessary.
                    MpcConfigBusiness.AutoConfigure(nextVideo);

                    await player.PlayVideoAsync(LastVideo);
                    if (PlaylistChanged != null)
                        PlaylistChanged(this, new EventArgs());
                }
            }
        }

        /// <summary>
        /// Enable/Disable SVP and madVR if necessary.
        /// </summary>
        public void ConfigurePlayer() {
            if (Settings.SavedFile.MediaPlayerApp == MediaPlayerApplication.Mpc) {
                if (CurrentVideo != null)
                    MpcConfigBusiness.AutoConfigure(CurrentVideo);
                else
                    MpcConfigBusiness.AutoConfigure(null);
            }
        }

        public async Task EnsureNextVideoMatchesConditionsAsync(bool skipping) {
            int QueuePos = skipping ? 0 : 1;
            if (nextVideo != null && PlayMode == PlayerMode.Normal) {
                // Get new conditions
                GetConditionsEventArgs Args = new GetConditionsEventArgs(FilterSettings);
                Args.QueuePos = QueuePos;
                if (GetConditions != null)
                    GetConditions(this, Args);

                // Run the conditions on the next video in the playlist
                if (!PlayerAccess.VideoMatchesConditions(nextVideo, FilterSettings.Update(playedVideos, Settings.SavedFile.AutoDownload))) {
                    // If next video doesn't match conditions, select another one
                    await SelectNextVideoAsync(QueuePos);
                }
            } else if (nextVideo == null)
                await SelectNextVideoAsync(QueuePos);
        }

        public void ReloadVideoInfo() {
            if (player.CurrentVideo != null) {
                player.CurrentVideo = PlayerAccess.GetVideoById(player.CurrentVideo.MediaId);
                if (NowPlaying != null)
                    NowPlaying(this, new NowPlayingEventArgs(true));
            }
        }

        /// <summary>
        /// Loads the list of rating categories for the Focus combobox.
        /// </summary>
        /// <returns>A list of RatingCategory objects.</returns>
        public async Task<List<RatingCategory>> GetFocusCategoriesAsync() {
            List<RatingCategory> Result = new List<RatingCategory>();
            List<RatingCategory> DbResult = await Task.Run(() => SearchVideoAccess.GetCustomRatingCategories());
            Result.Add(new RatingCategory() { Name = "Intensity" });
            Result.Add(new RatingCategory() { Name = "Physical" });
            Result.Add(new RatingCategory() { Name = "Emotional" });
            Result.Add(new RatingCategory() { Name = "Spiritual" });
            Result.Add(new RatingCategory() { Name = "Egoless" });
            Result.Add(new RatingCategory() { Name = "Love" });
            Result.AddRange(DbResult);
            return Result;
        }

        /// <summary>
        /// If the next video is still downloading, returns its download information.
        /// </summary>
        /// <returns>An object containing the information about the download.</returns>
        public DownloadItem GetNextVideoDownloading() {
            if (nextVideo != null) {
                DownloadItem VideoDownload = downloadManager.DownloadsList.FirstOrDefault(d => d.Request.MediaId == nextVideo.MediaId);
                if (VideoDownload != null && !VideoDownload.IsCompleted)
                    return VideoDownload;
            }
            return null;
        }

        /// <summary>
        /// Uses specified player business object to play the session.
        /// </summary>
        /// <param name="player">The player business object through which to play the session.</param>
        public void SetPlayer(IMediaPlayerBusiness player) {
            player.SetPath();
            if (this.player != null) {
                player.AllowClose = this.player.AllowClose;
                if (this.player.CurrentVideo != null)
                    player.CurrentVideo = this.player.CurrentVideo;
            }

            // If changing player, close the previous one.
            if (this.player != null)
                this.player.Close();

            this.player = player;
            player.PlayNext += player_PlayNext;
            player.NowPlaying += player_NowPlaying;
            player.Pause += player_Pause;
            player.Resume += player_Resume;

            if (player.CurrentVideo != null)
                player.PlayVideoAsync(player.CurrentVideo);
        }

        public string GetVideoDisplayTitle(Media video) {
            if (video == null)
                return "None";
            else if (video.Artist.Length == 0)
                return video.Title;
            else
                return string.Format("{0} - {1}", video.Artist, video.Title);
        }

        public void ResetPlayerMode() {
            playMode = PlayerMode.Normal;
        }

        #endregion
    }

    #region GetConditionsEventHandler

    /// <summary>
    /// Contains information for the GetConditions event. The event handler should fill Conditions with search conditions.
    /// </summary>
    public class GetConditionsEventArgs : EventArgs {
        /// <summary>
        /// Gets or sets whether to widen the range of criterias, if none were previously found.
        /// </summary>
        public bool IncreaseTolerance { get; set; }
        /// <summary>
        /// Gets or sets the position in the playlist pos to fill.
        /// </summary>
        public int QueuePos { get; set; }
        /// <summary>
        /// Gets or sets the conditions to use for searching the next video.
        /// </summary>
        public SearchSettings Conditions { get; set; }

        /// <summary>
        /// Initializes a new instance of the GetConditionsEventArgs class.
        /// </summary>
        public GetConditionsEventArgs() {
        }

        public GetConditionsEventArgs(SearchSettings conditions) {
            this.Conditions = conditions;
            this.Conditions.RatingFilters = new List<SearchRatingSetting>();
            this.Conditions.RatingFilters.Add(new SearchRatingSetting());
        }
    }

    public class NowPlayingEventArgs : EventArgs {
        /// <summary>
        /// Gets or sets whether the media info was edited and must be reloaded.
        /// </summary>
        public bool ReloadInfo { get; set; }

        /// <summary>
        /// Initializes a new instance of the NowPlayingEventArgs class.
        /// </summary>
        public NowPlayingEventArgs() {
        }

        /// <summary>
        /// Initializes a new instance of the NowPlayingEventArgs class.
        /// </summary>
        /// <param name="reloadInfo">Whether data was edited and must be reloaded.</param>
        public NowPlayingEventArgs(bool reloadInfo) {
            this.ReloadInfo = reloadInfo;
        }
    }

    #endregion

    #region PlayerMode

    public enum PlayerMode {
        Normal,
        SpecialRequest,
        RequestCategory,
        WarmPause,
        Fire,
        Water,
        Manual
    }

    #endregion
}