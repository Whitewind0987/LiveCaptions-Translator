using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.audio.windows;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using Wpf.Ui.Controls;

namespace LiveCaptionsTranslator
{
    public partial class SettingPage : Page
    {
        private static SettingWindow? SettingWindow;
        private bool loadingAudioDevices;

        public SettingPage()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();
            DataContext = Translator.Setting;

            Loaded += SettingPage_Loaded;

            TranslateAPIBox.ItemsSource = Translator.Setting?.Configs.Keys;
            TranslateAPIBox.SelectedIndex = 0;

            LoadAPISetting();
        }

        private async void SettingPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(
                    maxHeight: (int)App.Current.MainWindow.MinHeight);
                CheckForFirstUse();
                await LoadAudioEndpointsAsync();
            }
            catch (Exception ex)
            {
                ShowAudioEndpointFailure(ex.Message);
            }
        }

        private async void RefreshAudioDevicesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadAudioEndpointsAsync();
            }
            catch (Exception ex)
            {
                ShowAudioEndpointFailure(ex.Message);
            }
        }

        private void AudioOutputDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingAudioDevices ||
                AudioOutputDeviceBox.SelectedItem is not PlaybackDeviceOption selected)
            {
                return;
            }

            if (Translator.Setting == null)
                return;
            Translator.Setting.AudioOutputDeviceId = selected.EndpointId;
            AudioOutputDeviceStatus.Text = selected.Diagnostic;
        }

        private async Task LoadAudioEndpointsAsync()
        {
            loadingAudioDevices = true;
            AudioOutputDeviceBox.IsEnabled = false;
            try
            {
                await using var provider = new WindowsAudioEndpointProvider();
                var result = await provider.EnumerateAsync();
                if (!result.Success)
                {
                    ShowAudioEndpointFailure(result.FailureReason ?? "Output-device enumeration failed.");
                    return;
                }

                var defaultEndpoint = result.Endpoints.FirstOrDefault(endpoint => endpoint.IsDefault);
                var options = new List<PlaybackDeviceOption>
                {
                    new(
                        null,
                        defaultEndpoint == null
                            ? "System default (unavailable)"
                            : $"System default ({defaultEndpoint.DisplayName})",
                        defaultEndpoint == null
                            ? "No active default output device is currently available."
                            : $"Follows the current system default: {defaultEndpoint.DisplayName}")
                };
                options.AddRange(result.Endpoints.Select(endpoint => new PlaybackDeviceOption(
                    endpoint.Id,
                    endpoint.DisplayName,
                    endpoint.IsDefault ? "Current system default output device." : "Active output device.")));

                AudioOutputDeviceBox.ItemsSource = options;
                var savedId = Translator.Setting?.AudioOutputDeviceId;
                var savedOption = options.FirstOrDefault(option =>
                    option.EndpointId != null &&
                    string.Equals(option.EndpointId, savedId, StringComparison.Ordinal));
                AudioOutputDeviceBox.SelectedItem = savedOption ?? options[0];
                AudioOutputDeviceStatus.Text = savedOption?.Diagnostic ??
                    (savedId == null
                        ? options[0].Diagnostic
                        : "Saved output device is unavailable; capture will use System default.");
            }
            finally
            {
                AudioOutputDeviceBox.IsEnabled = true;
                loadingAudioDevices = false;
            }
        }

        private void ShowAudioEndpointFailure(string reason)
        {
            loadingAudioDevices = true;
            AudioOutputDeviceBox.ItemsSource =
                new[] { new PlaybackDeviceOption(null, "System default (unavailable)", reason) };
            AudioOutputDeviceBox.SelectedIndex = 0;
            AudioOutputDeviceBox.IsEnabled = true;
            AudioOutputDeviceStatus.Text = reason;
            loadingAudioDevices = false;
        }

        private sealed record PlaybackDeviceOption(
            string? EndpointId,
            string DisplayName,
            string Diagnostic);

        private async void LiveCaptionsButton_click(object sender, RoutedEventArgs e)
        {
            var isVisible = Translator.IsCaptionWindowVisible;
            if (!isVisible.HasValue)
                return;

            var result = isVisible.Value
                ? await Translator.HideCaptionWindowAsync()
                : await Translator.ShowCaptionWindowAsync();

            if (result.Success)
                ButtonText.Text = isVisible.Value ? "Show" : "Hide";
        }

        private void TranslateAPIBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadAPISetting();
        }

        private void TargetLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TargetLangBox.SelectedItem != null)
                Translator.Setting.TargetLanguage = TargetLangBox.SelectedItem.ToString();
        }

        private void TargetLangBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Translator.Setting.TargetLanguage = TargetLangBox.Text;
        }

        private void APISettingButton_click(object sender, RoutedEventArgs e)
        {
            if (SettingWindow != null && SettingWindow.IsLoaded)
                SettingWindow.Activate();
            else
            {
                SettingWindow = new SettingWindow();
                SettingWindow.Closed += (sender, args) => SettingWindow = null;
                SettingWindow.Show();
            }
        }

        private void Contexts_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (Translator.Setting.DisplaySentences > Translator.Setting.NumContexts)
                Translator.Setting.DisplaySentences = Translator.Setting.NumContexts;
        }

        private void DisplaySentences_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (Translator.Setting.DisplaySentences > Translator.Setting.NumContexts)
                Translator.Setting.NumContexts = Translator.Setting.DisplaySentences;
            Translator.Caption.OnPropertyChanged("DisplayLogCards");
            Translator.Caption.OnPropertyChanged("OverlayPreviousTranslation");
        }

        private void LiveCaptionsInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Show();
        }

        private void LiveCaptionsInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Hide();
        }

        private void FrequencyInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Show();
        }

        private void FrequencyInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Hide();
        }

        private void TranslateAPIInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            TranslateAPIInfoFlyout.Show();
        }

        private void TranslateAPIInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            TranslateAPIInfoFlyout.Hide();
        }

        private void TargetLangInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Show();
        }

        private void TargetLangInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Hide();
        }

        private void CaptionLogMaxInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            CaptionLogMaxInfoFlyout.Show();
        }

        private void CaptionLogMaxInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            CaptionLogMaxInfoFlyout.Hide();
        }

        private void ContextAwareInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            ContextAwareInfoFlyout.Show();
        }

        private void ContextAwareInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            ContextAwareInfoFlyout.Hide();
        }

        private void CheckForFirstUse()
        {
            var isVisible = Translator.IsCaptionWindowVisible;
            if (isVisible.HasValue)
                ButtonText.Text = isVisible.Value ? "Hide" : "Show";
        }

        public void LoadAPISetting()
        {
            var configType = Translator.Setting[Translator.Setting.ApiName].GetType();
            var languagesProp = configType.GetProperty(
                "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            // Traverse base classes to find `SupportedLanguages`
            while (configType != null && languagesProp == null)
            {
                configType = configType.BaseType;
                languagesProp = configType.GetProperty(
                    "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);
            }
            if (languagesProp == null)
                languagesProp = typeof(TranslateAPIConfig).GetProperty(
                    "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            var supportedLanguages = (Dictionary<string, string>)languagesProp.GetValue(null);
            TargetLangBox.ItemsSource = supportedLanguages.Keys;

            string targetLang = Translator.Setting.TargetLanguage;
            if (!supportedLanguages.ContainsKey(targetLang))
                supportedLanguages[targetLang] = targetLang;    // add custom language to supported languages
            TargetLangBox.SelectedItem = targetLang;
        }
    }
}
