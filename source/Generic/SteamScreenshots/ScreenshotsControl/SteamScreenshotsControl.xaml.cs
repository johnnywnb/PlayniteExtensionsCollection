﻿using FlowHttp;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using PluginsCommon;
using PluginsCommon.Converters;
using SteamCommon;
using SteamCommon.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace SteamScreenshots.ScreenshotsControl
{
    /// <summary>
    /// Interaction logic for SteamScreenshotsControl.xaml
    /// </summary>
    public partial class SteamScreenshotsControl : PluginUserControl, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private static readonly ILogger _logger = LogManager.GetLogger();
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _pluginStoragePath;
        private readonly SteamScreenshotsSettingsViewModel _settingsViewModel;
        private readonly DesktopView _activeViewAtCreation;
        private readonly DispatcherTimer _updateControlDataDelayTimer;
        private readonly ImageUriToBitmapImageConverter _imageUriToBitmapImageConverter;
        private bool _isValuesDefaultState = true;
        private Game _currentGame;
        private Guid _activeContext = default;
        
        private ObservableCollection<SteamAppDetails.AppDetails.Screenshot> _screenshots = new ObservableCollection<SteamAppDetails.AppDetails.Screenshot>();
        public ObservableCollection<SteamAppDetails.AppDetails.Screenshot> Screenshots
        {
            get => _screenshots;
            set
            {
                _screenshots = value;
                OnPropertyChanged();
            }
        }

        private Uri _currentImageUri;
        public Uri CurrentImageUri
        {
            get => _currentImageUri;
            set
            {
                _currentImageUri = value;
                OnPropertyChanged();
            }
        }

        private SteamAppDetails.AppDetails.Screenshot _selectedScreenshot;
        private ScrollViewer _screenshotsScrollViewer;

        public SteamAppDetails.AppDetails.Screenshot SelectedScreenshot
        {
            get => _selectedScreenshot;
            set
            {
                _selectedScreenshot = value;
                OnPropertyChanged();
                CurrentImageUri = _selectedScreenshot != null
                    ? _selectedScreenshot.PathThumbnail
                    : null;
            }
        }

        public SteamScreenshotsControl(SteamScreenshots plugin, SteamScreenshotsSettingsViewModel settingsViewModel, ImageUriToBitmapImageConverter imageUriToBitmapImageConverter)
        {
            _imageUriToBitmapImageConverter = imageUriToBitmapImageConverter;
            Resources.Add("ImageUriToBitmapImageConverter", imageUriToBitmapImageConverter);
            _playniteApi = API.Instance;
            SetControlTextBlockStyle();
            _pluginStoragePath = plugin.GetPluginUserDataPath();
            _settingsViewModel = settingsViewModel;
            if (_playniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop)
            {
                _activeViewAtCreation = _playniteApi.MainView.ActiveDesktopView;
            }

            _updateControlDataDelayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1100)
            };

            _updateControlDataDelayTimer.Tick += new EventHandler(UpdateControlData);

            InitializeComponent();
            DataContext = this;
        }

        private void SetControlTextBlockStyle()
        {
            // Desktop mode uses BaseTextBlockStyle and Fullscreen Mode uses TextBlockBaseStyle
            var baseStyleName = _playniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop ? "BaseTextBlockStyle" : "TextBlockBaseStyle";
            if (ResourceProvider.GetResource(baseStyleName) is Style baseStyle && baseStyle.TargetType == typeof(TextBlock))
            {
                var implicitStyle = new Style(typeof(TextBlock), baseStyle);
                Resources.Add(typeof(TextBlock), implicitStyle);
            }
        }

        private async void UpdateControlData(object sender, EventArgs e)
        {
            _updateControlDataDelayTimer.Stop();
            await UpdateControlAsync();
        }

        private void SetCollapsedVisibility()
        {
            Visibility = Visibility.Collapsed;
            _settingsViewModel.Settings.IsControlVisible = false;
            Screenshots.Clear();
        }

        private void SetVisibleVisibility()
        {
            if (_screenshotsScrollViewer is null)
            {
                _screenshotsScrollViewer = FindVisualChild<ScrollViewer>(ScreenshotsListBox);
            }

            _screenshotsScrollViewer?.ScrollToHorizontalOffset(0);
            Visibility = Visibility.Visible;
            _settingsViewModel.Settings.IsControlVisible = true;
        }

        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child != null && child is T)
                {
                    return (T)child;
                }
                else
                {
                    T childOfChild = FindVisualChild<T>(child);
                    if (childOfChild != null)
                    {
                        return childOfChild;
                    }
                }
            }

            return null;
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            //The GameContextChanged method is rised even when the control
            //is not in the active view. To prevent unecessary processing we
            //can stop processing if the active view is not the same one was
            //the one during creation
            _updateControlDataDelayTimer.Stop();
            if (_playniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop && _activeViewAtCreation != _playniteApi.MainView.ActiveDesktopView)
            {
                return;
            }

            if (!_isValuesDefaultState)
            {
                ResetToDefaultValues();
            }

            _currentGame = newContext;
            _updateControlDataDelayTimer.Start();
        }

        private void ResetToDefaultValues()
        {
            SetCollapsedVisibility();
            _activeContext = default;
            _isValuesDefaultState = true;
        }

        private async Task UpdateControlAsync()
        {
            if (_currentGame is null)
            {
                return;
            }

            await LoadControlData(_currentGame).ConfigureAwait(false);
        }

        private async Task LoadControlData(Game game, CancellationToken cancellationToken = default)
        {
            var scopeContext = Guid.NewGuid();
            _activeContext = scopeContext;
            _isValuesDefaultState = false;
            var steamId = Steam.GetGameSteamId(game, true);
            if (steamId.IsNullOrEmpty())
            {
                return;
            }

            var gameDataPath = Path.Combine(_pluginStoragePath, "appdetails", $"{steamId}_appdetails.json");
            if (FileSystem.FileExists(gameDataPath))
            {
                await SetScreenshots(gameDataPath, false, scopeContext);
                return;
            }
            
            var url = string.Format(@"https://store.steampowered.com/api/appdetails?appids={0}", steamId);
            var result = await HttpRequestFactory.GetHttpFileRequest()
                .WithUrl(url)
                .WithDownloadTo(gameDataPath)
                .DownloadFileAsync(cancellationToken);
            if (!result.IsSuccess || _activeContext != scopeContext)
            {
                return;
            }

            await SetScreenshots(gameDataPath, true, scopeContext);
        }

        private async Task SetScreenshots(string gameDataPath, bool downloadScreenshots, Guid scopeContext)
        {
            try
            {
                var parsedData = Serialization.FromJsonFile<Dictionary<string, SteamAppDetails>>(gameDataPath);
                if (parsedData.Keys?.Any() != true)
                {
                    return;
                }

                var response = parsedData[parsedData.Keys.First()];
                if (!response.success || response.data is null)
                {
                    return;
                }

                if (!response.data.screenshots.HasItems())
                {
                    return;
                }

                if (downloadScreenshots)
                {
                    await DownloadScreenshotsThumbnails(response.data.screenshots);
                    if (_activeContext != scopeContext)
                    {
                        return;
                    }
                }

                foreach (var screenshot in response.data.screenshots)
                {
                    Screenshots.Add(screenshot);
                }

                SelectedScreenshot = Screenshots.FirstOrDefault();
                _playniteApi.MainView.UIDispatcher.Invoke(() => SetVisibleVisibility());
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error during DownloadAppScreenshotsThumbnails");
            }
        }

        private async Task DownloadScreenshotsThumbnails(List<SteamAppDetails.AppDetails.Screenshot> screenshots)
        {
            var tasks = new List<Func<Task>>();
            foreach (var screenshot in screenshots)
            {
                if (screenshot.PathThumbnail is null)
                {
                    continue;
                }

                tasks.Add(async () =>
                {
                    await _imageUriToBitmapImageConverter
                    .DownloadUriToStorageAsync(screenshot.PathThumbnail);
                });
            }

            using (var taskExecutor = new TaskExecutor(4))
            {
                await taskExecutor.ExecuteAsync(tasks);
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }


    }
}
