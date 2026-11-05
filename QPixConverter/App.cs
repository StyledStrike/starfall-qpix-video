using System.Collections.ObjectModel;
using System.Text.Json;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

using QPixLib;

namespace QPixConverter;

public static class Program
{
    public static void Main()
    {
        if (OperatingSystem.IsWindows())
        {
            Win32Platform.Register();
            MewVGWin32Backend.Register();
        }
        else if (OperatingSystem.IsLinux())
        {
            X11Platform.Register();
            MewVGX11Backend.Register();
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported OS.");
        }

        var app = new App();
        Application.Run(app.Window);
    }
}

public class App
{
    private readonly Logger _logger;

    private readonly Grid _inputPanel;
    private readonly Grid _outputPanel;
    private readonly Grid _optionsPanel;

    private readonly TextBox _inputFileEntry;
    private readonly TextBox _outputDirEntry;
    private readonly TextBox _outputTitleEntry;

    private readonly Ellipse _statusDot;
    private readonly Label _statusLabel;
    private readonly ProgressBar _statusProgress;
    private readonly Button _convertButton;

    private readonly NumericUpDown _widthEntry;
    private readonly NumericUpDown _heightEntry;
    private readonly NumericUpDown _fpsEntry;
    private readonly ComboBox _formatEntry;

    private ConvertJob? _activeJob;
    private readonly TimeSpan _progressUpdateInterval = TimeSpan.FromSeconds(1.0 / 4);
    private readonly Timer _progressTimer;
    private UserSettings _userSettings;

    public Window Window { get; private set; }

    public App()
    {
        _logger = new Logger("QPix Converter");

        var windowBody = new StackPanel()
            .Vertical()
            .Spacing(16);

        Window = new Window()
            .Title("QPix Converter")
            .FitContentHeight(700)
            .Content(windowBody);
        
        Window.Closed += () => SaveUserSettings();

        // Input file panel

        _inputPanel = new Grid()
            .Columns("*,Auto")
            .Spacing(4);

        _inputFileEntry = new TextBox()
            .Placeholder("...")
            .IsReadOnly(true);

        _inputPanel.Add(_inputFileEntry);

        _inputPanel.Add(new Button()
            .Content("Select...")
            .OnClick(OnClickSelectInputFile));

        windowBody.Add(new GroupBox()
            .Header("Source video file")
            .Content(_inputPanel));

        // Output directory panel

        _outputPanel = new Grid()
            .Columns("*,Auto")
            .Spacing(4);

        _outputDirEntry = new TextBox()
            .Placeholder("...")
            .IsReadOnly(true);

        _outputPanel.Add(_outputDirEntry);

        _outputPanel.Add(new Button()
            .Content("Select...")
            .OnClick(OnClickSelectOutputDir));

        windowBody.Add(new GroupBox()
            .Header("Output directory")
            .Content(_outputPanel));
        
        // Output video title panel

        _outputTitleEntry = new TextBox()
            .Placeholder("Video title...");

        windowBody.Add(new GroupBox()
            .Header("Video title")
            .Content(_outputTitleEntry));

        // Video options panel

        _optionsPanel = new Grid()
            .Columns("Auto,150,Auto,150")
            .Rows("Auto,Auto")
            .AutoIndexing()
            .Spacing(6)
            .Padding(16)
            .CenterHorizontal();

        _optionsPanel.Add(new TextBlock()
            .Text("Max. width")
            .CenterVertical());

        _widthEntry = new NumericUpDown()
            .Minimum(8)
            .Maximum(255)
            .SmallChange(1)
            .Format("0");

        _optionsPanel.Add(_widthEntry);

        _optionsPanel.Add(new TextBlock()
            .Text("Max. height"));

        _heightEntry = new NumericUpDown()
            .Minimum(8)
            .Maximum(255)
            .SmallChange(1)
            .Format("0");

        _optionsPanel.Add(_heightEntry);

        _optionsPanel.Add(new TextBlock()
            .Text("Max. FPS"));

        _fpsEntry = new NumericUpDown()
            .Minimum(1)
            .Maximum(60)
            .SmallChange(1)
            .Format("0");

        _optionsPanel.Add(_fpsEntry);

        _optionsPanel.Add(new TextBlock()
            .Text("Color format"));


        _formatEntry = new ComboBox()
            .Items(Enum.GetNames(typeof(QPixFormat)));

        _optionsPanel.Add(_formatEntry);

        windowBody.Add(new Expander()
            .Header("QPix video options")
            .IsExpanded(false)
            .Content(_optionsPanel));

        // Status & actions panel

        var actionsPanel = new Grid()
            .Columns("Auto,*,200,150")
            .Spacing(8);

        windowBody.Add(actionsPanel);

        _statusDot = new Ellipse()
            .Size(10, 10)
            .CenterVertical();

        actionsPanel.Add(_statusDot);

        _statusLabel = new Label()
            .TextWrapping(TextWrapping.NoWrap)
            .FontSize(ThemeFontSize.Small);

        actionsPanel.Add(_statusLabel);

        _statusProgress = new ProgressBar()
            .CenterVertical();

        actionsPanel.Add(_statusProgress);

        _convertButton = new Button()
            .OnClick(OnClickConvert)
            .StyleSheet(new StyleSheet().WithName<Button>(BuiltInStyles.AccentButton));

        actionsPanel.Add(_convertButton);

        // Setup state & load user preferences
        
        _progressTimer = new Timer(OnProgressTimer);

        _userSettings = new()
        {
            MaxWidth = 128,
            MaxHeight = 128,
            MaxFps = 30,
            ColorFormat = QPixFormat.PACKED_RGB332
        };

        UpdateEnabledUIComponents();
        UpdateStatusBar();

        try
        {
            LoadUserSettings();
        }
        catch(Exception e)
        {
            _logger.Warn("Could not read user settings:", e.Message);
        }

        SetInputFile(_userSettings.LastInputFilePath);
        SetOutputDirectory(_userSettings.LastOutputDirPath);
        SetVideoOptions(_userSettings.MaxWidth, _userSettings.MaxHeight, _userSettings.MaxFps, _userSettings.ColorFormat);
    }

    private void ResetProgressTimer()
    {
        _progressTimer.Change(_progressUpdateInterval, TimeSpan.FromMilliseconds(-1));
    }

    private void OnProgressTimer(object? state)
    {
        if (_activeJob == null)
            return;

        var progress = _activeJob.Progress;

        _statusLabel.Text = $"Converting... ({progress.ProcessedFrameCount}/{progress.TotalFrameCount} frames, {Utils.HumanFileSize(progress.VideoSizeBytes)})";
        _statusProgress.Maximum = progress.TotalFrameCount;
        _statusProgress.Value = progress.ProcessedFrameCount;
        _statusProgress.IsIndeterminate = progress.TotalFrameCount < 1;

        ResetProgressTimer();
    }

    private void UpdateEnabledUIComponents()
    {
        var isRunningJob = _activeJob != null;

        _convertButton.Content(isRunningJob ? "Cancel" : "Convert");

        _inputPanel.IsEnabled = !isRunningJob;
        _outputPanel.IsEnabled = !isRunningJob;
        _optionsPanel.IsEnabled = !isRunningJob;
        _outputTitleEntry.IsEnabled = !isRunningJob;
    }

    private void UpdateStatusBar()
    {
        var state = _activeJob == null ? ConvertJobState.IDLE : _activeJob.State;
    
        if (state != ConvertJobState.CONVERTING)
        {
            if (state == ConvertJobState.COMPLETED && _activeJob != null && _activeJob.Progress.TotalSizeBytes > 0)
                _statusLabel.Text = $"Written {Utils.HumanFileSize(_activeJob.Progress.TotalSizeBytes)}";
            else
                _statusLabel.Text = "Idle";

            _statusProgress.IsIndeterminate = false;
            _statusProgress.Maximum = 0;
            _statusProgress.Value = 0;
        }

        _statusDot.WithTheme((theme, shape) =>
        {
            switch (state)
            {
                case ConvertJobState.IDLE:
                    shape.Fill(theme.Palette.ControlBorder);
                    shape.Stroke(theme.Palette.ControlBorder);
                    break;

                case ConvertJobState.CONVERTING:
                    shape.Fill(Color.Yellow);
                    shape.Stroke(Color.Yellow);
                    break;

                case ConvertJobState.COMPLETED:
                    shape.Fill(Color.Green);
                    shape.Stroke(Color.Green);
                    break;

                case ConvertJobState.ERRORED:
                    shape.Fill(Color.Red);
                    shape.Stroke(Color.Red);
                    break;
            }
        });
    }

    private void OnClickSelectInputFile()
    {
        var lastFileDir = Path.GetDirectoryName(_userSettings.LastInputFilePath);
        var doesLastDirExist = Directory.Exists(lastFileDir);

        var filePath = FileDialog.OpenFile(new()
        {
            Owner = Window,
            InitialDirectory = doesLastDirExist ? lastFileDir : null,
            Filters = [
                new("Video files", ["*.mp4", "*.m4v", "*.webm", "*.avi", "*.mkv"]),
                new("GIF files", "*.gif"),
            ]
        });

        if (filePath != null)
            SetInputFile(filePath);
    }

    private void OnClickSelectOutputDir()
    {
        var doesLastDirExist = Directory.Exists(_userSettings.LastOutputDirPath);

        var folderPath = FileDialog.SelectFolder(new FolderDialogOptions
        {
            Owner = Window,
            InitialDirectory = doesLastDirExist ? _userSettings.LastOutputDirPath : null
        });

        if (folderPath != null)
            SetOutputDirectory(folderPath);
    }

    private async void OnClickConvert()
    {
        if (_activeJob == null)
        {
            try
            {
                _activeJob = CreateJob(GetInputFile(), GetOutputDirectory(), GetOutputTitle());

                UpdateEnabledUIComponents();
                UpdateStatusBar();
                ResetProgressTimer();

                await _activeJob.Start();

                StopActiveJob();
            }
            catch (Exception e)
            {
                MessageBox.Notify("Could not start the encoding.", PromptIconKind.Error, e.Message, Window);
            }
        }
        else
            StopActiveJob();
    }

    private void StopActiveJob()
    {
        _activeJob?.Stop();
        _activeJob = null;

        UpdateEnabledUIComponents();
    }

    private void SetInputFile(string? path)
    {
        _inputFileEntry.Text(path ?? "");
        _userSettings.LastInputFilePath = path;

        if (path != null)
        {
            var fileName = Path.GetFileNameWithoutExtension(path);

            if (fileName != null)
                _outputTitleEntry.Text(fileName);
        }
    }

    private string GetInputFile()
    {
        return _inputFileEntry.Text;
    }

    private void SetOutputDirectory(string? path)
    {
        path ??= Directory.GetCurrentDirectory();
        _outputDirEntry.Text(path);
        _userSettings.LastOutputDirPath = path;
    }

    private string GetOutputDirectory()
    {
        return _outputDirEntry.Text;
    }

    private string GetOutputTitle()
    {
        return _outputTitleEntry.Text;
    }

    private void SetVideoOptions(int maxWidth, int maxHeight, int maxFPS, QPixFormat format)
    {
        _widthEntry.Value(maxWidth);
        _heightEntry.Value(maxHeight);
        _fpsEntry.Value(maxFPS);
        _formatEntry.SelectedIndex((int)format);
    }

    private (int maxWidth, int maxHeight, int maxFPS, QPixFormat format) GetVideoOptions()
    {
        return ((int)_widthEntry.Value, (int)_heightEntry.Value, (int)_fpsEntry.Value, (QPixFormat)_formatEntry.SelectedIndex);
    }

    private ConvertJob CreateJob(string inputFilePath, string outputDir, string? outputTitle)
    {
        if (!File.Exists(inputFilePath))
            throw new FileNotFoundException("The input file does not exist", inputFilePath);

        if (!Directory.Exists(outputDir))
            throw new DirectoryNotFoundException("The output directory does not exist");

        var originalName = Path.GetFileNameWithoutExtension(inputFilePath);
        var outputFilePath = Path.Join(outputDir, string.Join("_", originalName.Split(Path.GetInvalidFileNameChars())) + ".dat");

        var (maxWidth, maxHeight, maxFPS, format) = GetVideoOptions();

        var options = new ConvertJobOptions
        {
            InputFilePath = inputFilePath,
            OutputFilePath = outputFilePath,
            Format = format,
            MaxFPS = maxFPS,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Title = string.IsNullOrEmpty(outputTitle) ? null : outputTitle,
            EnableAudio = Path.GetExtension(inputFilePath) != ".gif"
        };

        var encodeJob = new ConvertJob(options);
        encodeJob.OnStateChanged += OnConvertStateChanged;
        encodeJob.OnError += OnConvertError;

        return encodeJob;
    }

    private void OnConvertStateChanged(ConvertJob job, ConvertJobState state)
    {
        UpdateStatusBar();
        UpdateEnabledUIComponents();
    }

    private void OnConvertError(ConvertJob job, string reason)
    {
        UpdateStatusBar();
        MessageBox.Notify("An error has occurred during encoding.", PromptIconKind.Error, reason, Window);
    }

    private void LoadUserSettings()
    {
        var jsonPath = Path.Join(Directory.GetCurrentDirectory(), "user_settings.json");
        if (!File.Exists(jsonPath)) return;
        
        var jsonContent = File.ReadAllText(jsonPath);
        if (jsonContent == null) return;

        _userSettings = JsonSerializer.Deserialize(jsonContent, UserSettingsJsonContext.Default.UserSettings);
    }

    private void SaveUserSettings()
    {
        var (maxWidth, maxHeight, maxFPS, format) = GetVideoOptions();

        _userSettings.MaxWidth = maxWidth;
        _userSettings.MaxHeight = maxHeight;
        _userSettings.MaxFps = maxFPS;
        _userSettings.ColorFormat = format;

        var jsonContent = JsonSerializer.Serialize(_userSettings, UserSettingsJsonContext.Default.UserSettings);
        var jsonPath = Path.Join(Directory.GetCurrentDirectory(), "user_settings.json");

        using var file = File.CreateText(jsonPath);
        file.Write(jsonContent);
        file.Close();
    }
}
