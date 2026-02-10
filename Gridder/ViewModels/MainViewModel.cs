using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gridder.Helpers;
using Gridder.Models;
using Gridder.Services;

namespace Gridder.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ILibraryScanService _libraryScanService;
    private readonly IFolderPickerService _folderPickerService;
    private readonly IPythonAnalysisService _pythonAnalysisService;
    private readonly ISeratoTagService _seratoTagService;
    private readonly JsonExportService _jsonExportService;

    public WaveformEditorViewModel WaveformEditor { get; } = new();
    public PlaybackViewModel Playback { get; }

    public MainViewModel(
        ILibraryScanService libraryScanService,
        IFolderPickerService folderPickerService,
        IPythonAnalysisService pythonAnalysisService,
        ISeratoTagService seratoTagService,
        JsonExportService jsonExportService,
        PlaybackViewModel playbackViewModel)
    {
        _libraryScanService = libraryScanService;
        _folderPickerService = folderPickerService;
        _pythonAnalysisService = pythonAnalysisService;
        _seratoTagService = seratoTagService;
        _jsonExportService = jsonExportService;
        Playback = playbackViewModel;

        // Sync playback position to waveform cursor + auto-scroll
        Playback.PositionUpdated += pos =>
        {
            WaveformEditor.PlaybackPositionSeconds = pos;

            if (Playback.IsPlaying)
            {
                var viewportDuration = WaveformEditor.ViewWidthPixels / WaveformEditor.PixelsPerSecond;
                var targetScroll = pos - viewportDuration * 0.25;
                WaveformEditor.ScrollPositionSeconds = Math.Clamp(targetScroll, 0, Math.Max(0, WaveformEditor.TotalDurationSeconds - viewportDuration));
            }
        };

        // Wire click-to-seek from waveform
        WaveformEditor.SeekRequested += seconds => Playback.SeekTo(seconds);
    }

    [ObservableProperty]
    private string? _libraryPath;

    [ObservableProperty]
    private ObservableCollection<AudioTrack> _tracks = new();

    [ObservableProperty]
    private AudioTrack? _selectedTrack;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string _statusMessage = "Select a folder to scan your music library";

    [ObservableProperty]
    private int _trackCount;

    [ObservableProperty]
    private int _analyzedCount;

    [ObservableProperty]
    private int _withBeatGridCount;

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        try
        {
            var path = await _folderPickerService.PickFolderAsync();
            if (!string.IsNullOrEmpty(path))
            {
                LibraryPath = path;
                await ScanLibraryAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ScanLibraryAsync()
    {
        if (string.IsNullOrEmpty(LibraryPath))
            return;

        IsScanning = true;
        StatusMessage = "Scanning library...";
        Tracks.Clear();

        try
        {
            var tracks = await _libraryScanService.ScanFolderAsync(LibraryPath);

            foreach (var track in tracks)
                Tracks.Add(track);

            TrackCount = Tracks.Count;
            WithBeatGridCount = Tracks.Count(t => t.HasExistingBeatGrid);
            AnalyzedCount = 0;

            StatusMessage = $"Found {TrackCount} tracks ({WithBeatGridCount} with existing beatgrids)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error scanning: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeSelectedTrackAsync()
    {
        if (SelectedTrack == null) return;

        var track = SelectedTrack;
        IsAnalyzing = true;
        track.AnalysisStatus = AnalysisStatus.Analyzing;
        track.AnalysisError = null;

        var progress = new Progress<string>(msg =>
        {
            MainThread.BeginInvokeOnMainThread(() => StatusMessage = msg);
        });

        try
        {
            var result = await _pythonAnalysisService.AnalyzeTrackAsync(track.FilePath, progress);

            // Convert analysis result to BeatGrid
            AppLogger.Log("Analysis", $"Track: {track.FileName}");
            AppLogger.Log("Analysis", $"  Beats detected: {result.Beats.Length}");
            AppLogger.Log("Analysis", $"  Tempo segments from Python: {result.TempoSegments.Length}");
            foreach (var seg in result.TempoSegments)
                AppLogger.Log("Analysis", $"    Seg: {seg.Bpm:F2} BPM, {seg.BeatCount} beats, start={seg.StartPosition:F3}s");

            var beatGrid = ConvertToBeatGrid(result);
            track.BeatGrid = beatGrid;

            AppLogger.Log("Analysis", $"  Serato markers: {beatGrid.Markers.Count}");
            for (int mi = 0; mi < beatGrid.Markers.Count; mi++)
            {
                var m = beatGrid.Markers[mi];
                if (m.IsTerminal)
                    AppLogger.Log("Analysis", $"    Marker {mi}: pos={m.PositionSeconds:F3}s, BPM={m.Bpm:F2} (terminal)");
                else
                    AppLogger.Log("Analysis", $"    Marker {mi}: pos={m.PositionSeconds:F3}s, beatsUntilNext={m.BeatsUntilNext}");
            }
            AppLogger.Log("Analysis", $"  AllBeatPositions: {beatGrid.AllBeatPositions.Count}");

            // Validate: compute what Serato would actually produce vs actual beats
            ValidateBeatGridAccuracy(beatGrid, result);

            // Store waveform data
            if (result.Waveform != null)
            {
                track.WaveformData = new WaveformData
                {
                    SamplesPerPixel = result.Waveform.SamplesPerPixel,
                    PeaksPositive = result.Waveform.PeaksPositive,
                    PeaksNegative = result.Waveform.PeaksNegative,
                    OnsetEnvelope = result.Waveform.OnsetEnvelope,
                    DurationSeconds = result.DurationSeconds,
                    SampleRate = result.SampleRate,
                };
            }

            track.AnalysisStatus = AnalysisStatus.Analyzed;
            AnalyzedCount = Tracks.Count(t => t.AnalysisStatus == AnalysisStatus.Analyzed);

            // Load into waveform editor and playback
            WaveformEditor.LoadTrack(track);
            await Playback.LoadTrackAsync(track.FilePath, track.BeatGrid);

            var segCount = result.TempoSegments.Length;
            var bpmInfo = segCount == 1
                ? $"{result.TempoSegments[0].Bpm:F1} BPM"
                : $"{segCount} tempo segments";
            StatusMessage = $"Analysis complete: {result.Beats.Length} beats, {bpmInfo}";
        }
        catch (Exception ex)
        {
            track.AnalysisStatus = AnalysisStatus.Error;
            track.AnalysisError = ex.Message;
            StatusMessage = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private bool CanAnalyze() => SelectedTrack != null && !IsAnalyzing;

    partial void OnSelectedTrackChanged(AudioTrack? value)
    {
        AnalyzeSelectedTrackCommand.NotifyCanExecuteChanged();

        if (value != null)
        {
            StatusMessage = $"Selected: {value.DisplayArtist} - {value.DisplayName}";

            // If already analyzed, load waveform editor and playback
            if (value.AnalysisStatus == AnalysisStatus.Analyzed)
            {
                WaveformEditor.LoadTrack(value);
                _ = Playback.LoadTrackAsync(value.FilePath, value.BeatGrid);
            }
        }
    }

    partial void OnIsAnalyzingChanged(bool value)
    {
        AnalyzeSelectedTrackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SaveBeatGridAsync()
    {
        if (SelectedTrack?.BeatGrid == null || SelectedTrack.BeatGrid.Markers.Count == 0)
        {
            StatusMessage = "No beatgrid to save. Analyze the track first.";
            return;
        }

        try
        {
            var grid = SelectedTrack.BeatGrid;
            AppLogger.Log("Save", $"Saving beatgrid to {SelectedTrack.FileName}");
            AppLogger.Log("Save", $"  Markers: {grid.Markers.Count}, AllBeatPositions: {grid.AllBeatPositions.Count}");
            for (int mi = 0; mi < grid.Markers.Count; mi++)
            {
                var m = grid.Markers[mi];
                if (m.IsTerminal)
                    AppLogger.Log("Save", $"  Marker {mi}: pos={m.PositionSeconds:F3}s, BPM={m.Bpm:F2} (terminal)");
                else
                    AppLogger.Log("Save", $"  Marker {mi}: pos={m.PositionSeconds:F3}s, beatsUntilNext={m.BeatsUntilNext}");
            }

            await Task.Run(() => _seratoTagService.WriteBeatGrid(SelectedTrack.FilePath, grid));
            await Task.Run(() => _seratoTagService.SetBpmLock(SelectedTrack.FilePath, true));
            SelectedTrack.HasExistingBeatGrid = true;
            WithBeatGridCount = Tracks.Count(t => t.HasExistingBeatGrid);
            AppLogger.Log("Save", "  Save successful (BPMLOCK set)");
            StatusMessage = $"Saved beatgrid to {SelectedTrack.FileName}";
        }
        catch (Exception ex)
        {
            AppLogger.Log("Save", $"  Save FAILED: {ex}");
            StatusMessage = $"Error saving: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        if (SelectedTrack?.BeatGrid == null)
        {
            StatusMessage = "No beatgrid to export. Analyze the track first.";
            return;
        }

        try
        {
            var path = Path.ChangeExtension(SelectedTrack.FilePath, ".beatgrid.json");
            await _jsonExportService.ExportAsync(SelectedTrack, path);
            StatusMessage = $"Exported JSON to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error exporting: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadExistingBeatGridAsync()
    {
        if (SelectedTrack == null || !SelectedTrack.HasExistingBeatGrid)
        {
            StatusMessage = "No existing Serato beatgrid found in this file.";
            return;
        }

        try
        {
            var beatGrid = _seratoTagService.ReadBeatGrid(SelectedTrack.FilePath);
            if (beatGrid != null)
            {
                beatGrid.ExpandBeats(SelectedTrack.Duration.TotalSeconds);
                SelectedTrack.BeatGrid = beatGrid;
                SelectedTrack.AnalysisStatus = AnalysisStatus.Analyzed;

                WaveformEditor.LoadTrack(SelectedTrack);
                await Playback.LoadTrackAsync(SelectedTrack.FilePath, beatGrid);

                var markerCount = beatGrid.Markers.Count;
                var bpm = beatGrid.Markers.LastOrDefault()?.Bpm;
                StatusMessage = $"Loaded existing beatgrid: {markerCount} marker(s), {bpm:F1} BPM, {beatGrid.AllBeatPositions.Count} beats";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error reading beatgrid: {ex.Message}";
        }
    }

    private static void ValidateBeatGridAccuracy(BeatGrid grid, AnalysisResult result)
    {
        if (grid.Markers.Count < 2 || result.Beats.Length < 2)
            return;

        var beats = result.Beats;
        int beatIdx = 0;
        double overallMaxDriftMs = 0;
        int worstMarker = 0;

        for (int mi = 0; mi < grid.Markers.Count - 1; mi++)
        {
            var marker = grid.Markers[mi];
            var next = grid.Markers[mi + 1];
            int n = marker.BeatsUntilNext ?? 0;
            if (n <= 0) continue;

            double markerPos = marker.PositionSeconds;
            double nextPos = next.PositionSeconds;
            double interval = (nextPos - markerPos) / n;
            double implicitBpm = 60.0 / interval;

            double maxDriftMs = 0;
            for (int k = 0; k < n && beatIdx + k < beats.Length; k++)
            {
                double expected = markerPos + k * interval;
                double actual = beats[beatIdx + k];
                double driftMs = Math.Abs(actual - expected) * 1000;
                if (driftMs > maxDriftMs)
                    maxDriftMs = driftMs;
            }

            if (maxDriftMs > overallMaxDriftMs)
            {
                overallMaxDriftMs = maxDriftMs;
                worstMarker = mi;
            }

            if (maxDriftMs > 15)  // Only log markers with notable drift
                AppLogger.Log("Validation", $"  Marker {mi}: {implicitBpm:F2} BPM, {n} beats, max drift={maxDriftMs:F1}ms");

            beatIdx += n;
        }

        AppLogger.Log("Validation", $"  Overall max drift: {overallMaxDriftMs:F1}ms at marker {worstMarker}");
    }

    private static BeatGrid ConvertToBeatGrid(AnalysisResult result)
    {
        var grid = new BeatGrid();

        if (result.TempoSegments.Length == 0)
            return grid;

        if (result.TempoSegments.Length == 1)
        {
            // Constant tempo: single terminal marker
            var seg = result.TempoSegments[0];
            grid.Markers.Add(new BeatGridMarker
            {
                PositionSeconds = seg.StartPosition,
                Bpm = seg.Bpm,
            });
        }
        else
        {
            // Variable tempo: non-terminal markers + terminal
            for (int i = 0; i < result.TempoSegments.Length - 1; i++)
            {
                var seg = result.TempoSegments[i];
                grid.Markers.Add(new BeatGridMarker
                {
                    PositionSeconds = seg.StartPosition,
                    BeatsUntilNext = seg.BeatCount,
                });
            }

            var last = result.TempoSegments[^1];
            grid.Markers.Add(new BeatGridMarker
            {
                PositionSeconds = last.StartPosition,
                Bpm = last.Bpm,
            });
        }

        grid.AllBeatPositions = result.Beats.Select(b => (double)b).ToList();
        return grid;
    }
}
