using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using RSTT.Core.Models;
using Xunit;

namespace RSTT.Speech.Tests;

[Collection(NativeInputTestGroup.Name)]
public sealed class UiLayoutTests
{
    [Fact]
    public async Task RendersDashboardSettingsAndRecorderWithoutOpeningAWindow()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var root = new DirectoryInfo(TestRepository.AppWorkerRoot);
                while (!File.Exists(Path.Combine(root.FullName, "RSTT.sln"))) root = root.Parent!;
                var app = XDocument.Load(Path.Combine(root.FullName, "src/RSTT.App/App.xaml"));
                var output = Path.Combine(root.FullName, "artifacts/test-results/ui");
                Directory.CreateDirectory(output);
                Render(root.FullName, app, "MainWindow.xaml", new PreviewModel { SelectedPageIndex = 0 }, 1180, 780, Path.Combine(output, "dashboard.png"));
                Render(root.FullName, app, "MainWindow.xaml", new PreviewModel { SelectedPageIndex = 4 }, 1180, 1280, Path.Combine(output, "settings.png"));
                Render(root.FullName, app, "ShortcutRecorderWindow.xaml", new PreviewModel(), 550, 340, Path.Combine(output, "recorder.png"));
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task;
    }

    private static void Render(string root, XDocument app, string file, object data, int width, int height, string output)
    {
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = XDocument.Load(Path.Combine(root, "src/RSTT.App", file));
        var content = new XElement(source.Root!.Elements().Last());
        // Exercise the real styles and layout with fixture data; event handlers are
        // covered by behavioral tests and cannot be resolved by loose XAML.
        foreach (var attribute in content.DescendantsAndSelf().Attributes().ToArray())
        {
            if (attribute.Name.LocalName is "Click" or "MouseLeftButtonDown" or "PasswordChanged" or "SelectionChanged") attribute.Remove();
        }
        var canvas = new XElement(ns + "Grid",
            new XAttribute(XNamespace.Xmlns + "x", x),
            new XAttribute(XNamespace.Xmlns + "infra", "clr-namespace:RSTT.App.Infrastructure;assembly=RSTT.App"),
            new XAttribute(XNamespace.Xmlns + "state", "clr-namespace:RSTT.Core.State;assembly=RSTT.Core"),
            new XAttribute("TextElement.Foreground", "#F2F7FA"),
            new XAttribute("Background", "#080D16"),
            new XElement(ns + "Grid.Resources", app.Root!.Element(ns + "Application.Resources")!.Elements()), content);
        // Resource CLR namespaces declared in App.xaml need an explicit assembly
        // when loading the same resources outside their compiled application.
        var xml = canvas.ToString().Replace("clr-namespace:RSTT.App.Infrastructure\"", "clr-namespace:RSTT.App.Infrastructure;assembly=RSTT.App\"", StringComparison.Ordinal);
        var visual = (Grid)XamlReader.Parse(xml);
        visual.DataContext = data;
        visual.Measure(new Size(width, height));
        visual.Arrange(new Rect(0, 0, width, height));
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(output);
        encoder.Save(stream);
        Assert.True(stream.Length > 1000);
    }

    // WPF bindings require instance properties, including constant fixture values.
#pragma warning disable CA1822
    public sealed class PreviewModel
    {
        public int SelectedPageIndex { get; set; }
        public bool HasTranscript => true;
        public bool ShowOnboarding => false;
        public string PreviewStableText => "This paragraph is waiting to be pasted. Focus an input and press MMB.";
        public string PreviewPendingText => " New speech appears here.";
        public string PasteRecognizedSentencesHotkey => "MMB";
        public string ToggleListeningHotkey => "Ctrl+Alt+Shift+R";
        public string ToggleCaptionsHotkey => "Ctrl+Alt+C";
        public string StatusLabel => "Ready";
        public string LiveHeadline => "Ready to listen";
        public string LiveDescription => "Your local model is loaded and no audio is being captured.";
        public string ActiveRuntimeLabel => "CUDA verified · ready";
        public string ActiveLanguageLabel => "en";
        public string TargetApplicationLabel => "Paste on demand · MMB";
        public string StartStopText => "Start listening";
        public string ModelStatusText => "Ready";
        public ModelInformation ActiveModel => new("parakeet", "Parakeet Unified English 0.6B", "", true);
        public AudioDevice SelectedAudioDevice { get; set; } = new("test", "Headphone (Realtek(R) Audio)", true);
        public string SelectedAudioDeviceName => "Headphone (Realtek(R) Audio)";
        public IEnumerable<ComputeBackend> ComputeBackends => Enum.GetValues<ComputeBackend>();
        public ComputeBackend ComputeBackend { get; set; } = ComputeBackend.Cuda;
        public IEnumerable<RecognitionMode> RecognitionModes => Enum.GetValues<RecognitionMode>();
        public RecognitionMode RecognitionMode { get; set; } = RecognitionMode.Accuracy;
        public string[] RecognitionLanguages => ["en"];
        public string DefaultLanguage { get; set; } = "en";
        public string ComputeStatus => "Requested: Cuda · CUDA verified. 3282 CUDA nodes in 3 model sessions; 1151 CPU nodes.";
        public string PerformanceSummary => "0.3% CPU · 0.74 RTF · 0 ms queued · 167 MB";
    }
#pragma warning restore CA1822
}
