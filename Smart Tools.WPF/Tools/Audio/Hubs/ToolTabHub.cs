using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using System.Windows;
using System.Windows.Controls;
using Border = System.Windows.Controls.Border;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// A tool view that can show a hub's tab strip at the top of its own content column (the sidebar keeps the full height, like Unit &amp; Currency).
    /// The view has an empty ContentPresenter named HubTabsHost in the first row of its main grid and puts the strip in it.
    /// </summary>
    internal interface IHubPage
    {
        void SetHubTabs(UIElement? strip);
    }

    /// <summary>One page of a <see cref="ToolTabHub"/>: the tool that used to be its own sidebar entry.</summary>
    internal sealed record HubTab(string LabelKey, string LabelFallback, PackIconKind Icon, Func<UIElement> Create, Action<UIElement> Shutdown);

    /// <summary>
    /// One sidebar tool made of a few full tools, shown one at a time behind a tab bar (the same idea as Media Viewer's pages).
    /// Each page is the whole original view with its own options, settings and files; the hub only switches between them.
    /// Only the page shown is in the visual tree, and every page that was opened is told to stop when the app or the tool closes.
    /// </summary>
    public abstract class ToolTabHub : UserControl
    {
        private static readonly Dictionary<string, int> LastTab = new();   // remembered while the app runs

        private readonly string _hubId;
        private readonly IReadOnlyList<HubTab> _tabs;
        private readonly Dictionary<int, UIElement> _pages = new();
        private readonly ContentControl _host = new() { IsTabStop = false, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, VerticalContentAlignment = System.Windows.VerticalAlignment.Stretch };
        private readonly Border _bar;
        private UIElement? _shown;
        private readonly List<RadioButton> _buttons = new();
        private bool _switching;

        internal ToolTabHub(string hubId, string tourKey, IReadOnlyList<HubTab> tabs)
        {
            _hubId = hubId;
            _tabs = tabs;

            // The same tabs as the Unit & Currency categories: icon + name, accent underline, inside a card.
            var strip = new WrapPanel();
            for (int i = 0; i < tabs.Count; i++)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new PackIcon { Kind = tabs[i].Icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.TextProperty, tabs[i].LabelKey);
                content.Children.Add(label);
                var button = new RadioButton { GroupName = "HubTab_" + hubId, Tag = i, Content = content };
                button.SetResourceReference(StyleProperty, "UnderlineTabStyle");
                button.Checked += (_, _) => { if (!_switching && button.Tag is int index) Show(index); };
                _buttons.Add(button);
                strip.Children.Add(button);
            }

            _bar = new Border { Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(6, 6, 6, 0), Child = strip };
            _bar.SetResourceReference(StyleProperty, "CardStyle");
            Tour.SetKey(_bar, tourKey);
            Tour.SetOrder(_bar, 0);

            _host.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ContentBg");
            Content = _host;

            Show(LastTab.TryGetValue(hubId, out int last) && last >= 0 && last < tabs.Count ? last : 0);
        }

        private void Show(int index)
        {
            _switching = true;
            _buttons[index].IsChecked = true;
            _switching = false;
            LastTab[_hubId] = index;
            if (!_pages.TryGetValue(index, out var page)) _pages[index] = page = _tabs[index].Create();
            // The strip lives in the page's own content column (one parent at a time): take it from the old page first.
            (_shown as IHubPage)?.SetHubTabs(null);
            (page as IHubPage)?.SetHubTabs(_bar);
            _shown = page;
            // Swapping the content unloads the old page (it releases its own playback, like leaving any tool) and loads the new one.
            _host.Content = page;
        }

        /// <summary>Called from MainWindow.Window_Closing: lets every page that was opened stop its work and release files and devices.</summary>
        public void CancelActiveRun()
        {
            foreach (var (index, page) in _pages) _tabs[index].Shutdown(page);
        }
    }

    /// <summary>Text to Speech + Speech to Text.</summary>
    public sealed class SpeechStudioView : ToolTabHub
    {
        public SpeechStudioView() : base("speech-studio", "Str_Tour_SpeechStudio_Tabs", new HubTab[]
        {
            new("Str_AudioHub_TextToSpeech", "Text to Speech", PackIconKind.Speaker, () => new TextToSpeechView(), v => ((TextToSpeechView)v).CancelActiveRun()),
            new("Str_AudioHub_SpeechToText", "Speech to Text", PackIconKind.Subtitles, () => new SpeechToTextView(), v => ((SpeechToTextView)v).CancelActiveRun()),
        }) { }
    }

    /// <summary>Loudness Meter &amp; Normalizer + Audio Trimmer &amp; Joiner.</summary>
    public sealed class AudioEditorView : ToolTabHub
    {
        public AudioEditorView() : base("audio-editor", "Str_Tour_AudioEditor_Tabs", new HubTab[]
        {
            new("Str_AudioHub_Trimmer", "Trim & Join", PackIconKind.ContentCut, () => new AudioTrimmerView(), v => ((AudioTrimmerView)v).CancelActiveRun()),
            new("Str_AudioHub_Loudness", "Loudness", PackIconKind.Speedometer, () => new LoudnessView(), v => ((LoudnessView)v).CancelActiveRun()),
        }) { }
    }

    /// <summary>Audio Recorder + Voice Changer.</summary>
    public sealed class VoiceStudioView : ToolTabHub
    {
        public VoiceStudioView() : base("voice-studio", "Str_Tour_VoiceStudio_Tabs", new HubTab[]
        {
            new("Str_AudioHub_Recorder", "Recorder", PackIconKind.Microphone, () => new MicRecorderView(), v => ((MicRecorderView)v).CancelActiveRun()),
            new("Str_AudioHub_VoiceChanger", "Voice Changer", PackIconKind.Tune, () => new VoiceChangerView(), v => ((VoiceChangerView)v).CancelActiveRun()),
        }) { }
    }
}
