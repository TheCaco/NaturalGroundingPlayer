﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Business;
using DataAccess;

namespace NaturalGroundingPlayer {
    /// <summary>
    /// Interaction logic for MediaEncoderWindow.xaml
    /// </summary>
    public partial class MediaEncoderWindow : Window {
        public static void Instance(Action callback) {
            MediaEncoderWindow NewForm = new MediaEncoderWindow();
            NewForm.callback = callback;
            SessionCore.Instance.Windows.Show(NewForm);
        }

        protected Action callback;
        private WindowHelper helper;
        private WmpPlayerWindow playerOriginal = new WmpPlayerWindow();
        private WmpPlayerWindow playerChanges = new WmpPlayerWindow();
        private MpcPlayerBusiness playerMpc = new MpcPlayerBusiness();
        private MediaEncoderSettings encodeSettings = new MediaEncoderSettings();
        private MediaEncoderBusiness business = new MediaEncoderBusiness();

        public MediaEncoderWindow() {
            InitializeComponent();
            helper = new WindowHelper(this);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e) {
            this.DataContext = encodeSettings;
            playerOriginal.Title = "Original";
            playerOriginal.WindowState = WindowState.Maximized;
            playerChanges.Title = "Preview Changes";
            playerChanges.WindowState = WindowState.Maximized;
            playerChanges.Player.Player.PositionChanged += Player_PositionChanged;
            playerMpc.SetPath();
            if (Settings.SavedFile.MediaPlayerApp != MediaPlayerApplication.Mpc)
                PreviewMpcButton.Visibility = Visibility.Hidden;
            business.EncodingCompleted += business_EncodingCompleted;
            ProcessingQueueList.ItemsSource = MediaEncoderBusiness.ProcessingQueue;
            MpcConfigBusiness.IsSvpEnabled = false;

            MediaEncoderSettings RecoverSettings = await business.AutoLoadPreviewFileAsync();
            if (RecoverSettings != null)
                SetEncodeSettings(RecoverSettings);
            // Only recover jobs if there are no jobs running
            if (!MediaEncoderBusiness.ProcessingQueue.Any())
                await business.AutoLoadJobsAsync();
        }

        /// <summary>
        /// When position changes in PreviewChanges and both players are on pause, the Original will go to the same position.
        /// </summary>
        private void Player_PositionChanged(object sender, EventArgs e) {
            MediaPlayer.WindowsMediaPlayer Player1 = playerChanges.Player.Player;
            MediaPlayer.WindowsMediaPlayer Player2 = playerOriginal.Player.Player;
            if (Player1 != null && Player2 != null && playerOriginal.IsVisible && playerChanges.IsVisible && !Player1.IsPlaying && !Player2.IsPlaying)
                Player2.SetFramePosition(encodeSettings.TrimStart ?? 0 + Player1.Position + .195);
        }

        private void business_EncodingCompleted(object sender, EncodingCompletedEventArgs e) {
            MediaEncodingCompletedWindow.Instance(e);
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
            ClosePreview();
            await business.DeletePreviewFilesAsync();
            if (callback != null)
                callback();
        }

        private void Window_Activated(object sender, EventArgs e) {
            if (playerChanges.Player.Position > 0)
                encodeSettings.Position = playerChanges.Player.Position;
        }

        private async void SelectVideoButton_Click(object sender, RoutedEventArgs e) {
            VideoListItem Result = SearchVideoWindow.Instance(new SearchSettings() { 
                MediaType = MediaType.Video, 
                ConditionField = FieldConditionEnum.FileExists, 
                ConditionValue = BoolConditionEnum.Yes,
                RatingCategory = "Height",
                RatingOperator = OperatorConditionEnum.Smaller}, false);
            if (Result != null && Result.FileName != null) {
                ClosePreview();
                encodeSettings.FileName = Result.FileName;

                try {
                    await business.ConvertToAvi(Result.FileName, encodeSettings);
                    encodeSettings.Crop = false;
                    encodeSettings.CropLeft = 0;
                    encodeSettings.CropTop = 0;
                    encodeSettings.CropRight = 0;
                    encodeSettings.CropBottom = 0;
                    PresetCombo_SelectionChanged(null, null);
                }
                catch (Exception ex) {
                    MessageBox.Show(this, ex.Message, "Cannot Open File", MessageBoxButton.OK, MessageBoxImage.Error);
                    encodeSettings.FileName = "";
                }
            }
        }

        public void SetEncodeSettings(MediaEncoderSettings value) {
            encodeSettings = value;
            this.DataContext = value;
        }

        public void ClosePreview() {
            if (playerOriginal.Visibility == Visibility.Visible)
                playerOriginal.Close();
            if (playerChanges.Visibility == Visibility.Visible)
                playerChanges.Close();
            playerMpc.Close();
        }

        private async void PreviewOriginalButton_Click(object sender, RoutedEventArgs e) {
            await PlayVideoAsync(playerOriginal, Settings.NaturalGroundingFolder + encodeSettings.FileName);
        }

        private async void PreviewChangesButton_Click(object sender, RoutedEventArgs e) {
            business.GenerateScript(encodeSettings, true, false);
            await PlayVideoAsync(playerChanges, Settings.TempFilesPath + "Preview.avs");
        }

        private async void PreviewMpcButton_Click(object sender, RoutedEventArgs e) {
            business.GenerateScript(encodeSettings, false, false);
            await playerMpc.PlayVideoAsync(Settings.TempFilesPath + "Preview.avs");
        }

        private async Task PlayVideoAsync(WmpPlayerWindow playerWindow, string fileName) {
            playerWindow.Show();
            playerWindow.Activate();
            await playerWindow.Player.OpenFileAsync(fileName);
            playerWindow.Player.MediaOpened += (s2, e2) => {
                if (encodeSettings.Position.HasValue)
                    playerWindow.Player.Position = encodeSettings.Position.Value;
                playerWindow.Player.Player.Pause();
            };
        }

        private async void EncodeButton_Click(object sender, RoutedEventArgs e) {
            if (!Validate()) {
                MessageBox.Show(this, "You must enter required file information.", "Validation Error");
                return;
            }

            MediaEncoderSettings EncodeSettings = encodeSettings;
            try {
                ClosePreview();
                SetEncodeSettings((MediaEncoderSettings)encodeSettings.Clone());
                encodeSettings.FileName = "";
                await Task.Delay(100); // Wait for media player file to be released.
                await business.EncodeFileAsync(EncodeSettings);
            }
            catch (Exception ex) {
                if (System.IO.File.Exists(Settings.TempFilesPath + "Preview.avi"))
                    SetEncodeSettings(EncodeSettings);
                MessageBox.Show(this, ex.Message, "Encoding Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) {
            this.Close();
        }

        private bool Validate() {
            bool Error = !IsValid(this) ||
                string.IsNullOrEmpty(encodeSettings.FileName) ||
                !encodeSettings.SourceHeight.HasValue ||
                !encodeSettings.SourceWidth.HasValue ||
                !encodeSettings.SourceFrameRate.HasValue;
            return !Error;
        }

        private bool IsValid(DependencyObject obj) {
            // The dependency object is valid if it has no errors and all
            // of its children (that are dependency objects) are error-free.
            return !Validation.GetHasError(obj) &&
                LogicalTreeHelper.GetChildren(obj)
                .OfType<DependencyObject>()
                .All(IsValid);
        }

        private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            //MediaEncoderSettings NewSettings = new MediaEncoderSettings();
            //NewSettings.FileName = settings.FileName;
            //NewSettings.SourceHeight = settings.SourceHeight;
            //NewSettings.SourceWidth = settings.SourceWidth;
            //NewSettings.SourceAspectRatio = settings.SourceAspectRatio;
            //NewSettings.SourceFrameRate = settings.SourceFrameRate;
            //NewSettings.Position = settings.Position;

            if (PresetCombo.SelectedIndex == 0) {
                // Good SD
                encodeSettings.DoubleNNEDI3Before = true;
                encodeSettings.DoubleEEDI3 = false;
                encodeSettings.DoubleNNEDI3 = true;
                encodeSettings.Denoise = true;
                encodeSettings.DenoiseStrength = 30;
                encodeSettings.SharpenAfterDouble = true;
                encodeSettings.SharpenAfterDoubleStrength = 10;
                encodeSettings.SharpenFinal = true;
                encodeSettings.SharpenFinalStrength = 10;
                encodeSettings.Resize = true;
                encodeSettings.ResizeHeight = 720;
                encodeSettings.EncodeQuality = 24;
                encodeSettings.IncreaseFrameRate = true;
                encodeSettings.IncreaseFrameRateValue = FrameRateModeEnum.fps60;
                if (encodeSettings.SourceHeight == 360) {
                    encodeSettings.Resize = false;
                    encodeSettings.DoubleNNEDI3 = false;
                }
            } else if (PresetCombo.SelectedIndex == 1) {
                // Average SD
                encodeSettings.DoubleNNEDI3Before = true;
                encodeSettings.DoubleEEDI3 = true;
                encodeSettings.DoubleNNEDI3 = false;
                encodeSettings.Denoise = true;
                encodeSettings.DenoiseStrength = 30;
                encodeSettings.SharpenAfterDouble = true;
                encodeSettings.SharpenAfterDoubleStrength = 10;
                encodeSettings.SharpenFinal = true;
                encodeSettings.SharpenFinalStrength = 10;
                encodeSettings.Resize = true;
                encodeSettings.ResizeHeight = 720;
                encodeSettings.EncodeQuality = 24;
                encodeSettings.IncreaseFrameRate = true;
                encodeSettings.IncreaseFrameRateValue = FrameRateModeEnum.fps60;
                if (encodeSettings.SourceHeight == 360) {
                    encodeSettings.Resize = false;
                    encodeSettings.DoubleEEDI3 = false;
                }
            } else if (PresetCombo.SelectedIndex == 2) {
                // Noisy SD
                encodeSettings.DoubleNNEDI3Before = false;
                encodeSettings.DoubleEEDI3 = true;
                encodeSettings.DoubleNNEDI3 = true;
                encodeSettings.Denoise = true;
                encodeSettings.DenoiseStrength = 30;
                encodeSettings.SharpenAfterDouble = true;
                encodeSettings.SharpenAfterDoubleStrength = 10;
                encodeSettings.SharpenFinal = true;
                encodeSettings.SharpenFinalStrength = 25;
                encodeSettings.Resize = true;
                encodeSettings.ResizeHeight = 720;
                encodeSettings.EncodeQuality = 25;
                encodeSettings.IncreaseFrameRate = true;
                encodeSettings.IncreaseFrameRateValue = FrameRateModeEnum.fps60;
                if (encodeSettings.SourceHeight == 360) {
                    encodeSettings.Resize = false;
                    encodeSettings.DoubleNNEDI3 = false;
                }
            } else if (PresetCombo.SelectedIndex == 3) {
                // DVD
                encodeSettings.DoubleNNEDI3Before = (encodeSettings.SourceHeight.HasValue && encodeSettings.SourceHeight.Value < 540);
                encodeSettings.DoubleEEDI3 = false;
                encodeSettings.DoubleNNEDI3 = true;
                encodeSettings.Denoise = true;
                encodeSettings.DenoiseStrength = 10;
                encodeSettings.SharpenAfterDouble = false;
                encodeSettings.SharpenFinal = false;
                encodeSettings.Resize = true;
                encodeSettings.ResizeHeight = 1080;
                encodeSettings.EncodeQuality = 24;
                encodeSettings.IncreaseFrameRate = false;
                encodeSettings.IncreaseFrameRateValue = FrameRateModeEnum.Double;
            } else if (PresetCombo.SelectedIndex == 4) {
                // Convert to 60fps
                encodeSettings.DoubleNNEDI3Before = false;
                encodeSettings.DoubleEEDI3 = false;
                encodeSettings.DoubleNNEDI3 = false;
                encodeSettings.Denoise = false;
                encodeSettings.DenoiseStrength = 10;
                encodeSettings.SharpenAfterDouble = false;
                encodeSettings.SharpenFinal = false;
                encodeSettings.Resize = false;
                encodeSettings.ResizeHeight = 720;
                encodeSettings.EncodeQuality = 24;
                encodeSettings.IncreaseFrameRate = true;
                encodeSettings.IncreaseFrameRateValue = FrameRateModeEnum.fps60;
            } else {
                // Upscale For Production
                encodeSettings.DoubleNNEDI3Before = (encodeSettings.SourceHeight.HasValue && encodeSettings.SourceHeight.Value < 480);
                encodeSettings.DoubleEEDI3 = false;
                encodeSettings.DoubleNNEDI3 = true;
                encodeSettings.DenoiseStrength = 30;
                encodeSettings.SharpenFinalStrength = 20;
                encodeSettings.Denoise = false;
                encodeSettings.SharpenAfterDouble = false;
                encodeSettings.SharpenFinal = false;
                encodeSettings.Resize = false;
                encodeSettings.IncreaseFrameRate = true;
                encodeSettings.IncreaseFrameRateValue = FrameRateModeEnum.Double;
                encodeSettings.EncodeQuality = 16;
            }

            // Fix colors when converting SD to HD. Flv files generally don't need this.
            if (String.Compare(Path.GetExtension(encodeSettings.FileName), ".flv", true) == 0)
                encodeSettings.FixColors = false;
            else
                encodeSettings.FixColors = (encodeSettings.SourceHeight <= 480);
        }
    }
}