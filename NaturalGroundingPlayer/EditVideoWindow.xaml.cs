﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Collections.Generic;
using Business;
using DataAccess;
using YoutubeExtractor;
using Microsoft.Win32;

namespace NaturalGroundingPlayer {
    public delegate void ClosingCallback(Media result);

    /// <summary>
    /// Interaction logic for EditVideoWindow.xaml
    /// </summary>
    public partial class EditVideoWindow : Window {
        /// <summary>
        /// Displays a window to edit specified video.
        /// </summary>
        public static EditVideoWindow Instance(Guid? videoId, string fileName, IMediaPlayerBusiness player, ClosingCallback callback) {
            EditVideoWindow NewForm = new EditVideoWindow();
            NewForm.editMode = EditVideoWindowMode.Edit;
            if (videoId != null && videoId != Guid.Empty)
                NewForm.videoId = videoId;
            else
                NewForm.fileName = fileName;
            NewForm.player = player;
            NewForm.callback = callback;
            SessionCore.Instance.Windows.Show(NewForm);
            return NewForm;
        }

        /// <summary>
        /// Displays a window to add a new download.
        /// </summary>
        public static EditVideoWindow InstanceAddDownload(ClosingCallback callback) {
            EditVideoWindow NewForm = new EditVideoWindow();
            NewForm.editMode = EditVideoWindowMode.AddDownload;
            NewForm.callback = callback;
            SessionCore.Instance.Windows.Show(NewForm);
            return NewForm;
        }

        /// <summary>
        /// Displays a popup containing the FileBinding menu features.
        /// </summary>
        public static EditVideoWindow InstancePopup(UIElement target, PlacementMode placement, Guid? videoId, string fileName, IMediaPlayerBusiness player, ClosingCallback callback) {
            EditVideoWindow NewForm = new EditVideoWindow();
            NewForm.editMode = EditVideoWindowMode.Popup;
            NewForm.videoId = videoId;
            if (videoId != null && videoId != Guid.Empty)
                NewForm.videoId = videoId;
            else
                NewForm.fileName = fileName;
            NewForm.player = player;
            NewForm.callback = callback;
            WindowHelper.SetScale(NewForm.FileBindingButton.ContextMenu);
            NewForm.Window_Loaded(null, null);
            NewForm.ShowFileBindingMenu(target, placement);
            return NewForm;
        }

        protected IMediaPlayerBusiness player;
        protected Guid? videoId;
        protected string fileName;
        protected ClosingCallback callback;
        protected bool isFormSaved = false;
        private EditVideoBusiness business = new EditVideoBusiness();
        private EditRatingsBusiness ratingBusiness;
        private Media video;
        private bool fileNotFound;
        private bool downloaded;
        private bool isNew;
        private bool isUrlValid;
        private EditVideoWindowMode editMode;
        private WindowHelper helper;

        public EditVideoWindow() {
            InitializeComponent();
            helper = new WindowHelper(this);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e) {
            if (editMode == EditVideoWindowMode.AddDownload) {
                // Add Download
                this.Title = "Add Download";
                SaveButton.Content = "_Download";
                FileBindingButton.IsEnabled = false;
                video = business.NewVideo();
                isNew = true;
            } else {
                // Edit or Popup
                if (videoId != null)
                    video = business.GetVideoById(videoId.Value);
                else {
                    video = business.GetVideoByFileName(fileName);
                    if (video == null) {
                        video = business.NewVideo();
                        video.FileName = fileName;
                        video.MediaTypeId = (int)EditVideoBusiness.GetFileType(fileName);
                        video.DownloadName = System.IO.Path.GetFileNameWithoutExtension(fileName);
                        isNew = true;
                    }
                }
            }
            this.DataContext = video;
            CategoryCombo.ItemsSource = business.GetCategories(video.MediaTypeId);
            Custom1Combo.ItemsSource = business.GetCustomRatingCategories();
            Custom2Combo.ItemsSource = Custom1Combo.ItemsSource;
            ratingBusiness = business.GetRatings(video);
            RatingsGrid.DataContext = ratingBusiness;
            EditRating_LostFocus(null, null);
            if (player == null)
                PlayButton.Visibility = System.Windows.Visibility.Hidden;
            if (video.FileName != null && !File.Exists(Settings.NaturalGroundingFolder + video.FileName)) {
                fileNotFound = true;
                ErrorText.Text = "File not found.";
            }

            if (editMode == EditVideoWindowMode.Edit && MediaInfoReader.HasMissingInfo(video))
                await LoadMediaInfoAsync();
        }

        private async Task LoadMediaInfoAsync() {
            MediaInfoReader MediaInfo = new MediaInfoReader();
            await MediaInfo.LoadInfoAsync(video);
            MediaInfo.Dispose();
            DimensionText.GetBindingExpression(TextBox.TextProperty).UpdateTarget();
        }

        private async void DownloadUrlText_LostFocus(object sender, RoutedEventArgs e) {
            isUrlValid = false;
            ErrorText.Text = "";
            if (video.DownloadUrl.Length > 0) {
                try {
                    var VTask = DownloadBusiness.GetDownloadUrlsAsync(video.DownloadUrl);
                    var VideoList = await VTask;
                    VideoInfo FirstVid = VideoList.FirstOrDefault();
                    if (FirstVid != null) {
                        video.DownloadName = FirstVid.Title;
                        isUrlValid = true;
                    }
                }
                catch { }
                if (!isUrlValid)
                    ErrorText.Text = "Please enter a valid URL";
            }
        }

        private void YouTubeSearchButton_Click(object sender, RoutedEventArgs e) {
            if (DownloadNameText.Text.Length > 0) {
                string SearchQuery = Uri.EscapeDataString(DownloadNameText.Text);
                Process.Start("https://www.youtube.com/results?search_query=" + SearchQuery);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) {
            this.Close();
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e) {
            if (SaveChanges()) {
                this.Close();

                // Start download after closing.
                if (editMode == EditVideoWindowMode.AddDownload)
                    await menuDownloadVideo_ClickAsync();
            }
        }

        private bool SaveChanges() {
            SaveButton.Focus();
            video.Artist = video.Artist.Trim();
            video.Album = video.Album.Trim();
            video.Title = video.Title.Trim();
            video.DownloadName = video.DownloadName.Trim();
            video.DownloadUrl = video.DownloadUrl.Trim();
            video.BuyUrl = video.BuyUrl.Trim();

            ErrorText.Text = "";
            if (TitleText.Text.Length == 0) {
                ErrorText.Text = "Title is required.";
                return false;
            }
            if (business.IsTitleDuplicate(video)) {
                ErrorText.Text = "Artist and title already exist in the database.";
                return false;
            }

            if (editMode == EditVideoWindowMode.AddDownload) {
                if (!isUrlValid) {
                    ErrorText.Text = "Please enter a valid URL";
                    return false;
                }

                if (SessionCore.Instance.Business.DownloadManager.IsDownloadDuplicate(video)) {
                    ErrorText.Text = "You are already downloading this video.";
                    return false;
                }
            }

            // Only update EditedOn when directly editing from the Edit window.
            if (editMode != EditVideoWindowMode.Popup)
                video.EditedOn = DateTime.UtcNow;

            ratingBusiness.UpdateChanges();
            business.Save();
            isFormSaved = true;

            // Update grid when in popup mode. Otherwise Callback is called in Window_Closing.
            if (editMode == EditVideoWindowMode.Popup)
                callback(video);

            return true;
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e) {
            if (player != null && video.FileName != null)
                await player.PlayVideoAsync(video);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
            //if (editMode != EditVideoWindowMode.Popup)
            //    Owner.Show();
            if (callback != null) {
                if (isFormSaved)
                    callback(video);
                else
                    callback(null);
            }
        }

        #region File Binding Menu

        private void FileBindingButton_Click(object sender, RoutedEventArgs e) {
            ShowFileBindingMenu(FileBindingButton, PlacementMode.Bottom);
        }

        public void ShowFileBindingMenu(UIElement target, PlacementMode placement) {
            // Set context menu items visibility.
            if (player == null)
                FileBindingButton.ContextMenu.Items.Remove(menuPlay);
            else 
                menuPlay.IsEnabled = (!fileNotFound && video.FileName != null);
            if (editMode != EditVideoWindowMode.Popup)
                FileBindingButton.ContextMenu.Items.Remove(menuEdit);
            string DefaultFileName = GetDefaultFileName();
            menuMoveFile.IsEnabled = (video.Title.Length > 0 && video.FileName != null && video.FileName != DefaultFileName);
            if (menuMoveFile.IsEnabled)
                menuMoveFile.Header = string.Format("_Move to \"{0}\"", DefaultFileName);
            else
                menuMoveFile.Header = "_Move to Default Location";
            if (isNew)
                menuSelectFile.Header = "_Select Existing Entry...";
            else
                menuSelectFile.Header = "_Select Another File...";
            menuDownloadVideo.IsEnabled = (!downloaded && (fileNotFound || video.FileName == null) && video.DownloadUrl.Length > 0);
            menuExtractAudio.IsEnabled = (!fileNotFound && video.FileName != null);
            menuRemoveBinding.IsEnabled = (!isNew && video.FileName != null);
            menuDeleteVideo.IsEnabled = (!fileNotFound && video.FileName != null);
            if (isNew)
                menuDeleteVideo.Header = "Delete _File";
            else
                menuDeleteVideo.Header = "Delete Attached _File";
            menuDeleteEntry.IsEnabled = !isNew;

            // Show context menu.
            FileBindingButton.ContextMenu.IsEnabled = true;
            FileBindingButton.ContextMenu.PlacementTarget = target;
            FileBindingButton.ContextMenu.Placement = placement;
            FileBindingButton.ContextMenu.IsOpen = true;
        }

        private void menuEdit_Click(object sender, RoutedEventArgs e) {
            EditVideoWindow.Instance(null, video.FileName, player, callback);
        }

        private void menuMoveFile_Click(object sender, RoutedEventArgs e) {
            MoveFilesBusiness MoveBusiness = new MoveFilesBusiness();
            string DefaultFileName = GetDefaultFileName();
            if (MoveBusiness.MoveFile(video, DefaultFileName)) {
                video.FileName = DefaultFileName;
                SaveChanges();
                fileNotFound = false;
            }
        }

        private string GetDefaultFileName() {
            DefaultMediaPath PathCalc = new DefaultMediaPath();
            string Result = PathCalc.GetDefaultFileName(video.Artist, video.Title, video.MediaCategoryId, (MediaType)video.MediaTypeId) + Path.GetExtension(video.FileName);
            return Result;
        }

        private async void menuSelectFile_Click(object sender, RoutedEventArgs e) {
            if (!isNew) {
                // Bind to another file.
                Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
                dlg.InitialDirectory = Settings.NaturalGroundingFolder;
                if (video.MediaType == MediaType.Video)
                    string.Format("Video Files|*{0})", string.Join(";*", Settings.VideoExtensions));
                else if (video.MediaType == MediaType.Audio)
                    string.Format("Audio Files|*{0})", string.Join(";*", Settings.AudioExtensions));
                else if (video.MediaType == MediaType.Image)
                    string.Format("Image Files|*{0})", string.Join(";*", Settings.ImageExtensions));
                if (dlg.ShowDialog(IsLoaded ? this : Owner).Value == true) {
                    if (!dlg.FileName.StartsWith(Settings.NaturalGroundingFolder))
                        MessageBox.Show("You must select a file within your Natural Grounding folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Information);
                    else {
                        string BindFile = dlg.FileName.Substring(Settings.NaturalGroundingFolder.Length);
                        if (business.GetVideoByFileName(BindFile) == null) {
                            video.FileName = BindFile;
                            video.Length = null;
                            video.Height = null;
                            await LoadMediaInfoAsync();
                            if (editMode == EditVideoWindowMode.Popup)
                                SaveChanges();
                        } else
                            MessageBox.Show("This file is already in the database.", "Error", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    fileNotFound = false;
                }
            } else {
                // Bind file to an existing entry.
                SearchSettings settings = new SearchSettings() { 
                    MediaType = (MediaType)video.MediaTypeId,
                    ConditionField = FieldConditionEnum.FileExists,
                    ConditionValue = BoolConditionEnum.No
                };
                VideoListItem Result = SearchVideoWindow.Instance(settings, false);
                if (Result != null) {
                    // Close and re-open selected entry.
                    Close();
                    EditVideoWindow NewForm = Instance(Result.MediaId, null, player, callback);
                    NewForm.video.FileName = video.FileName;
                    NewForm.video.Length = null;
                    NewForm.video.Height = null;
                    await NewForm.LoadMediaInfoAsync();
                }
            }
        }

        private async void menuDownloadVideo_Click(object sender, RoutedEventArgs e) {
            await menuDownloadVideo_ClickAsync();
        }

        private async Task menuDownloadVideo_ClickAsync() {
            await SessionCore.Instance.Business.DownloadManager.DownloadVideoAsync(video, -1, null);
            downloaded = true;
        }

        private async void menuExtractAudio_Click(object sender, RoutedEventArgs e) {
            if (video.FileName != null && File.Exists(Settings.NaturalGroundingFolder + video.FileName)) {
                MediaInfoReader MInfo = new MediaInfoReader();
                await MInfo.LoadInfoAsync(Settings.NaturalGroundingFolder + video.FileName);
                string Ext = null;
                if (MInfo.AudioFormat == "MPEG Audio")
                    Ext = ".mp2";
                else if (MInfo.AudioFormat == "PCM")
                    Ext = ".wav";
                else
                    Ext = ".aac";

                SaveFileDialog SaveDlg = new SaveFileDialog();
                SaveDlg.InitialDirectory = Settings.NaturalGroundingFolder + "Audios";
                SaveDlg.OverwritePrompt = true;
                SaveDlg.DefaultExt = ".mp3";
                SaveDlg.Filter = string.Format("Audio Files|*{0})", Ext); ;
                SaveDlg.FileName = Path.GetFileNameWithoutExtension(video.FileName) + Ext;

                if (SaveDlg.ShowDialog() == true) {
                    FfmpegBusiness.ExtractAudio(Settings.NaturalGroundingFolder + video.FileName, SaveDlg.FileName);
                }
            }
        }

        private void menuRemoveBinding_Click(object sender, RoutedEventArgs e) {
            video.FileName = null;
            video.Length = null;
            video.Height = null;
            if (editMode == EditVideoWindowMode.Popup)
                SaveChanges();
        }

        private void menuDeleteVideo_Click(object sender, RoutedEventArgs e) {
            if (MessageBox.Show("Are you sure you want to delete this video file?" + Environment.NewLine + video.FileName, "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) {
                try {
                    business.DeleteFile(video.FileName);
                    video.FileName = null;
                    video.Length = null;
                    video.Height = null;
                    bool IsSaved = SaveChanges();
                    // If adding new record and deleting the file and can't save, we must close form
                    // to avoid duplicate record if downloading another video file.
                    if (isNew && !IsSaved) {
                        video.MediaId = Guid.Empty;
                        isFormSaved = true;
                        this.Close(); // A non-loaded window can still be closed...
                    }
                }
                catch (Exception ex) {
                    string Msg = "The file cannot be deleted:\r\n" + ex.Message;
                    MessageBox.Show(Msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void menuDeleteEntry_Click(object sender, RoutedEventArgs e) {
            string Title;
            if (video.Artist.Length > 0)
                Title = string.Format("{0} - {1}", video.Artist, video.Title);
            else
                Title = video.Title;
            if (MessageBox.Show("Are you sure you want to delete this database entry?\r\nThe file will not be deleted.\r\n" + Title, "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) {
                business.Delete(video);
                isFormSaved = true;
                this.Close();
            }
        }

        #endregion

        private void CustomCombo_LostFocus(object sender, KeyboardFocusChangedEventArgs e) {
            ComboBox Obj = sender as ComboBox;
            RatingCategory ObjItem = Obj.SelectedItem as RatingCategory;
            if (ObjItem != null)
                Obj.Text = ObjItem.Name;
            else
                Obj.Text = "";

            if (Obj.Text.Length > 0 && Custom1Combo.Text == Custom2Combo.Text)
                Obj.Text = "";
        }

        private void CategoryCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) {
            ComboBox Obj = sender as ComboBox;
            MediaCategory ObjItem = Obj.SelectedItem as MediaCategory;
            if (ObjItem != null)
                Obj.Text = ObjItem.Name;
            else
                Obj.Text = "";
        }

        private void EditRating_LostFocus(object sender, RoutedEventArgs e) {
            if (sender != null)
                (sender as TextBox).GetBindingExpression(TextBox.TextProperty).UpdateSource();
            RatingViewerControl.DisplayValue(PMValueText, ratingBusiness.PM, 0);
            RatingViewerControl.DisplayValue(PFValueText, ratingBusiness.PF, 0);
            RatingViewerControl.DisplayValue(EMValueText, ratingBusiness.EM, 0);
            RatingViewerControl.DisplayValue(EFValueText, ratingBusiness.EF, 0);
            RatingViewerControl.DisplayValue(SMValueText, ratingBusiness.SM, 0);
            RatingViewerControl.DisplayValue(SFValueText, ratingBusiness.SF, 0);
            RatingViewerControl.DisplayValue(LoveValueText, ratingBusiness.Love, 0);
            RatingViewerControl.DisplayValue(EgolessValueText, ratingBusiness.Egoless, 0);
            RatingViewerControl.DisplayValue(Custom1ValueText, ratingBusiness.Custom1, 0);
            RatingViewerControl.DisplayValue(Custom2ValueText, ratingBusiness.Custom2, 0);
        }
    }

    /// <summary>
    /// Represents the mode in which to display the editor.
    /// </summary>
    public enum EditVideoWindowMode {
        Edit,
        AddDownload,
        Popup
    }
}