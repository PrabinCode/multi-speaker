using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MultiSpeaker;

/// <summary>Explains how to use the app and what each setting and button does.</summary>
sealed class HelpWindow : Window
{
    sealed record Item(string? Title, string Text);

    static readonly (string Heading, Item[] Items)[] Sections =
    [
        ("Getting started",
        [
            new("1. Connect your speakers", "Pair them in Windows: Settings > Bluetooth & devices."),
            new("2. Choose the sound source", "Pick the speaker that lags the most (often the cheapest or oldest one). It becomes your laptop's sound output."),
            new("3. Tick the other speakers", "Tick every speaker that should play too, then press Start."),
            new("4. Line them up", "Pause your music, put the laptop where you sit, and press Sync delay, then Match volume."),
            new("5. Optional: link the volumes", "Tick Link volumes so your keyboard volume keys turn all speakers up and down together."),
        ]),
        ("Settings",
        [
            new("Sound source", "The device Windows plays to, the same as in the taskbar's sound menu; changing it here changes it there too. " +
                "It plays the sound itself and can't be delayed, so choose the speaker that lags the most. The others are delayed to match it."),
            new("Tick box", "Play on this speaker as well. The source's box is greyed out because it always plays."),
            new("Channel", "Both plays full stereo. Left or Right plays only that side: set one speaker to Left and another to Right " +
                "for a real stereo pair, and place them on those sides of the room."),
            new("Volume (%)", "The speaker's own volume, the same one Windows shows. Speakers differ in loudness, so equal numbers rarely sound equal; " +
                "use Match volume or your ears."),
            new("Delay (ms)", "Holds this speaker's sound back so it lines up with the source. Higher means later. " +
                "If you hear an echo, change it in 10 ms steps with - and + until it sounds like one speaker."),
            new("Link volumes", "Changing one speaker's volume changes the others by the same ratio, and so do your keyboard volume keys. " +
                "Untick it to adjust one speaker on its own, then tick it again."),
        ]),
        ("Buttons",
        [
            new("Start / Stop", "Starts or stops copying the sound to the ticked speakers. Stop also cancels Sync delay or Match volume."),
            new("Sync delay", "Plays a few short chirps and listens with your microphone to set every delay automatically. " +
                "Pause your music and keep the room quiet; it takes about 10 seconds per speaker."),
            new("Match volume", "Same chirps, but sets each speaker as loud as the source, as heard from the laptop. It never sets a speaker below 5 %."),
            new("Refresh devices", "Reads the speaker list again. Usually not needed: connecting or disconnecting a speaker is picked up automatically."),
            new("Title bar", "The sun/moon button switches between light and dark. Minimize hides MultiSpeaker in the tray; " +
                "right-click the tray icon to start, stop or exit."),
        ]),
        ("Good to know",
        [
            new(null, "Two speakers usually work well. Three or more can stutter, because one Bluetooth adapter has limited capacity. " +
                "Keeping Wi-Fi on 5 GHz helps."),
            new(null, "For Sync delay and Match volume, use a Bluetooth speaker as the source, not the laptop's own speakers: " +
                "those sit right next to the microphone, so they always sound loudest to it."),
            new(null, "A speaker the microphone can't hear (for example an HDMI output with nothing attached) is reported and left unchanged."),
            new(null, "If a speaker turns off and comes back, playback continues by itself. Your settings are saved automatically."),
        ]),
        ("Problems?",
        [
            new("Echo between speakers", "Run Sync delay again, or adjust the delay of the speaker that plays early."),
            new("\"Still ... ms late at 0 ms delay\"", "That speaker is slower than the source. Make it the sound source instead."),
            new("\"Can't play only one side\"", "This speaker can't be split while it's the source. Make another speaker the source and split that one."),
            new("Crackling or dropouts", "Move the speakers closer to the laptop, or use fewer speakers at once."),
        ]),
    ];

    public HelpWindow(Window owner, bool dark)
    {
        Owner = owner;
        Title = "MultiSpeaker help";
        Icon = owner.Icon;
        Width = 600;
        Height = 660;
        MinWidth = 420;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this, dark);

        var page = new StackPanel { Margin = new Thickness(24, 12, 24, 24) };
        foreach (var (heading, items) in Sections)
        {
            page.Children.Add(new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
            foreach (var item in items)
                page.Children.Add(CreateItem(item));
        }
        page.Children.Add(CreateRepoLink());

        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static TextBlock CreateItem(Item item)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), LineHeight = 20 };
        if (item.Title != null)
        {
            block.Inlines.Add(new Run(item.Title) { FontWeight = FontWeights.SemiBold });
            block.Inlines.Add(new LineBreak());
        }
        else
        {
            block.Inlines.Add(new Run("•  "));
        }
        var text = new Run(item.Text);
        text.SetResourceReference(TextElement.ForegroundProperty, "MutedTextBrush");
        block.Inlines.Add(text);
        return block;
    }

    static TextBlock CreateRepoLink()
    {
        var link = new Hyperlink(new Run("More help, updates and bug reports on GitHub"))
        {
            NavigateUri = new Uri(MainWindow.RepoUrl),
        };
        link.RequestNavigate += (_, e) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        return new TextBlock(link) { Margin = new Thickness(0, 12, 0, 0) };
    }
}
